//! `UploadSession` Durable Object — one instance per upload id.
//!
//! Authoritative per-upload state (replaces the legacy `UploadRegistry` +
//! `UploadSession` + tail signal). Holds the manifest, capability tokens, status,
//! an in-memory wakeup list for tailing readers, and the alarm that drives both
//! the 10-minute idle timeout and the 60-second post-completion linger cleanup.
//!
//! Internal routes (reached only via a DO stub from the Worker / queue consumer):
//! `POST /init`, `PUT /chunk/{n}`, `POST /complete`, `POST /abort`,
//! `POST /del`, `POST /relay/{n}`, `POST /fail`, `GET /tail`.

use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

use async_stream::try_stream;
use futures::channel::oneshot;
use futures::StreamExt;
use worker::durable::DurableObject;
use worker::{durable_object, Env, Error, Method, Request, Response, Result, State};

use crate::chunkplan::{expected_chunk_len, ChunkPlan, MAX_CHUNK_COUNT};
use crate::consts::{IDLE_TIMEOUT_MS, LINGER_MS, PART_URL_HEADER, REDIRECT_MAX_AGE_SECS};
use crate::model::{is_https, CompleteResponse, Destination, SessionState, SessionStatus};
use crate::sanitize;
use crate::wasm_util::{error_json, now_ms};
use crate::{dest, ids, manifest};

const STATE_KEY: &str = "state";
/// Idle guard for a parked tail: re-check storage at least this often even if a
/// wakeup is somehow missed (e.g. cross-eviction).
const TAIL_IDLE_GUARD: Duration = Duration::from_secs(20);
/// How long `/complete` waits for the relay queue to drain before backstopping.
const COMPLETE_WAIT_ITERS: usize = 20;
const COMPLETE_WAIT_STEP: Duration = Duration::from_millis(500);
/// Cap on chunks relayed inline during `/complete` so we stay well within the
/// Durable Object subrequest budget (large files rely on the queue).
const INLINE_RELAY_CAP: usize = 32;

/// Live-tail body generator. The concrete `worker::Error` item type pins error
/// inference for `Response::from_stream`. Streams chunks in order from 0,
/// parking on the DO's wakeup list until each chunk is staged; a terminal
/// failure yields `Err`, which errors the underlying `ReadableStream` and
/// **severs** the connection (never a clean EOF on partial data — parity with
/// `DownloadStreamer`/`UploadFailedException`).
///
/// The end condition comes from `SessionState::stream_end`, re-read from the
/// cache on every wakeup: known-length sessions end at the fixed chunk count;
/// unknown-length ones end once the final chunk index is known AND every chunk
/// `0..=F` has been streamed — while the final index is unknown the tail waits
/// on the notifier exactly like it waits for a missing middle chunk.
fn tail_stream(
    cache: Rc<RefCell<Option<SessionState>>>,
    notifier: Rc<Notifier>,
    bucket: worker::Bucket,
    id: String,
) -> impl futures::Stream<Item = Result<Vec<u8>>> {
    try_stream! {
        let mut n: u64 = 0;
        // The last end bound observed from the cache. If the linger cleanup wipes
        // the cache right as the reader reaches that bound, this turns the wipe
        // into a clean EOF instead of a spurious sever after full delivery.
        let mut known_end: Option<u64> = None;
        'chunks: loop {
            // Park until this chunk is staged (or the session goes terminal).
            loop {
                let rx = notifier.subscribe();
                let (status, staged_n, end) = {
                    let borrow = cache.borrow();
                    match borrow.as_ref() {
                        Some(s) => (
                            s.status,
                            s.staged.get(n as usize).copied().unwrap_or(false),
                            s.stream_end(),
                        ),
                        None => (SessionStatus::Failed, false, known_end),
                    }
                };
                if end.is_some() {
                    known_end = end;
                }

                // End check first (before the sever check) so a zero-chunk upload
                // still ends cleanly — parity with the old for-loop bound.
                if let Some(end) = end {
                    if n >= end {
                        break 'chunks;
                    }
                }
                if manifest::should_sever_active_tail(status) {
                    Err(Error::RustError("upload failed mid-stream".into()))?;
                }
                if staged_n {
                    break;
                }
                if status == SessionStatus::Complete {
                    // Completed but this chunk isn't staged (linger cleanup raced) — sever.
                    Err(Error::RustError("staged chunk unavailable".into()))?;
                }

                // Wait for a wakeup, with an idle guard so we can't hang forever.
                let delay = Box::pin(worker::Delay::from(TAIL_IDLE_GUARD));
                let rx = Box::pin(rx);
                let _ = futures::future::select(rx, delay).await;
            }

            let key = format!("{id}/{n:05}");
            let obj = bucket.get(&key).execute().await?.ok_or_else(|| Error::RustError(format!("chunk {n} missing")))?;
            let body = obj.body().ok_or_else(|| Error::RustError(format!("chunk {n} has no body")))?;
            // Pass-through pipe: yield the R2 object's stream in its native small
            // pieces instead of buffering the whole 16–32 MiB chunk (and letting
            // `Response::from_stream` copy it again). Keeps per-tail memory bounded
            // and avoids memcpy-ing a multi-GB file through WASM linear memory.
            let mut pieces = body.stream()?;
            while let Some(piece) = pieces.next().await {
                yield piece?;
            }

            n += 1;
        }
    }
}

