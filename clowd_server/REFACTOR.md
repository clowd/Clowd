# clowd_server → Cloudflare Workers refactor

Status: **implemented** (worker + C# client integration, 2026-07-12; deploy guide in README.md — real Azure/S3 destinations not yet exercised live, see README "known gaps")
Replaces: the C#/ASP.NET Core docker server in this folder (`Clowd.Server`, `Clowd.Server.Tests`)
Target platform: **Cloudflare Workers + R2 + KV + Queues + Durable Objects**, domain **clwd.app**
Implementation language: **Rust** (`workers-rs`, compiled to WASM)

---

## 1. Goal

Clowd uploads can optionally be routed through a server that:

1. Returns a shareable download URL (`https://clwd.app/u/{id}`) **immediately**, before the upload finishes.
2. Stages the bytes while the upload is in progress and **relays them to their final destination** (the user's own Azure Blob or S3-compatible bucket — clwd.app never permanently hosts user data).
3. Serves downloads that arrive **mid-upload** in real time ("live tail"), streaming bytes as they become available. Must work for files **larger than 200 MB** (OBS video recordings).
4. Once the upload is fully committed to its destination, returns **301 → final destination URL** and deletes all staged data.

The docker implementation in this folder already does all of this (see `Program.cs`,
`Uploads/UploadService.cs`, `Uploads/DownloadStreamer.cs`, `Redirects/RedirectStore.cs`) but
requires an always-on container + persistent disk. This refactor ports the same
protocol/semantics to serverless infrastructure with ~zero idle cost.

### Decisions already made

| Decision | Choice | Rationale |
|---|---|---|
| Destinations | **External only: Azure Blob + S3-compatible** | clwd.app accelerates access, does not host. The other 5 legacy providers (imgur/catbox/picsur/vgy.me/hastebin) stay client-direct only. |
| Auth on upload creation | **Unauthenticated** | No stored data; cost exposure is bandwidth only (and R2 egress is free). Per-upload capability tokens still protect mutation of a session (parity with old server). |
| Live tail for >200 MB files | **Required** | Rules out AWS Lambda (200 MB response cap, 2 MB/s throttle) and Azure Functions (hard 230 s HTTP response limit). |
| Cold start | **~0 required** | Rules out Fargate (30–60 s) and Azure Container Apps (15–30 s, queued). Workers isolates start in ~5 ms. |
| Language | **Rust** (no Node) | `workers-rs` is Cloudflare's official Rust→WASM toolchain. |
| IaC | **wrangler** (native config), not CDK/Pulumi | See §8. |

### Why Cloudflare (platform comparison summary)

| | AWS | Azure | Cloudflare |
|---|---|---|---|
| Serverless streaming download | Lambda: 200 MB cap, 2 MB/s after 6 MB ([limits](https://docs.aws.amazon.com/lambda/latest/dg/configuration-response-streaming.html)) | Functions: hard 230 s HTTP ceiling (LB idle timeout) | Workers: **no duration limit, no response size limit**; only CPU-time metered (30 s default → 5 min paid), I/O wait free ([limits](https://developers.cloudflare.com/workers/platform/limits/)) |
| Container fallback | Fargate: 30–60 s cold or ~$9/mo warm | Container Apps: 15–30 s cold (request queued) or min-replica cost | not needed |
| Egress pricing | ~$0.085/GB (CloudFront) | ~$0.08/GB | **R2 egress $0** |
| Per-object redirects | S3 `x-amz-website-redirect-location` | none | none — replaced by Worker + KV (§4.1) |
| Idle cost | ~$0.50/mo | ~$0 | $5/mo flat (Workers Paid, required for DO/Queues/5-min CPU) |

Note: R2 has **no** equivalent of S3's `x-amz-website-redirect-location` / static website
hosting. That's fine — a Worker consulting KV is strictly more capable (status code control,
instant deletes, no public bucket).

---

## 2. Architecture overview

One Worker script (`clowd-server`) exports a `fetch` handler (router + API + live tail) and a
`queue` handler (destination relay). One Durable Object class holds per-upload session state.

```
https://clwd.app  (Worker custom domain, TLS + DNS automatic)
 │
 ├─ GET /                     → 301 https://github.com/clowd/Clowd   (in-worker, no rule needed)
 ├─ GET /healthz              → 200
 │
 ├─ GET /u/{id}               → 1. KV hit  → 301 {finalUrl}          (completed upload, common case)
 │                              2. KV miss → ask Session DO:
 │                                   uploading  → live-tail stream from R2 chunks (via DO, §5)
 │                                   completed  → 301 (KV write still propagating)
 │                                   failed     → 410 Gone
 │                                   unknown    → 404
 │
 └─ /api/v1/* (control plane)
     POST   /uploads                        create session            → id, downloadUrl, uploadToken
     PUT    /uploads/{id}/chunks/{n}        chunk bytes → R2 staging, notify DO, enqueue relay
     POST   /uploads/{id}/complete          commit destination, write KV, schedule cleanup
     POST   /uploads/{id}/abort             abandon + cleanup
     DELETE /uploads/{id}                   remove short link (deleteToken)

 Bindings:
   KV  "REDIRECTS"     id → { finalUrl }            write-once at completion
   R2  "STAGING"       {id}/{n:05} chunk objects    transient, lifecycle-deleted
   DO  "SESSIONS"      UploadSession                manifest, status, relay progress, tail wakeups, alarms
   Q   "RELAY"         { uploadId, chunkNo }        producer + consumer in same script

 Relay consumer (per queue message):
   R2 GET chunk ──▶ Azure `Put Block` (blob SAS)  or  S3 `UploadPart` (presigned URL)
                └─▶ report block-id/ETag to Session DO
```

Data flow for one upload:

```
Clowd client                        clwd.app Worker                  Destination (user's bucket)
────────────                        ───────────────                  ───────────────────────────
POST /api/v1/uploads ─────────────▶ create DO session
  {file meta, destination caps} ◀── {id, downloadUrl, uploadToken}
                                     ── link is shareable NOW ──
PUT chunks/0..N (16 MiB each) ────▶ R2 staging + DO notify ──queue──▶ Put Block / UploadPart
POST complete ────────────────────▶ verify all relayed
                                    Put Block List / CompleteMultipartUpload ──▶ committed
                                    KV["u/{id}"] = finalUrl
                              ◀──── {finalUrl, length}
                                    (60 s linger) delete R2 staging, DO storage
```

Why the tail is *not* a browser redirect to a second worker/URL: `/u/{id}` must be the one
canonical URL for the file's whole lifetime. Bouncing in-progress viewers to
`tail.clwd.app/...` leaks a second URL that dies after completion (bookmarks, chat previews,
download managers). Since router, API and tail live in one script (Durable Object reachable
in-process), no second public endpoint is needed.

---

## 3. Storage & state

### 3.1 KV namespace `REDIRECTS` — completed uploads only

- Key `{id}`, value `{"finalUrl": "...", "length": n, "completedUtc": "..."}`.
- Written exactly once, at completion. KV is **eventually consistent (up to ~60 s
  cross-edge)** — acceptable only because the value never changes after the single write.
  The freshly-completed race (KV miss right after completion) falls through to the Session
  DO, which is strongly consistent and answers 301 itself.
- `DELETE /uploads/{id}` removes the key (upload delete feature — `UploadRecord.CanDelete`
  in the client). Deleting the destination object itself remains client-side (client holds
  the credentials; server never does).

### 3.2 R2 bucket `clowd-staging` — transient chunks

- Objects `{id}/{n:05}` (e.g. `aj20lajk/00017`), one per chunk, plain single PUTs
  (no R2 multipart needed — chunks are already ≤ 32 MiB).
- Deleted explicitly 60 s after completion/abort (linger lets active tails finish —
  parity with old `FinishedLinger`).
- **Lifecycle rule: delete objects older than 2 days** as backstop for crashed sessions
  (parity with old `SweepOrphans` 24 h; R2 lifecycle granularity is days).
- Bucket location hint: pick nearest region to primary users (single-region is fine;
  R2→edge transfer is internal).

### 3.3 Durable Object `UploadSession` — one per upload id

Authoritative session state (replaces `UploadRegistry` + `UploadSession` + the tailing
signal in the old server):

- Metadata: file name, content type, declared length, chunk size/count, created time.
- Capability tokens: `uploadToken` (32 random bytes, constant-time compare — parity with
  `UploadRegistry`/`TokenMatches`), `deleteToken`.
- Destination descriptor (capability URLs only, §6).
- Chunk manifest: which chunks are staged in R2; which are relayed (+ block id / ETag).
- Status: `uploading | committing | complete | failed | aborted`.
- In-memory wakeup list for tailing readers (replaces `WaitForDataAsync`).
- **Alarm**: idle timeout — no chunk received for 10 min (parity `UploadIdleTimeout`)
  → fail session, abort destination, delete staging. Also drives the post-completion
  linger cleanup.

DO storage is deleted at cleanup; the only long-lived state anywhere is the tiny KV entry.

---

## 4. HTTP protocol (v2)

Field naming/JSON casing should match the old DTOs (`Api/Dto.cs`) where concepts carry over.

### 4.1 `POST /api/v1/uploads` — create (unauthenticated)

```jsonc
// request
{
  "fileName": "recording.mp4",
  "contentType": "video/mp4",
  "contentLength": 734003200,          // REQUIRED in v2 (client always knows; enables
                                       // Content-Length on tails + exact chunk plan)
  "chunkSize": 16777216,               // optional; server clamps to [5 MiB, 32 MiB]
  "destination": { ... }               // §6 — capability URLs only, never account keys
}
// response 201
{
  "id": "aj20lajk",                    // 12-byte url-safe (parity with UploadRegistry)
  "downloadUrl": "https://clwd.app/u/aj20lajk",
  "uploadToken": "…",                  // bearer for all mutations below
  "deleteToken": "…",                  // for DELETE (client stores in UploadRecord.DeleteKey)
  "chunkSize": 16777216,
  "chunkCount": 44,
  "finalUrl": "https://…"              // known up front (derived from destination caps)
}
```

Server-enforced caps: `contentLength ≤ 10 GiB` (parity `MaxUploadBytes`), chunk count
sanity, destination URL allow-listing (https only).

### 4.2 `PUT /api/v1/uploads/{id}/chunks/{n}` — upload a chunk

`Authorization: Bearer {uploadToken}`, body = raw chunk bytes (all chunks `chunkSize` long
except the last). The worker streams the body into R2 (`STAGING.put`), notifies the DO
(manifest + wake tails), and enqueues a relay message. Idempotent: re-PUT of an existing
chunk overwrites and is not re-relayed if already relayed. Response `200 {"received": n}`.

Chunks go *through* the worker (not presigned-direct-to-R2) in v1: it removes SigV4
presigning code, R2 API-token secrets, and a separate notify round-trip — one request does
store+notify+enqueue. Worker request body limits (100 MB on Free/Pro plans) comfortably
exceed the 32 MiB max chunk. CPU cost of piping a body to R2 is negligible. If upload
throughput ever demands it, switch to presigned R2 PUT URLs (S3-compatible,
[docs](https://developers.cloudflare.com/r2/api/s3/presigned-urls/)) + a separate notify
call — the protocol reserves this by keeping chunk upload addressable per-chunk.

Client should upload chunks **sequentially** (or ≤2 in flight): live tail serves in order,
so sequential order maximizes what tails can show; it also matches the old single-stream
behavior for progress reporting.

### 4.3 `POST /api/v1/uploads/{id}/complete`

Token-authed. Server verifies every chunk is staged **and** relayed (waits briefly for the
relay queue to drain; verifies against R2 `list` — the client is trusted with its own token
but not blindly), then commits:

- Azure: `Put Block List` (ordered block ids).
- S3: `CompleteMultipartUpload` via the client-presigned URL, XML body from collected ETags.

Then writes KV, marks DO `complete`, sets the 60 s linger alarm for staging deletion, and
returns `{"finalUrl": "…", "length": n}` (parity with `UploadCompleteResponse`).
Idempotent — safe to retry.

### 4.4 `POST /abort`, `DELETE /uploads/{id}`

- Abort (uploadToken): mark failed, S3 `AbortMultipartUpload` via presigned URL; Azure
  uncommitted blocks are garbage-collected by Azure itself in ~7 days (same reasoning as
  `AzureBlobDestination.AbortAsync`); delete staging; active tails are severed (not
  cleanly ended — parity with old fail semantics).
- Delete (deleteToken): remove KV entry → `/u/{id}` becomes 404. Destination object
  deletion stays client-side.

### 4.5 `GET /u/{id}` — the download URL

1. KV hit → `301 Location: finalUrl`, `Cache-Control: public, max-age=3600` (edge-cacheable;
   the mapping is immutable — mirrors the old write-once `RedirectStore` ordering guarantee).
2. KV miss → DO lookup:
   - `uploading | committing` → **live tail** (§5).
   - `complete` (KV not yet propagated) → 301.
   - `failed/aborted` → `410 Gone` (parity with old `/d/{id}`).
   - no such session → 404.

Old `/d/{id}` route → new `/u/{id}` (user-chosen URL shape `clwd.app/u/…`).

---

## 5. Live tail mechanics

The tail response is generated **inside the Session DO's fetch handler** (router forwards
via the DO stub). Rationale:

- The manifest and "new chunk" events are in-memory there — tails wake instantly on chunk
  arrival, no polling, no KV eventual-consistency hazards.
- A DO request has its own subrequest budget (1000), so a 10 GiB / 32 MiB = 320-chunk file
  costs ~320 R2 GETs — well within limits. Chunk size floor of 16 MiB for files > 5 GiB
  keeps this true (server picks/clamps `chunkSize` accordingly at create time).
- Known ceiling: all tailers of one upload stream through one DO instance (single isolate,
  one location). For the realistic case (a handful of viewers per link) this is fine;
  a stateless-worker tail reading R2 + long-polling the DO is the escape hatch if ever
  needed.

Response details:

- Headers: `Content-Length` (declared length — real browser progress bars),
  `Content-Type`, `Content-Disposition: inline; filename=…`, `Cache-Control: no-store`,
  `Accept-Ranges: none` (v1; Range/resume is a listed follow-up).
- Body: for chunk `0..N`: if staged → stream R2 object into the response; else await DO
  wakeup (with idle-timeout guard). Use pass-through piping (no per-byte work in
  Rust/WASM) so CPU-time stays milliseconds even for multi-GB tails — wall-clock is
  unlimited while the client stays connected; CPU limit configured to the 5-min max
  ([changelog](https://developers.cloudflare.com/changelog/post/2025-03-25-higher-cpu-limits/)).
- Upload fails mid-tail → sever the stream (cancel/error the writable side), never a
  clean EOF on partial data — parity with `DownloadStreamer`/`UploadFailedException`.
- Throughput reality check: a live tail can never outrun the uploader's upstream link,
  so per-viewer bandwidth is modest by construction; full-speed downloads happen post-301
  from the destination.

---

## 6. Destination adapters — capability URLs only

Design rule (improvement over the old server, which accepted raw `Credentials`
dictionaries): **the server never receives account keys**, only narrowly-scoped,
expiring capability URLs minted by the client. Important given unauthenticated creation.

### Azure Blob (`type: "azure-blob"`)

Client sends a **blob-level SAS URL** with `create+write` permissions (the client already
knows account/container/blob naming — see `AzureUploadProvider`; the old server used a
container SAS, `Creds.cs`):

- Relay: `Put Block` per chunk, block id = base64 of zero-padded chunk number.
- Commit: `Put Block List` in chunk order.
- `finalUrl`: SAS URL minus query string, or client-supplied override (custom domain —
  parity with `AzureUploadProvider` custom-domain config).
- Optimization (later): `Put Block From URL` with a presigned R2 GET — Azure pulls the
  chunk directly from R2, zero bytes through the worker.

### S3-compatible (`type: "s3-multipart"`)

Client (which owns the credentials — see `S3UploadProvider`: AWS, R2, MinIO, Wasabi, B2)
performs `CreateMultipartUpload` itself, then presigns and sends:

```jsonc
{
  "type": "s3-multipart",
  "partUrls": ["https://…partNumber=1&…", "…"],   // one per chunk, UNSIGNED-PAYLOAD
  "completeUrl": "https://…",                      // presigned CompleteMultipartUpload
  "abortUrl": "https://…",                         // presigned AbortMultipartUpload
  "finalUrl": "https://…"
}
```

- Relay: `PUT partUrls[n]` per chunk; collect ETags in the DO.
- Commit: POST `completeUrl` with the standard ETag XML body.
- Constraint: chunk size ≥ 5 MiB (S3 part minimum, all but last) — hence the server-side
  clamp in §4.1.

### Relay queue consumer

Message `{uploadId, chunkNo}`; consumer fetches the destination descriptor from the DO,
streams R2 chunk → destination PUT, reports success/block-id/ETag to the DO. Queues gives
at-least-once + retries with backoff; relay operations are idempotent (fixed block
ids/part numbers). After max retries → DLQ → consumer marks the session failed (tails
sever, uploader's next call gets an error — parity with old fail-fast behavior). Batch
size 1–5, `max_batch_timeout` ≤ 1 s so relay lag stays low.

---

## 7. Lifecycle summary (parity table)

| Old (`ServerOptions`/`SessionCleanupService`) | New |
|---|---|
| `MaxUploadBytes` 10 GiB | same, enforced at create + cumulative chunk check |
| `UploadIdleTimeout` 10 min | DO alarm since last chunk |
| `FinishedLinger` 60 s before cache delete | DO alarm after complete → delete staging + DO storage |
| Orphan sweep 24 h (`SweepOrphans`) | R2 lifecycle rule, 2 days |
| 32-byte token, `FixedTimeEquals` | same (constant-time compare) |
| id regex `^[A-Za-z0-9_-]{8,64}$` (path-traversal guard) | same validation on `/u/{id}` and API routes |
| 301 persisted **before** cache-serving stops | KV written before staging linger starts; DO answers 301 during KV propagation |

Abuse tripwires (unauthenticated create): per-IP create rate limit (Workers rate-limiting
binding or WAF free rules), destination host allow/deny knobs, global concurrent-session
cap in a small DO or KV counter. Cheap to add in Phase 4; not blocking.

---

## 8. IaC, local dev, deployment (the "CDK equivalent" answer)

Cloudflare has no first-party CDK. The ecosystem options are **Pulumi** (real-language IaC,
C# supported), **Terraform/OpenTofu** (official provider), **Alchemy** (community
TypeScript-native IaC), and **wrangler** (Cloudflare's native CLI + per-worker config).

**Decision: wrangler only.** This stack is one worker script + a handful of resources, all
of which wrangler declares in `wrangler.jsonc` (checked into this folder) or creates with
one-time CLI commands. Pulumi/Terraform would add a state backend and a second toolchain to
manage ~6 resources; revisit only if this grows (multi-env, WAF rules, many DNS records).

```jsonc
// clowd_server/wrangler.jsonc (sketch)
{
  "name": "clowd-server",
  "main": "build/worker/shim.mjs",            // produced by worker-build (workers-rs)
  "compatibility_date": "2026-07-01",
  "build": { "command": "cargo install -q worker-build && worker-build --release" },
  "routes": [{ "pattern": "clwd.app", "custom_domain": true }],   // DNS + TLS automatic
  "limits": { "cpu_ms": 300000 },             // 5-min CPU cap (paid plan)
  "kv_namespaces": [{ "binding": "REDIRECTS", "id": "…" }],
  "r2_buckets":    [{ "binding": "STAGING", "bucket_name": "clowd-staging" }],
  "durable_objects": { "bindings": [{ "name": "SESSIONS", "class_name": "UploadSession" }] },
  "migrations": [{ "tag": "v1", "new_sqlite_classes": ["UploadSession"] }],
  "queues": {
    "producers": [{ "binding": "RELAY", "queue": "clowd-relay" }],
    "consumers": [{ "queue": "clowd-relay", "max_batch_size": 5, "max_batch_timeout": 1,
                    "max_retries": 5, "dead_letter_queue": "clowd-relay-dlq" }]
  }
}
```

One-time account setup (manual/scripted, ~15 min):

1. Cloudflare account, **Workers Paid** plan ($5/mo — needed for DO, Queues, 5-min CPU).
2. Add `clwd.app` zone; point registrar nameservers at Cloudflare.
3. `wrangler login` (or `CLOUDFLARE_API_TOKEN` for CI).
4. `wrangler r2 bucket create clowd-staging` (+ lifecycle rule),
   `wrangler kv namespace create REDIRECTS`,
   `wrangler queues create clowd-relay && wrangler queues create clowd-relay-dlq`.
5. `wrangler deploy` — the custom-domain route provisions DNS + cert automatically.

### Local development — full offline stack

`wrangler dev` runs the production runtime (**workerd**) locally with **Miniflare
emulation of R2, KV, Queues, and Durable Objects incl. alarms** — the entire feature
(create → chunk PUTs → live tail in a browser → complete → 301) works with no cloud
account, and `--persist-to .wrangler-state` keeps state across restarts. This is
materially better than the AWS story (no localstack equivalent needed).

- Scaffold: `cargo generate cloudflare/workers-rs` (templates include DO + queues).
- Iterate: `wrangler dev` (auto-rebuilds via `worker-build`).
- Integration tests: a Rust test harness that spawns `wrangler dev` and drives the API
  with `reqwest` — direct port of the old xunit suites (`ApiTests.cs`,
  `StreamingTests.cs`, which already cover early-URL, tail-while-uploading, fail-severs-
  tail, 301-after-commit; keep those scenarios as the acceptance list).
- `wrangler dev --remote` to smoke-test against real R2/Queues pre-deploy.
- CI: GitHub Action — `worker-build` + tests + `wrangler deploy` on tag.

### Rust/WASM notes

- Crate type `cdylib`, target `wasm32-unknown-unknown`; `worker` crate with the `queue`
  feature; `#[durable_object]` for `UploadSession`; `getrandom` with `js` feature for
  token generation; RustCrypto `hmac`/`sha2` if/when R2 presigning is added (pure Rust,
  WASM-safe — `ring` is not).
- Keep this folder **out of the root cargo workspace** (own `[workspace]` in
  `clowd_server/Cargo.toml`, plus `exclude = ["clowd_server"]` in the root manifest):
  different target, profile, and dependency constraints than `clowd_capture_wgpu`.

---

## 9. Costs & limits

| Item | Figure |
|---|---|
| Workers Paid plan | $5/mo flat (10 M req, DO, Queues, KV quotas included) |
| R2 staging storage | $0.015/GB-mo — transient (hours), effectively pennies |
| R2 egress (tails, Azure pulls) | **$0** |
| Queues | $0.40/M operations beyond included tier |
| Worker CPU per invocation | 30 s default → **5 min** configured; I/O wait not counted |
| Streaming response duration/size | unlimited while client connected |
| Request body (chunk PUT) | 100 MB (Free/Pro plan) ≫ 32 MiB max chunk |
| Subrequests per invocation | 1000 (paid) → chunk-size floor keeps 10 GiB tails ~320 GETs |
| Max upload | 10 GiB (config; 640 × 16 MiB chunks) |

## 10. Phased delivery

1. **Skeleton + shortener**: workers-rs scaffold, `wrangler dev` loop, `/` → GitHub 301,
   `/healthz`, KV-backed `/u/{id}` 301 path, deploy to clwd.app. *(clwd.app is live)*
2. **Upload + live tail**: create/chunks/complete against a null destination, Session DO,
   R2 staging, tail streaming. Acceptance: browser starts a download mid-upload of a
   1 GiB file locally and receives the full file.
3. **Relay + real destinations**: Queues consumer, Azure `Put Block`/`Put Block List`,
   S3 presigned `UploadPart`/`Complete`, KV write, staging cleanup.
4. **Hardening**: idle-timeout alarms, abort/delete, DLQ handling, caps + rate limits,
   >200 MB remote validation, R2 lifecycle backstop.
5. **Client integration**: new `IUploadProvider` in `clowd_ui/Clowd.Upload`
   ("Clowd Server (clwd.app)") speaking this protocol — chunked PUTs, early
   `downloadUrl` surfaced to the Recent page immediately, `DeleteKey` = `deleteToken`.
6. **Decommission**: delete `Clowd.Server`/`Clowd.Server.Tests` (git history keeps them);
   this doc + README describe the replacement.

## 11. Open questions / follow-ups

- **Range/resume** on tails (`Accept-Ranges`) — post-v1; helps severed tails recover.
- **R2 location hint** — pick when creating the bucket (nearest to primary users).
- **workers-rs streaming ergonomics** — Phase 2 should validate pass-through piping CPU
  cost on a multi-GB tail early; escape hatch is a minimal TS shim for the tail handler
  only (Workers are V8 isolates, not Node — but preference is all-Rust).
- **Multiple tails per upload** — supported by design; sanity-test N=5 concurrent.
- **contentLength unknown** (old server allowed `null`) — v2 requires it; revisit only if
  a streaming-capture-upload use case appears.
- **Destination allow-list** — decide whether to restrict destination hosts (e.g. block
  RFC-1918 / metadata IPs) — SSRF hygiene for the relay fetcher. (Cheap: URL scheme https
  + deny private IP literals; DNS-rebinding is a non-issue for one-shot PUTs to
  user-supplied storage endpoints, but document the stance.)

## 12. References

- Workers limits (duration/CPU/body/subrequests): <https://developers.cloudflare.com/workers/platform/limits/>
- 5-min CPU config: <https://developers.cloudflare.com/changelog/post/2025-03-25-higher-cpu-limits/>
- R2 presigned URLs (S3 API): <https://developers.cloudflare.com/r2/api/s3/presigned-urls/>
- R2 Workers binding API: <https://developers.cloudflare.com/r2/api/workers/workers-api-reference/>
- workers-rs: <https://github.com/cloudflare/workers-rs>
- Queues: <https://developers.cloudflare.com/queues/>
- Durable Objects (alarms, limits): <https://developers.cloudflare.com/durable-objects/>
- Azure Block Blob `Put Block (List)`: <https://learn.microsoft.com/en-us/rest/api/storageservices/put-block>
- Rejected alternatives (for the record): AWS Lambda streaming limits
  (<https://docs.aws.amazon.com/lambda/latest/dg/configuration-response-streaming.html>),
  Azure Functions 230 s HTTP ceiling
  (<https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan>),
  App Runner 120 s request timeout, Fargate/Container Apps cold starts.
