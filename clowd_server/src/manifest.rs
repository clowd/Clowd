//! Session status state machine (pure, host-testable).
//!
//! Porting the legacy `UploadState` transitions (`UploadSession.cs`): a session
//! starts `Uploading`, may move to `Committing` while `/complete` finalizes the
//! destination, then to a terminal state. Terminal states never transition again
//! (parity with the `if (_state != Uploading) return;` guards).

use crate::chunkplan::{implied_total, MAX_CHUNK_COUNT, MAX_UPLOAD_BYTES};
use crate::model::SessionStatus;

/// How `GET /u/{id}` should answer for a given status when KV missed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TailDisposition {
    /// Live-tail the staged chunks.
    Stream,
    /// 301 to the final URL (KV still propagating).
    Redirect,
    /// 410 Gone (failed/aborted).
    Gone,
    /// 404 (unknown / deleted).
    NotFound,
}

pub fn is_terminal(status: SessionStatus) -> bool {
    matches!(
        status,
        SessionStatus::Complete | SessionStatus::Failed | SessionStatus::Aborted | SessionStatus::Deleted
    )
}

/// Whether a chunk PUT is allowed in this status.
pub fn can_accept_chunk(status: SessionStatus) -> bool {
    matches!(status, SessionStatus::Uploading)
}

/// Whether the tail streaming loop should keep serving (vs. sever/stop).
pub fn tail_disposition(status: SessionStatus) -> TailDisposition {
    match status {
        SessionStatus::Uploading | SessionStatus::Committing => TailDisposition::Stream,
        SessionStatus::Complete => TailDisposition::Redirect,
        SessionStatus::Failed | SessionStatus::Aborted => TailDisposition::Gone,
        SessionStatus::Deleted => TailDisposition::NotFound,
    }
}

/// Why a chunk PUT on an unknown-length session was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ChunkReject {
    /// Plain 400 — the session keeps running (bad index/size, conflicting final
    /// marker). Re-PUTs of already-accepted chunks stay idempotent.
    Bad(String),
    /// 400 AND the session must fail (destination aborted) — accepting the chunk
    /// would push the cumulative total past `MAX_UPLOAD_BYTES`.
    Fatal(String),
}

/// Validate a chunk PUT on an unknown-length session (pure; the caller re-runs
/// this against fresh state after every await).
///
/// Rules (spec v2 §2):
/// - every chunk is EXACTLY `chunk_size` bytes except the final one
///   (`?final=1`), which may be `1..=chunk_size` (full-size final is legal);
///   zero-byte chunks are rejected
/// - once a final chunk is accepted at `F`: `n > F` is rejected, the final
///   marker is immutable (conflicting markers rejected), and re-PUTs of
///   `n <= F` stay idempotent (the final re-PUT must repeat its exact length)
/// - a final marker below an already-staged higher chunk is a conflict
/// - the 10 GiB cap is enforced cumulatively: `n * chunk_size + len`
pub fn check_unknown_chunk(
    n: u64,
    len: u64,
    is_final: bool,
    chunk_size: u64,
    final_index: Option<u64>,
    final_chunk_len: Option<u64>,
    highest_staged: Option<u64>,
) -> Result<(), ChunkReject> {
    if n >= MAX_CHUNK_COUNT {
        return Err(ChunkReject::Bad("chunk number out of range".into()));
    }
    if let Some(f) = final_index {
        if n > f {
            return Err(ChunkReject::Bad("chunk number is beyond the final chunk".into()));
        }
        if is_final && n != f {
            return Err(ChunkReject::Bad("conflicting final chunk marker".into()));
        }
        if !is_final && n == f {
            return Err(ChunkReject::Bad("chunk was already marked final".into()));
        }
        if is_final && n == f {
            if let Some(flen) = final_chunk_len {
                if len != flen {
                    return Err(ChunkReject::Bad(
                        "final chunk length conflicts with the accepted final chunk".into(),
                    ));
                }
            }
        }
    } else if is_final {
        if let Some(h) = highest_staged {
            // `>=` deliberately: re-marking an already-staged chunk as final could
            // shrink (or swap) bytes that were already relayed to the destination,
            // desyncing the committed object from the computed total. The shipped
            // chunker never does this — a lost-response retry of the real final PUT
            // lands in the idempotent `n == f` branch above instead.
            if h >= n {
                return Err(ChunkReject::Bad(
                    "cannot mark chunk as final: it or later chunks are already staged".into(),
                ));
            }
        }
    }
    if len == 0 {
        return Err(ChunkReject::Bad("zero-byte chunks are not allowed".into()));
    }
    if is_final {
        if len > chunk_size {
            return Err(ChunkReject::Bad("final chunk exceeds the chunk size".into()));
        }
    } else if len != chunk_size {
        return Err(ChunkReject::Bad("non-final chunk must be exactly chunkSize bytes".into()));
    }
    if implied_total(n, chunk_size, len) > MAX_UPLOAD_BYTES {
        return Err(ChunkReject::Fatal(format!("upload exceeds the {MAX_UPLOAD_BYTES} byte limit")));
    }
    Ok(())
}