/// In-memory wakeup list — tailing readers park here until a chunk arrives or the
/// session reaches a terminal state (replaces the legacy `WaitForDataAsync`).
#[derive(Default)]
struct Notifier {
    wakers: RefCell<Vec<oneshot::Sender<()>>>,
}

impl Notifier {
    fn subscribe(&self) -> oneshot::Receiver<()> {
        let (tx, rx) = oneshot::channel();
        self.wakers.borrow_mut().push(tx);
        rx
    }

    fn notify(&self) {
        for tx in self.wakers.borrow_mut().drain(..) {
            let _ = tx.send(());
        }
    }
}

#[durable_object]
pub struct UploadSession {
    state: State,
    env: Env,
    /// Cache of the persisted session state (write-through to storage).
    cache: Rc<RefCell<Option<SessionState>>>,
    notifier: Rc<Notifier>,
}

impl DurableObject for UploadSession {
    fn new(state: State, env: Env) -> Self {
        Self {
            state,
            env,
            cache: Rc::new(RefCell::new(None)),
            notifier: Rc::new(Notifier::default()),
        }
    }

    async fn fetch(&self, mut req: Request) -> Result<Response> {
        let method = req.method();
        // Prefer the explicit route header set by `router::forward` (the request
        // URL cannot be rewritten reliably); fall back to the URL path for
        // `do_request` calls (/init, /relay/{n}, /fail, /tail) that build a fresh
        // URL.
        let route = req
            .headers()
            .get(crate::consts::DO_ROUTE_HEADER)
            .ok()
            .flatten()
            .unwrap_or_else(|| req.path());
        let segments: Vec<&str> = route
            .split('/')
            .filter(|s| !s.is_empty())
            .collect();

        match (&method, segments.as_slice()) {
            (Method::Post, ["init"]) => self.init(&mut req).await,
            (Method::Put, ["chunk", n]) => match n.parse::<u64>() {
                Ok(n) => self.chunk(&mut req, n).await,
                Err(_) => error_json("bad chunk number", 400),
            },
            (Method::Post, ["complete"]) => self.complete(&req).await,
            (Method::Post, ["abort"]) => self.abort(&req).await,
            (Method::Delete, ["del"]) => self.delete(&req).await,
            (Method::Post, ["relay", n]) => match n.parse::<u64>() {
                Ok(n) => self.relay(n).await,
                Err(_) => error_json("bad chunk number", 400),
            },
            (Method::Post, ["fail"]) => self.fail_internal().await,
            (Method::Get, ["tail"]) => self.tail().await,
            _ => Response::error("not found", 404),
        }
    }

