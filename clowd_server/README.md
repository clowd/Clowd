# clwd.app — streaming upload relay (Cloudflare Workers)

A single Cloudflare Worker (Rust → WASM, via [`workers-rs`](https://github.com/cloudflare/workers-rs))
that hands out a shareable download URL **before** an upload finishes, live-tails the bytes to
recipients as they arrive, relays them to the user's own Azure Blob / S3-compatible bucket, and
then `301`s to the final destination. It supersedes the legacy C#/ASP.NET Core docker server
that previously lived in this folder (deleted; git history has it); see `REFACTOR.md` for the
full spec.

- **Worker `fetch`** — router (`/`, `/healthz`), control plane (`/api/v1/uploads…`), and the
  live-tail `GET /u/{id}`.
- **Durable Object `UploadSession`** — one per upload: manifest, capability tokens, status, an
  in-memory wakeup list for tailing readers, and alarms (10-min idle timeout, 60-s post-complete
  linger cleanup).
- **Queue `RELAY` (+ `clowd-relay-dlq`)** — relays each staged chunk to the destination
  (Azure `Put Block` / S3 `UploadPart`); DLQ marks the session failed.
- **R2 `STAGING`** — transient chunk objects `{id}/{n:05}`.
- **KV `REDIRECTS`** — write-once `{finalUrl,length,completedUtc}` for completed uploads.

---

## 1. Prerequisites

- **Rust** with the wasm target:
  ```sh
  rustup target add wasm32-unknown-unknown
  ```
- **worker-build** (compiles the crate + emits the JS shim; installed once, *not* in the build
  command):
  ```sh
  cargo install worker-build --locked
  ```
- **Node** 20+ and the pinned wrangler devDependency:
  ```sh
  npm install
  ```
- A **Cloudflare account on the Workers Paid plan** ($5/mo — required for Durable Objects,
  Queues, and the 5-minute CPU limit).

---

## 2. One-time Cloudflare setup (~15 min)

1. **Add the `clwd.app` zone** in the Cloudflare dashboard, then point your registrar's
   nameservers at the two Cloudflare nameservers it shows. Wait until the zone is **Active**
   (the custom-domain route in `wrangler.jsonc` provisions DNS + TLS automatically on deploy).

2. **Authenticate wrangler** (interactive, or set `CLOUDFLARE_API_TOKEN` for CI):
   ```sh
   npx wrangler login
   ```

3. **Create the resources:**
   ```sh
   # R2 staging bucket
   npx wrangler r2 bucket create clowd-staging

   # KV namespace for completed-upload redirects — copy the printed id
   npx wrangler kv namespace create REDIRECTS

   # Relay queue + its dead-letter queue
   npx wrangler queues create clowd-relay
   npx wrangler queues create clowd-relay-dlq
   ```

4. **R2 lifecycle backstop — delete staged chunks older than 2 days** (crashed-session cleanup;
   R2 lifecycle granularity is days):
   ```sh
   npx wrangler r2 bucket lifecycle add clowd-staging --name expire-staging --expire-days 2
   ```
   (If your wrangler version differs, run `npx wrangler r2 bucket lifecycle --help`, or add the
   rule in the dashboard: R2 → clowd-staging → Settings → Object lifecycle rules → delete after
   2 days.)

5. **Check the KV namespace id** in `wrangler.jsonc` matches the id printed in step 3.
   The production id is committed (KV namespace ids are not secrets); only replace it if
   you recreated the namespace or are deploying to a different account:
   ```jsonc
   "kv_namespaces": [ { "binding": "REDIRECTS", "id": "<the id from step 3>" } ],
   ```

---

## 3. Local development — full offline stack

`wrangler dev` runs the real Workers runtime (workerd) locally with Miniflare emulation of R2,
KV, Queues, and Durable Objects **including alarms** — the entire flow works with no cloud
account. State persists across restarts under `.wrangler-state`.

```sh
npm run dev          # → http://localhost:8787
```

`.dev.vars` sets `DEV_ALLOW_DISCARD=true`, which enables the **`discard`** destination (relays to
nowhere, commit is a no-op) so you can exercise everything end to end without a real bucket. This
flag is deliberately absent from `wrangler.jsonc`, so production never enables it.

### Exercise the whole flow with curl

```sh
BASE=http://localhost:8787

# 1) Create a session (discard destination — dev only).
resp=$(curl -s -X POST "$BASE/api/v1/uploads" \
  -H 'content-type: application/json' \
  -d '{
        "fileName":"hello.txt",
        "contentType":"text/plain",
        "contentLength":11,
        "destination":{"type":"discard","finalUrl":"https://example.com/hello.txt"}
      }')
echo "$resp"
ID=$(echo "$resp"        | sed -E 's/.*"id":"([^"]+)".*/\1/')
TOKEN=$(echo "$resp"     | sed -E 's/.*"uploadToken":"([^"]+)".*/\1/')
# chunkCount here is 1 (11 bytes < the 5 MiB minimum chunk).

# 2) (Optional) open a live tail BEFORE uploading — headers come back immediately,
#    the body streams as chunks arrive. Run this in a second terminal:
curl -sN "$BASE/u/$ID"

# 3) Upload chunk 0 (raw bytes, bearer token).
curl -s -X PUT "$BASE/api/v1/uploads/$ID/chunks/0" \
  -H "authorization: Bearer $TOKEN" \
  --data-binary 'hello world'
# → {"received":0}

# 4) Complete — commits the destination, writes KV, returns finalUrl + length.
curl -s -X POST "$BASE/api/v1/uploads/$ID/complete" \
  -H "authorization: Bearer $TOKEN"
# → {"finalUrl":"https://example.com/hello.txt","length":11}

# 5) The download URL is now a permanent redirect.
curl -si "$BASE/u/$ID" | grep -i '^location'
# → location: https://example.com/hello.txt
```

Other routes: `POST /api/v1/uploads/{id}/abort` (bearer uploadToken) and
`DELETE /api/v1/uploads/{id}` (bearer **deleteToken** — removes the short link so `/u/{id}` 404s).

For **larger, multi-chunk** files, split the file into `chunkSize`-byte pieces and `PUT` them to
`/chunks/0`, `/chunks/1`, … sequentially; every chunk is exactly `chunkSize` except the last.

Smoke-test against real R2/Queues before deploying:

```sh
npm run dev:remote
```

---

## 4. Running tests

> **[TESTING.md](TESTING.md)** covers the full testing ladder — including driving a real
> Clowd desktop client through a locally-running worker — in more detail.

Pure logic (id/token validation, chunk-plan math, the manifest state machine, Azure block-list
XML, S3 complete XML, destination URL construction/validation) is host-testable:

```sh
cargo test                                   # native unit tests
```

Full-surface checks (what CI should gate on):

```sh
cargo fmt --check
cargo check  --target wasm32-unknown-unknown
cargo clippy --target wasm32-unknown-unknown -- -D warnings
cargo test
```

End-to-end smoke test (the full lifecycle against a local `wrangler dev`: create → chunked
upload → **mid-upload live tail** with Content-Length + byte-exact content → complete → 301
→ auth rejection → delete → 404). Start `npm run dev` in one terminal, then:

```sh
npm run e2e                                  # or E2E_BASE=http://127.0.0.1:xxxx npm run e2e
```

---

## 5. Deploy

The usual path is CI: run the **Deploy server** workflow from the GitHub Actions tab
(manual dispatch — `.github/workflows/deploy-server.yml`). It gates on fmt/clippy/tests
plus the offline e2e suite, deploys with the `CLOUDFLARE_API_TOKEN` /
`CLOUDFLARE_ACCOUNT_ID` repository secrets, and verifies `/healthz` afterwards.

To deploy from your machine instead:

```sh
npm run deploy        # worker-build --release, then wrangler deploy
```

The custom-domain route provisions DNS + a TLS cert for `clwd.app` automatically. Verify:

```sh
curl -si https://clwd.app/healthz          # → 200 {"ok":true}
curl -si https://clwd.app/                 # → 301 github.com/clowd/Clowd
```

Tail production logs with `npm run tail`.

---

## 6. Configuration knobs

| Where | Knob | Default | Notes |
|---|---|---|---|
| `wrangler.jsonc` | `limits.cpu_ms` | `300000` | 5-min CPU cap (I/O wait is free; live tails aren't CPU-bound). |
| `wrangler.jsonc` | queue `max_batch_size` / `max_batch_timeout` / `max_retries` | 5 / 1s / 5 | Relay batching + retries before the DLQ. |
| `.dev.vars` | `DEV_ALLOW_DISCARD` | `true` (dev only) | Enables the `discard` destination. Never set in production. |
| `src/consts.rs` | `IDLE_TIMEOUT_MS` | 10 min | No chunk received → session failed (DO alarm). |
| `src/consts.rs` | `LINGER_MS` | 60 s | Post-complete/abort staging + DO-storage cleanup delay. |
| `src/consts.rs` | `REDIRECT_MAX_AGE_SECS` | 3600 | `Cache-Control` on the completed-upload 301. |
| `src/chunkplan.rs` | `MAX_UPLOAD_BYTES` | 10 GiB | Hard per-upload cap. |
| `src/chunkplan.rs` | chunk-size band | 5–32 MiB (default 16 MiB) | Clamped at create time; 16 MiB floor for files > 5 GiB. |

### Destinations

The server only ever receives **capability URLs**, never account keys (unauthenticated create):

- **`azure-blob`** — client-supplied blob-level SAS URL (`create+write`):
  `{"type":"azure-blob","sasUrl":"https://…?sv=…&sig=…","customDomain":"files.example.com"}`
  (`customDomain`/`finalUrl` optional). Relay = `Put Block`; commit = `Put Block List`.
- **`s3-multipart`** — client presigns everything:
  `{"type":"s3-multipart","partUrls":["…","…"],"completeUrl":"…","abortUrl":"…","finalUrl":"…"}`
  (`partUrls` length must equal `chunkCount`). Relay = `UploadPart`; commit = `CompleteMultipartUpload`.
- **`discard`** — dev/local only (see `DEV_ALLOW_DISCARD`).

All destination URLs must be `https`.

---

## 7. Known gaps

- **Real Azure/S3 destinations have not been exercised live** — the e2e suite runs the full
  lifecycle (create → chunked upload → mid-upload live tail → complete → 301 → delete)
  against the `discard` destination only. Watch the first real uploads with `npm run tail`.
- **No rate limiting on the unauthenticated create endpoint.** Add a Cloudflare WAF rate
  rule on `POST /api/v1/uploads` before publicizing the endpoint. Related: any https URL is
  accepted as a destination/`finalUrl` (inherent open-redirect surface of a public
  shortener); a destination-host allowlist knob is future work.
- **Zero-byte files fail when accelerated** (client plans 1 chunk, server plans 0) — the
  client should fall back to the direct provider path for empty files.
- **No `Range`/resume on live tails** (`Accept-Ranges: none`); a severed tail must restart.
- Chunk PUT/relay bodies are buffered per-operation (≤ 32 MiB) inside the session's Durable
  Object; fine at the default 16 MiB chunks, but a pathological number of concurrent
  operations on one upload pressures the 128 MB isolate limit. Tails are pass-through
  streamed and unaffected.
- A destination-commit failure leaves staging cleanup to the idle alarm / 2-day R2
  lifecycle instead of cleaning up immediately; transient DO storage read errors are
  reported as "not found" rather than 500/retry.
