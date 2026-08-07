//! Queue consumer: relays staged chunks to their destination.
//!
//! Bound to `clowd-relay` (relay work) and `clowd-relay-dlq` (dead-letter). Each
//! message is `{uploadId, chunkNo}`. Relay work dispatches to the session DO's
//! `/relay/{n}` (idempotent via fixed block ids / part numbers); DLQ messages
//! mean a chunk exhausted its retries → mark the session failed (tails sever,
//! the uploader's next call errors — parity with the old fail-fast behavior).

use worker::{Env, MessageBatch, MessageExt, Method, Result};

use crate::ids::is_valid_id;
use crate::model::RelayMessage;
use crate::telemetry::{self, Report};
use crate::wasm_util::{do_request, is_success};

pub async fn handle(batch: MessageBatch<RelayMessage>, env: &Env) -> Result<()> {
    let is_dlq = batch.queue().ends_with("-dlq");

    for msg in batch.messages()? {
        let body = msg.body();
        let id = body.upload_id.clone();
        let n = body.chunk_no;

        if !is_valid_id(&id) {
            msg.ack(); // unroutable — drop it
            continue;
        }

        let ns = match env.durable_object("SESSIONS") {
            Ok(ns) => ns,
            Err(_) => {
                msg.retry();
                continue;
            }
        };
        let stub = match ns
            .id_from_name(&id)
            .and_then(|oid| oid.get_stub())
        {
            Ok(s) => s,
            Err(_) => {
                msg.retry();
                continue;
            }
        };

        if is_dlq {
            // A chunk that reached the dead-letter queue exhausted its retries:
            // the upload is about to be marked failed and every tail severed.
            // Exactly one event per chunk, after 5 attempts, so it cannot become a
            // hot path — and it fires even when the failure was reaching the DO at
            // all, which is the one case `session.relay` cannot report. It carries
            // no cause; pair it with the `session.relay` events for that.
            telemetry::capture(
                env,
                Report::error("relay.dead_letter", format!("chunk {n} exhausted its relay retries"))
                    .transaction("queue clowd-relay-dlq".to_string())
                    .extra("upload_id", id.clone())
                    .extra("chunk_no", n.to_string()),
            )
            .await;
        }

        let path = if is_dlq { "/fail".to_string() } else { format!("/relay/{n}") };
        let outcome = do_request(&stub, &path, Method::Post, None, &[]).await;

        match outcome {
            Ok(resp) if is_success(&resp) => msg.ack(),
            _ if is_dlq => msg.ack(), // best-effort fail marking; don't loop the DLQ
            _ => msg.retry(),
        }
    }

    Ok(())
}