    async fn alarm(&self) -> Result<Response> {
        let Some(state) = self.load().await? else {
            return Response::empty();
        };

        if matches!(state.status, SessionStatus::Uploading | SessionStatus::Committing) {
            // The alarm is (re)armed on every chunk and on entering /complete, but
            // it can still fire while a chunk PUT or the /complete drain+commit is
            // in flight. Only fail if the session is *genuinely* idle; otherwise
            // re-arm for the remaining window so we don't abort a live commit
            // mid-flight and then have /complete overwrite Failed with Complete.
            let idle_ms = now_ms() - state.last_activity_ms;
            if idle_ms < IDLE_TIMEOUT_MS as f64 {
                let remaining = IDLE_TIMEOUT_MS - idle_ms as i64;
                self.set_alarm_in(remaining.max(1)).await?;
                return Response::empty();
            }
            // Idle timeout: no activity for 10 minutes → fail + abort destination.
            let mut state = state;
            state.status = SessionStatus::Failed;
            self.persist(&state).await?;
            let _ = dest::abort(&state.destination).await;
            self.delete_staging(&state).await;
            self.notifier.notify();
            // Linger, then delete our own storage.
            self.set_alarm_in(LINGER_MS).await?;
        } else {
            // Linger cleanup: drop staging + DO storage.
            self.delete_staging(&state).await;
            self.state.storage().delete_all().await.ok();
            *self.cache.borrow_mut() = None;
        }
        Response::empty()
    }
}

impl UploadSession {
    // --- state helpers ----------------------------------------------------

    async fn load(&self) -> Result<Option<SessionState>> {
        if let Some(s) = self.cache.borrow().as_ref() {
            return Ok(Some(s.clone()));
        }
        let loaded: Option<SessionState> = self
            .state
            .storage()
            .get(STATE_KEY)
            .await
            .ok()
            .flatten();
        *self.cache.borrow_mut() = loaded.clone();
        Ok(loaded)
    }

    async fn persist(&self, state: &SessionState) -> Result<()> {
        *self.cache.borrow_mut() = Some(state.clone());
        self.state
            .storage()
            .put(STATE_KEY, state)
            .await
    }

    async fn set_alarm_in(&self, ms: i64) -> Result<()> {
        self.state
            .storage()
            .set_alarm(Duration::from_millis(ms.max(0) as u64))
            .await
    }

    /// The fixed chunk plan of a known-length session (`None` while an
    /// unknown-length session is still in flight).
    fn plan_of(state: &SessionState) -> Option<ChunkPlan> {
        Some(ChunkPlan {
            chunk_size: state.chunk_size,
            chunk_count: state.chunk_count,
            content_length: state.content_length?,
        })
    }

    /// `?final=1` on the chunk PUT URL marks the final chunk of an
    /// unknown-length upload (`router::forward` preserves the original query).
    fn final_flag(req: &Request) -> bool {
        req.url()
            .ok()
            .map(|u| {
                u.query_pairs()
                    .any(|(k, v)| k == "final" && v == "1")
            })
            .unwrap_or(false)
    }

    fn authed(req: &Request, expected: &str) -> bool {
        let hv = req
            .headers()
            .get("Authorization")
            .ok()
            .flatten();
        match ids::bearer(hv.as_deref()) {
            Some(tok) => ids::token_matches(tok, expected),
            None => false,
        }
    }

    async fn delete_staging(&self, state: &SessionState) {
        let Ok(bucket) = self.env.bucket("STAGING") else {
            return;
        };
        // Unknown-length sessions grow `staged` past the create-time chunk_count
        // (which stays 0 until /complete) — sweep whichever bound is larger.
        let count = state
            .chunk_count
            .max(state.staged.len() as u64);
        for n in 0..count {
            let key = format!("{}/{n:05}", state.id);
            let _ = bucket.delete(&key).await;
        }
    }

    // --- routes -----------------------------------------------------------

