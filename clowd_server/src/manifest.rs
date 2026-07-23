//! Session status state machine (pure, host-testable).
//!
//! Porting the legacy `UploadState` transitions (`UploadSession.cs`): a session
//! starts `Uploading`, may move to `Committing` while `/complete` finalizes the
//! destination, then to a terminal state. Terminal states never transition again
//! (parity with the `if (_state != Uploading) return;` guards).

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
}
