# Testing clowd_server locally

How to exercise the clwd.app upload relay at every level — from pure unit tests to a full
Clowd desktop client uploading through a locally-running worker. Prerequisites (Rust wasm
target, `worker-build`, `npm install`) are in [README.md](README.md) §1.

All commands run from this directory (`clowd_server/`) unless noted.

## 1. Unit tests + static gate

Pure logic (id/token validation, chunk-plan math, the manifest state machine, Azure/S3 XML,
destination URL validation) is host-testable — no wasm toolchain involved:

```sh
cargo test
```

The full static gate — what CI runs before every deploy:

```sh
cargo fmt --check
cargo check  --target wasm32-unknown-unknown
cargo clippy --target wasm32-unknown-unknown -- -D warnings
cargo test
```

## 2. Server end-to-end (fully offline)

`wrangler dev` runs the real Workers runtime locally with Miniflare emulation of R2, KV,
Queues, and Durable Objects (including alarms) — no Cloudflare account or network needed.

```sh
npm run dev          # terminal 1 → http://localhost:8787
npm run e2e          # terminal 2 — full lifecycle smoke test
```

The e2e suite (`e2e/e2e.mjs`) uploads a synthetic 40 MB file against the dev-only `discard`
destination and checks: create → chunked upload → **mid-upload live tail** (Content-Length +
byte-exact body) → complete → 301 redirect → auth rejection → delete → 404.

To poke at individual routes by hand, README §3 has a step-by-step curl walkthrough of the
same flow.

## 3. Client end-to-end (Clowd.Ui → local worker)

This is the closest thing to production: the real desktop client chunking, presigning, and
relaying through a local worker to a **real bucket**.

What you need:

- A real destination bucket. The worker only accepts `https` destination URLs, so the
  `discard` destination is unreachable from the client path — configure a working S3-compatible
  bucket (AWS S3, R2) or Azure Blob container in the provider first and verify a plain
  (non-accelerated) upload succeeds.
- A file that is **not empty** — zero-byte files currently fail when accelerated (client
  plans 1 chunk, server plans 0; see README "Known gaps").

Steps:

1. Start the worker: `npm run dev`.
2. Build and launch the client: `dotnet run --project ../clowd_ui/Clowd.Ui` (or your usual
   IDE flow).
3. In Clowd's settings, open the S3 or Azure upload provider and set:
   - **Accelerate uploads** → on
   - **Accelerate server url** → `http://localhost:8787` (default is `https://clwd.app`)
4. Upload something. You should observe:
   - The share link (`http://localhost:8787/u/{id}`) is available **immediately**, before
     the upload finishes.
   - Request logs streaming in the `wrangler dev` terminal (create → chunk PUTs → queue
     relays → complete).
   - `curl -sN http://localhost:8787/u/{id}` mid-upload live-tails the bytes as they arrive;
     after completion the same URL 301s to the real bucket/CDN URL.
5. Delete the upload from Clowd's upload history — this exercises the accelerated delete
   round trip (provider deletes the blob/object, then removes the clwd.app short link;
   `/u/{id}` should 404 afterwards).

## 4. Remote smoke test (real R2/Queues, pre-deploy)

Runs your local build in Cloudflare's cloud against the real bound resources — the last
check before `npm run deploy`:

```sh
npm run dev:remote
```

Requires `npx wrangler login` (or `CLOUDFLARE_API_TOKEN`) and the one-time resource setup
from README §2.

## 5. C# client tests

The accelerated-upload client logic (chunk-plan math mirroring the server, delete-token
encoding) has its own test project at the repo root:

```sh
dotnet test clowd_ui/Clowd.Shared.Tests
```

The chunk-plan tests must stay in agreement with the server's `src/chunkplan.rs` — if you
change the chunk-size band or count math on one side, change and test both.

## 6. Deploying

Deploys are done from CI: the **Deploy server** workflow in the GitHub Actions tab
(`.github/workflows/deploy-server.yml`, manual dispatch). It runs the full gate from §1
plus the offline e2e from §2, then `wrangler deploy`, then verifies
`https://clwd.app/healthz`. A `skip_tests` input bypasses the gate for emergencies.

Manual deploys (`npm run deploy`) still work and use the same committed `wrangler.jsonc`.