    async fn init(&self, req: &mut Request) -> Result<Response> {
        let state: SessionState = req
            .json()
            .await
            .map_err(|e| Error::RustError(format!("bad init payload: {e}")))?;
        self.persist(&state).await?;
        // Arm the idle timeout.
        self.set_alarm_in(IDLE_TIMEOUT_MS).await?;
        Response::empty()
    }

    async fn chunk(&self, req: &mut Request, n: u64) -> Result<Response> {
        // Every rejection before the `req.bytes()` read below must cancel the
        // still-streaming body first, or workerd 503s the response (see
        // `discard_body`).
        let Some(state) = self.load().await? else {
            crate::wasm_util::discard_body(req).await;
            return Response::error("not found", 404);
        };
        if !Self::authed(req, &state.upload_token) {
            crate::wasm_util::discard_body(req).await;
            return Response::error("unauthorized", 401);
        }
        if !manifest::can_accept_chunk(state.status) {
            // terminal session — no more bytes accepted
            crate::wasm_util::discard_body(req).await;
            return Response::error("session is no longer accepting chunks", 409);
        }

        // Unknown-length extras (spec v2 §2): `?final=1` marks the last chunk and
        // s3 chunks carry their presigned UploadPart URL in `x-clowd-part-url`.
        // Both are ignored for known-length sessions.
        let unknown = state.is_unknown_length();
        let is_final = unknown && Self::final_flag(req);
        let part_url = if unknown {
            req.headers()
                .get(PART_URL_HEADER)
                .ok()
                .flatten()
                .filter(|u| !u.is_empty())
        } else {
            None
        };

        if unknown {
            if n >= MAX_CHUNK_COUNT {
                crate::wasm_util::discard_body(req).await;
                return error_json("chunk number out of range", 400);
            }
            if matches!(state.destination, Destination::S3Multipart { .. }) {
                // Same https rule as create-time part URLs.
                match part_url.as_deref() {
                    Some(u) if is_https(u) => {}
                    Some(_) => {
                        crate::wasm_util::discard_body(req).await;
                        return error_json("x-clowd-part-url must be https", 400);
                    }
                    None => {
                        crate::wasm_util::discard_body(req).await;
                        return error_json("x-clowd-part-url header is required for s3 chunks", 400);
                    }
                }
            }
        } else if n >= state.chunk_count {
            crate::wasm_util::discard_body(req).await;
            return error_json("chunk number out of range", 400);
        }

        let bytes = req.bytes().await?;
        let len = bytes.len() as u64;
        if unknown {
            // Pre-staging validation on the (possibly stale) snapshot — cheap
            // reject before the R2 put; re-checked against fresh state below.
            if let Err(rej) = manifest::check_unknown_chunk(
                n,
                len,
                is_final,
                state.chunk_size,
                state.final_index,
                state.final_chunk_len,
                state.highest_staged(),
            ) {
                return self.reject_unknown_chunk(rej).await;
            }
        } else if let Some(plan) = Self::plan_of(&state) {
            if let Some(expected) = expected_chunk_len(n, &plan) {
                if bytes.len() as u64 != expected {
                    return error_json("chunk has the wrong size", 400);
                }
            }
        }

        let bucket = self.env.bucket("STAGING")?;
        let key = format!("{}/{n:05}", state.id);
        bucket.put(&key, bytes).execute().await?;

        // The R2 put above (and `req.bytes()` before it) took seconds, during which
        // the DO input gate lets other events interleave — concurrent chunk PUTs,
        // relay results, /abort. Re-load fresh state and apply the mutation with no
        // await in between so we don't persist a stale snapshot over their updates
        // (mirrors `relay()`). Bail if the session went terminal under us.
        let mut state = self
            .load()
            .await?
            .ok_or_else(|| Error::RustError("session vanished".into()))?;
        if !manifest::can_accept_chunk(state.status) {
            // Aborted/failed/completed while we were staging — the R2 object will be
            // swept by the linger cleanup / lifecycle rule; don't resurrect.
            return Response::error("session is no longer accepting chunks", 409);
        }
        if unknown {
            // A concurrent PUT may have set (or conflicted with) the final marker
            // while we were staging — re-run the rules against fresh state. A
            // rejected chunk's R2 object is swept by the lifecycle rule.
            if let Err(rej) = manifest::check_unknown_chunk(
                n,
                len,
                is_final,
                state.chunk_size,
                state.final_index,
                state.final_chunk_len,
                state.highest_staged(),
            ) {
                return self.reject_unknown_chunk(rej).await;
            }
            state.ensure_chunk_slot(n);
            if is_final && state.final_index.is_none() {
                state.final_index = Some(n);
                state.final_chunk_len = Some(len);
            }
            if let Some(u) = part_url {
                state.lazy_part_urls[n as usize] = Some(u);
            }
        }
        state.staged[n as usize] = true;
        state.last_activity_ms = now_ms();
        let need_relay = state.relayed[n as usize].is_none();
        self.persist(&state).await?;
        self.notifier.notify();
        // Reset the idle timeout on every chunk (parity with the old activity clock).
        self.set_alarm_in(IDLE_TIMEOUT_MS).await?;

        if need_relay {
            let msg = crate::model::RelayMessage {
                upload_id: state.id.clone(),
                chunk_no: n,
            };
            // Best effort: /complete backstops relay if the queue is slow.
            let _ = self.env.queue("RELAY")?.send(&msg).await;
        }

        #[derive(serde::Serialize)]
        struct Received {
            received: u64,
        }
        Response::from_json(&Received {
            received: n,
        })
    }