/// A tail already streaming bytes must be *severed* (not cleanly ended) if the
/// session reaches a failed/aborted/deleted state mid-stream — the byte count
/// won't add up, so the connection is reset rather than closed with a truncated
/// but "clean" EOF (parity with `DownloadStreamer`/`UploadFailedException`).
pub fn should_sever_active_tail(status: SessionStatus) -> bool {
    matches!(status, SessionStatus::Failed | SessionStatus::Aborted | SessionStatus::Deleted)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_uploading_accepts_chunks() {
        assert!(can_accept_chunk(SessionStatus::Uploading));
        for s in [
            SessionStatus::Committing,
            SessionStatus::Complete,
            SessionStatus::Failed,
            SessionStatus::Aborted,
            SessionStatus::Deleted,
        ] {
            assert!(!can_accept_chunk(s), "{s:?} must reject chunks");
        }
    }

    #[test]
    fn terminal_classification() {
        assert!(!is_terminal(SessionStatus::Uploading));
        assert!(!is_terminal(SessionStatus::Committing));
        assert!(is_terminal(SessionStatus::Complete));
        assert!(is_terminal(SessionStatus::Failed));
        assert!(is_terminal(SessionStatus::Aborted));
        assert!(is_terminal(SessionStatus::Deleted));
    }

    #[test]
    fn tail_dispositions() {
        assert_eq!(tail_disposition(SessionStatus::Uploading), TailDisposition::Stream);
        assert_eq!(tail_disposition(SessionStatus::Committing), TailDisposition::Stream);
        assert_eq!(tail_disposition(SessionStatus::Complete), TailDisposition::Redirect);
        assert_eq!(tail_disposition(SessionStatus::Failed), TailDisposition::Gone);
        assert_eq!(tail_disposition(SessionStatus::Aborted), TailDisposition::Gone);
        assert_eq!(tail_disposition(SessionStatus::Deleted), TailDisposition::NotFound);
    }

    #[test]
    fn sever_only_on_hard_failure() {
        assert!(should_sever_active_tail(SessionStatus::Failed));
        assert!(should_sever_active_tail(SessionStatus::Aborted));
        assert!(should_sever_active_tail(SessionStatus::Deleted));
        assert!(!should_sever_active_tail(SessionStatus::Complete));
        assert!(!should_sever_active_tail(SessionStatus::Uploading));
    }

    // --- unknown-length chunk rules ---------------------------------------

    use crate::chunkplan::DEFAULT_CHUNK as CS;

    fn bad(r: Result<(), ChunkReject>) -> bool {
        matches!(r, Err(ChunkReject::Bad(_)))
    }
    fn fatal(r: Result<(), ChunkReject>) -> bool {
        matches!(r, Err(ChunkReject::Fatal(_)))
    }

    #[test]
    fn unknown_nonfinal_must_be_exactly_chunk_size() {
        assert!(check_unknown_chunk(0, CS, false, CS, None, None, None).is_ok());
        assert!(bad(check_unknown_chunk(0, CS - 1, false, CS, None, None, None)));
        assert!(bad(check_unknown_chunk(0, CS + 1, false, CS, None, None, None)));
    }

    #[test]
    fn unknown_final_may_be_one_to_chunk_size() {
        assert!(check_unknown_chunk(2, 1, true, CS, None, None, Some(1)).is_ok());
        // a full-size final is legal
        assert!(check_unknown_chunk(2, CS, true, CS, None, None, Some(1)).is_ok());
        assert!(bad(check_unknown_chunk(2, CS + 1, true, CS, None, None, Some(1))));
    }

    #[test]
    fn unknown_zero_byte_chunks_rejected() {
        assert!(bad(check_unknown_chunk(0, 0, false, CS, None, None, None)));
        assert!(bad(check_unknown_chunk(0, 0, true, CS, None, None, None)));
    }

    #[test]
    fn unknown_chunks_beyond_final_rejected() {
        assert!(bad(check_unknown_chunk(4, CS, false, CS, Some(3), Some(10), Some(3))));
        assert!(bad(check_unknown_chunk(4, 5, true, CS, Some(3), Some(10), Some(3))));
    }

    #[test]
    fn unknown_final_marker_is_immutable() {
        // duplicate final at a different index
        assert!(bad(check_unknown_chunk(2, CS, true, CS, Some(3), Some(10), Some(3))));
        // re-PUT of the final chunk without final=1
        assert!(bad(check_unknown_chunk(3, 10, false, CS, Some(3), Some(10), Some(3))));
        // idempotent re-PUT of the final chunk (same length) is fine …
        assert!(check_unknown_chunk(3, 10, true, CS, Some(3), Some(10), Some(3)).is_ok());
        // … but a different length conflicts
        assert!(bad(check_unknown_chunk(3, 11, true, CS, Some(3), Some(10), Some(3))));
        // marking final below an already-staged higher chunk conflicts
        assert!(bad(check_unknown_chunk(1, 10, true, CS, None, None, Some(5))));
        // re-marking the highest already-staged chunk as final conflicts too — its full-size
        // bytes may already have relayed, and a shorter final would desync the destination
        // object from the computed total
        assert!(bad(check_unknown_chunk(5, 10, true, CS, None, None, Some(5))));
        assert!(bad(check_unknown_chunk(5, CS, true, CS, None, None, Some(5))));
    }

    #[test]
    fn unknown_earlier_reputs_stay_idempotent_after_final() {
        assert!(check_unknown_chunk(1, CS, false, CS, Some(3), Some(10), Some(3)).is_ok());
    }

    #[test]
    fn unknown_cumulative_cap_is_fatal() {
        use crate::chunkplan::MAX_UPLOAD_BYTES;
        // 640 full 16 MiB chunks is exactly the 10 GiB cap → the last one fits
        assert_eq!(640 * CS, MAX_UPLOAD_BYTES);
        assert!(check_unknown_chunk(639, CS, true, CS, None, None, Some(638)).is_ok());
        // one chunk further (even a single byte) exceeds the cap → session fails
        assert!(fatal(check_unknown_chunk(640, 1, true, CS, None, None, Some(639))));
        assert!(fatal(check_unknown_chunk(640, CS, false, CS, None, None, Some(639))));
    }

    #[test]
    fn unknown_chunk_index_hard_bounded() {
        assert!(bad(check_unknown_chunk(MAX_CHUNK_COUNT, CS, false, CS, None, None, None)));
        assert!(bad(check_unknown_chunk(u64::MAX, 1, true, CS, None, None, None)));
    }
}