    /// Map an unknown-length chunk rejection to its response. `Fatal` (the
    /// cumulative 10 GiB cap) also fails the session and aborts the destination
    /// — the same failure shape as the idle timeout / DLQ path.
    async fn reject_unknown_chunk(&self, rej: manifest::ChunkReject) -> Result<Response> {
        match rej {
            manifest::ChunkReject::Bad(msg) => error_json(&msg, 400),
            manifest::ChunkReject::Fatal(msg) => {
                // Re-load fresh state and apply the terminal transition with no
                // await in between (input-gate discipline; mirrors `fail_internal`).
                if let Some(mut state) = self.load().await? {
                    if !manifest::is_terminal(state.status) {
                        state.status = SessionStatus::Failed;
                        self.persist(&state).await?;
                        let _ = dest::abort(&state.destination).await;
                        self.delete_staging(&state).await;
                        self.notifier.notify();
                        self.set_alarm_in(LINGER_MS).await?;
                    }
                }
                error_json(&msg, 400)
            }
        }
    }

    async fn relay(&self, n: u64) -> Result<Response> {
        let Some(state) = self.load().await? else {
            return Response::error("not found", 404);
        };
        // Bound by the tracking vectors, not chunk_count — unknown-length
        // sessions keep chunk_count at 0 while chunks arrive.
        let Some(slot) = state.relayed.get(n as usize) else {
            return Response::error("chunk out of range", 400);
        };
        if slot.is_some() {
            return Response::empty(); // idempotent — already relayed
        }
        if !state.staged[n as usize] {
            return Response::error("chunk not staged yet", 409); // queue will retry
        }

        let result = self.relay_one(&state, n).await?;
        // reload to avoid clobbering concurrent updates, then record.
        let mut state = self
            .load()
            .await?
            .ok_or_else(|| Error::RustError("session vanished".into()))?;
        if let Some(slot) = state.relayed.get_mut(n as usize) {
            *slot = Some(result);
        }
        self.persist(&state).await?;
        Response::empty()
    }

    async fn relay_one(&self, state: &SessionState, n: u64) -> Result<String> {
        let bucket = self.env.bucket("STAGING")?;
        let key = format!("{}/{n:05}", state.id);
        let obj = bucket
            .get(&key)
            .execute()
            .await?
            .ok_or_else(|| Error::RustError(format!("chunk {n} missing from staging")))?;
        let body = obj
            .body()
            .ok_or_else(|| Error::RustError(format!("chunk {n} has no body")))?;
        let bytes = body.bytes().await?;
        // Resolve chunk n's part URL uniformly for both session kinds
        // (create-time list, or the lazily-collected x-clowd-part-url values).
        let part_url = state.part_url(n);
        dest::relay_chunk(&state.destination, n, bytes, part_url.as_deref()).await
    }

    async fn complete(&self, req: &Request) -> Result<Response> {
        let Some(state) = self.load().await? else {
            return Response::error("not found", 404);
        };
        if !Self::authed(req, &state.upload_token) {
            return Response::error("unauthorized", 401);
        }

        // Idempotent: already complete → return the same result.
        if state.status == SessionStatus::Complete {
            return crate::wasm_util::json_status(
                &CompleteResponse {
                    final_url: state.final_url.clone(),
                    length: state.content_length.unwrap_or(0),
                },
                200,
            );
        }
        // A commit is already running (a concurrent/retried /complete). Do NOT run
        // the commit again — a second CompleteMultipartUpload double-commits and the
        // S3 loser gets NoSuchUpload, which the old error path turned into a Failed
        // session *after* the winner had succeeded. Tell the client to retry; it
        // will see Complete once the in-flight call finishes (REFACTOR §4.3).
        if state.status == SessionStatus::Committing {
            return error_json("commit in progress, retry shortly", 409);
        }
        if matches!(
            state.status,
            SessionStatus::Failed | SessionStatus::Aborted | SessionStatus::Deleted
        ) {
            return Response::error("session is not completable", 409);
        }

        // Unknown-length sessions are completable only once the client has
        // marked (and we have staged) a final chunk — that fixes the count.
        if state.is_unknown_length() && state.final_index.is_none() {
            return error_json("cannot complete: the final chunk has not been received", 400);
        }
        if !state.all_staged() {
            return error_json("cannot complete: some chunks are missing", 400);
        }

        // Wait briefly for the relay queue to drain.
        let mut state = state;
        for _ in 0..COMPLETE_WAIT_ITERS {
            if state.all_relayed() {
                break;
            }
            worker::Delay::from(COMPLETE_WAIT_STEP).await;
            state = self
                .load()
                .await?
                .ok_or_else(|| Error::RustError("session vanished".into()))?;
        }

        // Backstop: relay a bounded number of stragglers inline. (Bounded by the
        // tracking vector, not chunk_count — see `relay()`.)
        if !state.all_relayed() {
            let missing: Vec<u64> = (0..state.relayed.len() as u64)
                .filter(|&n| state.relayed[n as usize].is_none())
                .collect();
            if missing.len() > INLINE_RELAY_CAP {
                return error_json("cannot complete: chunks not yet relayed, retry shortly", 400);
            }
            for n in missing {
                let result = self.relay_one(&state, n).await?;
                state = self
                    .load()
                    .await?
                    .ok_or_else(|| Error::RustError("session vanished".into()))?;
                state.relayed[n as usize] = Some(result);
                self.persist(&state).await?;
            }
        }

        let results = state
            .ordered_relay_results()
            .ok_or_else(|| Error::RustError("relay results incomplete".into()))?;

        // Unknown-length: the true total is now known — F * chunkSize + the final
        // chunk's length. Store it (it becomes CompleteResponse.length and lets
        // tails emit Content-Length from here on) and fix the chunk count for the
        // destination commit and cleanup paths.
        if state.is_unknown_length() {
            let total = state
                .computed_total()
                .ok_or_else(|| Error::RustError("final chunk length missing".into()))?;
            state.content_length = Some(total);
            state.chunk_count = state.final_index.map(|f| f + 1).unwrap_or(0);
        }

        // Enter committing (tails keep streaming during commit). Refresh the
        // activity clock + idle alarm so a slow drain/commit is not mistaken for an
        // idle session and failed out from under us (see `alarm`).
        state.status = SessionStatus::Committing;
        state.last_activity_ms = now_ms();
        self.persist(&state).await?;
        self.set_alarm_in(IDLE_TIMEOUT_MS).await?;

        // 1) commit the destination.
        if let Err(e) = dest::commit(
            &state.destination,
            &results,
            &state.content_type,
            &state.file_name,
            state.chunk_count,
        )
        .await
        {
            // Re-load before failing: never downgrade a Complete session (a winner
            // may have finished under us) back to Failed.
            let mut fresh = self
                .load()
                .await?
                .ok_or_else(|| Error::RustError("session vanished".into()))?;
            if fresh.status == SessionStatus::Complete {
                return crate::wasm_util::json_status(
                    &CompleteResponse {
                        final_url: fresh.final_url.clone(),
                        length: fresh.content_length.unwrap_or(0),
                    },
                    200,
                );
            }
            fresh.status = SessionStatus::Failed;
            self.persist(&fresh).await?;
            let _ = dest::abort(&fresh.destination).await;
            self.notifier.notify();
            self.set_alarm_in(LINGER_MS).await?;
            return error_json(&format!("destination commit failed: {e}"), 502);
        }

        // The commit succeeded — the destination object now exists, so completion is
        // authoritative. Re-load to pick up any concurrent manifest changes, but
        // proceed to publish the redirect regardless of what raced under us (short
        // of an already-published Complete, handled idempotently below).
        let mut state = self
            .load()
            .await?
            .ok_or_else(|| Error::RustError("session vanished".into()))?;
        if state.status == SessionStatus::Complete {
            return crate::wasm_util::json_status(
                &CompleteResponse {
                    final_url: state.final_url.clone(),
                    length: state.content_length.unwrap_or(0),
                },
                200,
            );
        }

        // 2) write KV *before* marking complete (write-once redirect ordering). Embed
        // the delete-token hash so DELETE stays authorizable after the DO's
        // post-completion linger wipes its storage (REFACTOR §3.1/§4.4).
        #[derive(serde::Serialize)]
        struct RedirectRecord<'a> {
            #[serde(rename = "finalUrl")]
            final_url: &'a str,
            length: u64,
            #[serde(rename = "completedUtc")]
            completed_utc: f64,
            #[serde(rename = "deleteTokenHash")]
            delete_token_hash: String,
        }
        let record = RedirectRecord {
            final_url: &state.final_url,
            length: state.content_length.unwrap_or(0),
            completed_utc: now_ms(),
            delete_token_hash: ids::hash_token(&state.delete_token),
        };
        let kv = self.env.kv("REDIRECTS")?;
        kv.put(&state.id, serde_json::to_string(&record)?)?
            .execute()
            .await?;

        // 3) mark complete (DO answers 301 during KV propagation).
        state.status = SessionStatus::Complete;
        self.persist(&state).await?;
        self.notifier.notify();

        // 4) linger, then delete staging + DO storage.
        self.set_alarm_in(LINGER_MS).await?;

        crate::wasm_util::json_status(
            &CompleteResponse {
                final_url: state.final_url.clone(),
                length: state.content_length.unwrap_or(0),
            },
            200,
        )
    }

    async fn abort(&self, req: &Request) -> Result<Response> {
        let Some(mut state) = self.load().await? else {
            return Response::error("not found", 404);
        };
        if !Self::authed(req, &state.upload_token) {
            return Response::error("unauthorized", 401);
        }
        if manifest::is_terminal(state.status) {
            return Response::empty(); // idempotent
        }

        state.status = SessionStatus::Aborted;
        self.persist(&state).await?;
        let _ = dest::abort(&state.destination).await;
        self.delete_staging(&state).await;
        self.notifier.notify(); // sever active tails
        self.set_alarm_in(LINGER_MS).await?; // cleanup DO storage after linger
        Response::empty()
    }

    async fn delete(&self, req: &Request) -> Result<Response> {
        let Some(mut state) = self.load().await? else {
            return Response::error("not found", 404);
        };
        if !Self::authed(req, &state.delete_token) {
            return Response::error("unauthorized", 401);
        }

        // Remove the short link → /u/{id} becomes 404.
        let _ = self
            .env
            .kv("REDIRECTS")?
            .delete(&state.id)
            .await;
        state.status = SessionStatus::Deleted;
        self.persist(&state).await?;
        self.notifier.notify();
        self.delete_staging(&state).await;
        self.set_alarm_in(LINGER_MS).await?;
        Response::empty()
    }

    async fn fail_internal(&self) -> Result<Response> {
        let Some(mut state) = self.load().await? else {
            return Response::empty();
        };
        if manifest::is_terminal(state.status) {
            return Response::empty();
        }
        state.status = SessionStatus::Failed;
        self.persist(&state).await?;
        let _ = dest::abort(&state.destination).await;
        self.delete_staging(&state).await;
        self.notifier.notify();
        self.set_alarm_in(LINGER_MS).await?;
        Response::empty()
    }

    async fn tail(&self) -> Result<Response> {
        let Some(state) = self.load().await? else {
            return Response::error("not found", 404);
        };

        match manifest::tail_disposition(state.status) {
            manifest::TailDisposition::NotFound => Response::error("not found", 404),
            manifest::TailDisposition::Gone => Response::error("gone", 410),
            manifest::TailDisposition::Redirect => self.redirect_response(&state.final_url),
            manifest::TailDisposition::Stream => self.stream_response(&state),
        }
    }

    fn redirect_response(&self, final_url: &str) -> Result<Response> {
        // Build the 301 from fresh mutable headers. `Response::redirect_with_status`
        // yields immutable (Fetch-spec guarded) headers, so setting Cache-Control on
        // it throws a TypeError and the response 500s.
        worker::Url::parse(final_url).map_err(|e| Error::RustError(format!("bad final url: {e}")))?;
        let headers = worker::Headers::new();
        headers.set("Location", final_url)?;
        headers.set("Cache-Control", &format!("public, max-age={REDIRECT_MAX_AGE_SECS}"))?;
        Ok(worker::ResponseBuilder::new()
            .with_headers(headers)
            .with_status(301)
            .empty())
    }

    fn stream_response(&self, state: &SessionState) -> Result<Response> {
        let cache = self.cache.clone();
        let notifier = self.notifier.clone();
        let bucket = self.env.bucket("STAGING")?;
        let id = state.id.clone();

        let stream = tail_stream(cache, notifier, bucket, id);

        let disposition = sanitize::content_disposition(Some(&state.file_name));
        let headers = worker::Headers::new();
        headers.set("Content-Type", &state.content_type)?;
        headers.set("Content-Disposition", &disposition)?;
        headers.set("Cache-Control", "no-store")?;
        headers.set("Accept-Ranges", "none")?;
        // Creation is unauthenticated and the content type is attacker-controlled, so
        // this content is served from the trusted clwd.app origin. Prevent it from
        // being interpreted as active content (hosted HTML/JS, SVG script): forbid
        // MIME sniffing and sandbox it into a unique, script-disabled origin.
        headers.set("X-Content-Type-Options", "nosniff")?;
        headers.set("Content-Security-Policy", "sandbox; default-src 'none'")?;

        let builder = worker::ResponseBuilder::new()
            .with_headers(headers)
            .with_status(200);
        match state.content_length {
            // workerd only emits Content-Length on a streaming response when the body is
            // the readable side of a FixedLengthStream; a Content-Length header set on a
            // plain ReadableStream body is ignored and the response falls back to
            // Transfer-Encoding: chunked (no browser progress bar). A sever (Err from the
            // tail stream) still aborts the pipe and resets the connection.
            Some(len) => {
                let fixed: worker::worker_sys::FixedLengthStream = worker::FixedLengthStream::wrap(stream, len).into();
                Ok(builder.stream(fixed.readable()))
            }
            // Unknown-length upload still in flight: the total does not exist yet, so
            // FixedLengthStream cannot be used — serve a plain streaming body
            // (Transfer-Encoding: chunked is the accepted degradation). All the
            // security headers above still apply.
            None => builder.from_stream(stream),
        }
    }
}
