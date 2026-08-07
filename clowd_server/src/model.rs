//! Wire DTOs, the destination descriptor, and the persisted session state.
//! All JSON is camelCase (parity with the legacy `Api/Dto.cs`). Pure — no wasm
//! APIs — so the state machine and destination validation are host-testable.

use serde::{Deserialize, Serialize};

use crate::azure;

// ---------------------------------------------------------------------------
// Control-plane request/response DTOs (v2)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateRequest {
    pub file_name: Option<String>,
    pub content_type: Option<String>,
    /// Optional since the unknown-length extension: absent or `null` means the
    /// client cannot seek the source (accelerated pipe upload). Clients keep
    /// sending it whenever they know it — it enables Content-Length on tails.
    pub content_length: Option<i64>,
    pub chunk_size: Option<u64>,
    pub destination: Destination,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateResponse {
    pub id: String,
    pub download_url: String,
    pub upload_token: String,
    pub delete_token: String,
    pub chunk_size: u64,
    pub chunk_count: u64,
    pub final_url: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompleteResponse {
    pub final_url: String,
    pub length: u64,
}

/// Relay queue message: `{uploadId, chunkNo}` (REFACTOR §6).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RelayMessage {
    pub upload_id: String,
    pub chunk_no: u64,
}

// ---------------------------------------------------------------------------
// Destination descriptor — capability URLs only, never account keys (§6)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum Destination {
    /// Client-supplied blob-level SAS URL with create+write permission.
    #[serde(rename_all = "camelCase")]
    AzureBlob {
        sas_url: String,
        #[serde(default)]
        custom_domain: Option<String>,
        /// Optional explicit final URL (custom-domain override).
        #[serde(default)]
        final_url: Option<String>,
    },
    /// Client presigns everything; the server never sees account keys.
    #[serde(rename_all = "camelCase")]
    S3Multipart {
        part_urls: Vec<String>,
        complete_url: String,
        abort_url: String,
        final_url: String,
    },
    /// Relays to nowhere; commit is a no-op. Dev/local e2e only, gated behind
    /// the `DEV_ALLOW_DISCARD` env var at the router.
    #[serde(rename_all = "camelCase")]
    Discard {
        #[serde(default = "discard_default_final")]
        final_url: String,
    },
}

fn discard_default_final() -> String {
    "https://clwd.app/discard".into()
}

/// The https-only check applied to every capability URL — create-time
/// destinations and the lazily-arriving `x-clowd-part-url` values alike.
pub fn is_https(url: &str) -> bool {
    url.starts_with("https://")
}

impl Destination {
    pub fn kind(&self) -> &'static str {
        match self {
            Destination::AzureBlob {
                ..
            } => "azure-blob",
            Destination::S3Multipart {
                ..
            } => "s3-multipart",
            Destination::Discard {
                ..
            } => "discard",
        }
    }

    pub fn is_discard(&self) -> bool {
        matches!(self, Destination::Discard { .. })
    }

    /// The public URL the completed upload redirects to.
    pub fn final_url(&self) -> String {
        match self {
            Destination::AzureBlob {
                sas_url,
                custom_domain,
                final_url,
            } => {
                if let Some(f) = final_url {
                    f.clone()
                } else if let Some(d) = custom_domain {
                    // custom-domain host + the blob path from the SAS URL
                    let base = azure::strip_query(sas_url);
                    if let Some(path) = base
                        .split_once("//")
                        .and_then(|(_, rest)| rest.split_once('/'))
                        .map(|(_, p)| p)
                    {
                        format!("https://{d}/{path}")
                    } else {
                        base
                    }
                } else {
                    azure::strip_query(sas_url)
                }
            }
            Destination::S3Multipart {
                final_url,
                ..
            } => final_url.clone(),
            Destination::Discard {
                final_url,
            } => final_url.clone(),
        }
    }

    /// Reject non-https / malformed destinations (§4.1, §6). `Err(msg)` → 400.
    /// `chunk_count` cross-checks the S3 presigned part-URL count.
    pub fn validate(&self, chunk_count: u64) -> Result<(), String> {
        match self {
            Destination::AzureBlob {
                sas_url,
                custom_domain,
                final_url,
            } => {
                if !is_https(sas_url) {
                    return Err("azure sasUrl must be https".into());
                }
                if sas_url
                    .split_once('?')
                    .map(|(_, q)| q.is_empty())
                    .unwrap_or(true)
                {
                    return Err("azure sasUrl must carry a SAS query string".into());
                }
                if let Some(f) = final_url {
                    if !is_https(f) {
                        return Err("azure finalUrl must be https".into());
                    }
                }
                if custom_domain
                    .as_deref()
                    .map(|d| d.is_empty())
                    .unwrap_or(false)
                {
                    return Err("azure customDomain must not be empty".into());
                }
                Ok(())
            }
            Destination::S3Multipart {
                part_urls,
                complete_url,
                abort_url,
                final_url,
            } => {
                if part_urls.is_empty() {
                    return Err("s3 partUrls must not be empty".into());
                }
                if part_urls.len() as u64 != chunk_count {
                    return Err(format!(
                        "s3 partUrls has {} entries but the upload plans {chunk_count} chunks",
                        part_urls.len()
                    ));
                }
                for u in part_urls {
                    if !is_https(u) {
                        return Err("s3 partUrls must all be https".into());
                    }
                }
                for (name, u) in [("completeUrl", complete_url), ("abortUrl", abort_url), ("finalUrl", final_url)] {
                    if !is_https(u) {
                        return Err(format!("s3 {name} must be https"));
                    }
                }
                Ok(())
            }
            Destination::Discard {
                final_url,
            } => {
                if !is_https(final_url) {
                    return Err("discard finalUrl must be https".into());
                }
                Ok(())
            }
        }
    }

    /// Destination validation for unknown-length uploads: `s3-multipart` must
    /// have an EMPTY `partUrls` (part URLs arrive per-chunk via
    /// `x-clowd-part-url`) while the control URLs are still https-checked;
    /// azure/discard follow the same rules as known-length (count-free).
    pub fn validate_unknown(&self) -> Result<(), String> {
        match self {
            Destination::S3Multipart {
                part_urls,
                complete_url,
                abort_url,
                final_url,
            } => {
                if !part_urls.is_empty() {
                    return Err("s3 partUrls must be empty when contentLength is unknown".into());
                }
                for (name, u) in [("completeUrl", complete_url), ("abortUrl", abort_url), ("finalUrl", final_url)] {
                    if !is_https(u) {
                        return Err(format!("s3 {name} must be https"));
                    }
                }
                Ok(())
            }
            // azure/discard validation does not depend on the chunk count.
            _ => self.validate(0),
        }
    }
}

// ---------------------------------------------------------------------------
// Persisted session state (Durable Object storage under key "state")
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SessionStatus {
    Uploading,
    Committing,
    Complete,
    Failed,
    Aborted,
    /// The short link was deleted by the client (`DELETE /uploads/{id}`); `/u/{id}` → 404.
    Deleted,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionState {
    pub id: String,
    pub file_name: String,
    pub content_type: String,
    /// Total upload bytes. `None` while an unknown-length session is in flight;
    /// `/complete` computes and stores the true total. Records persisted before
    /// the unknown-length extension always carry a number here.
    #[serde(default)]
    pub content_length: Option<u64>,
    pub chunk_size: u64,
    /// Planned chunk count. 0 while an unknown-length session is in flight; set
    /// to `final_index + 1` when `/complete` fixes the total.
    pub chunk_count: u64,
    pub upload_token: String,
    pub delete_token: String,
    pub destination: Destination,
    pub final_url: String,
    pub status: SessionStatus,
    /// Which chunks have been staged to R2 (grows on demand for unknown-length
    /// sessions, hard-bounded by `MAX_CHUNK_COUNT`).
    pub staged: Vec<bool>,
    /// Per-chunk relay result: block id (azure) / ETag (s3) / marker (discard).
    pub relayed: Vec<Option<String>>,
    /// Unknown-length sessions: index of the chunk the client marked `?final=1`.
    /// Set once, immutable — conflicting final markers are rejected.
    #[serde(default)]
    pub final_index: Option<u64>,
    /// Byte length of that final chunk (with `final_index`, fixes the total).
    #[serde(default)]
    pub final_chunk_len: Option<u64>,
    /// Per-chunk presigned S3 UploadPart URLs arriving lazily via the
    /// `x-clowd-part-url` header (unknown-length s3 sessions only; known-length
    /// sessions carry all part URLs in the destination from create).
    #[serde(default)]
    pub lazy_part_urls: Vec<Option<String>>,
    /// Last chunk-received time (ms since epoch) — drives the idle alarm.
    pub last_activity_ms: f64,
    pub created_ms: f64,
}

impl SessionState {
    pub fn all_staged(&self) -> bool {
        self.staged.iter().all(|s| *s)
    }

    pub fn all_relayed(&self) -> bool {
        self.relayed.iter().all(|r| r.is_some())
    }

    /// ETags/ids in chunk order, if every chunk has relayed.
    pub fn ordered_relay_results(&self) -> Option<Vec<String>> {
        self.relayed.iter().cloned().collect()
    }

    /// True while the session's total length is still unknown (created without
    /// `contentLength` and not yet completed).
    pub fn is_unknown_length(&self) -> bool {
        self.content_length.is_none()
    }

    /// Presigned S3 UploadPart URL for chunk `n` — create-time for known-length
    /// sessions, lazily stored (`x-clowd-part-url`) for unknown-length ones.
    /// `None` for azure/discard or when no URL has arrived yet.
    pub fn part_url(&self, n: u64) -> Option<String> {
        if let Destination::S3Multipart {
            part_urls,
            ..
        } = &self.destination
        {
            if let Some(u) = part_urls.get(n as usize) {
                return Some(u.clone());
            }
        }
        self.lazy_part_urls
            .get(n as usize)
            .cloned()
            .flatten()
    }

    /// Grow the per-chunk tracking vectors to cover chunk `n` (unknown-length
    /// sessions grow on demand; the caller bounds `n` by `MAX_CHUNK_COUNT`).
    pub fn ensure_chunk_slot(&mut self, n: u64) {
        let need = n as usize + 1;
        if self.staged.len() < need {
            self.staged.resize(need, false);
        }
        if self.relayed.len() < need {
            self.relayed.resize(need, None);
        }
        if self.lazy_part_urls.len() < need {
            self.lazy_part_urls.resize(need, None);
        }
    }

    /// Highest staged chunk index, if any chunk has been staged.
    pub fn highest_staged(&self) -> Option<u64> {
        self.staged
            .iter()
            .rposition(|s| *s)
            .map(|i| i as u64)
    }

    /// The true total of an unknown-length upload once the final chunk is known:
    /// `F * chunkSize + final chunk length`.
    pub fn computed_total(&self) -> Option<u64> {
        let f = self.final_index?;
        let flen = self.final_chunk_len?;
        Some(
            f.saturating_mul(self.chunk_size)
                .saturating_add(flen),
        )
    }

    /// Index one past the last chunk a tail must stream, if known yet:
    /// the fixed plan count for known-length sessions, `final_index + 1` once an
    /// unknown-length session's final chunk is marked, `None` before that (the
    /// tail parks on the notifier exactly like for a missing middle chunk).
    pub fn stream_end(&self) -> Option<u64> {
        if self.content_length.is_some() {
            Some(self.chunk_count)
        } else {
            self.final_index.map(|f| f + 1)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn azure(sas: &str) -> Destination {
        Destination::AzureBlob {
            sas_url: sas.into(),
            custom_domain: None,
            final_url: None,
        }
    }

    #[test]
    fn create_request_parses_camelcase() {
        let json = r#"{
            "fileName":"a.mp4","contentType":"video/mp4","contentLength":100,
            "chunkSize":16777216,
            "destination":{"type":"discard","finalUrl":"https://example.com/x"}
        }"#;
        let req: CreateRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.content_length, Some(100));
        assert_eq!(req.chunk_size, Some(16777216));
        assert!(req.destination.is_discard());
    }

    #[test]
    fn azure_destination_roundtrips() {
        let json = r#"{"type":"azure-blob","sasUrl":"https://a.blob.core.windows.net/c/b?sv=1&sig=x"}"#;
        let d: Destination = serde_json::from_str(json).unwrap();
        assert_eq!(d.kind(), "azure-blob");
        assert_eq!(d.final_url(), "https://a.blob.core.windows.net/c/b");
    }

    #[test]
    fn azure_custom_domain_final_url() {
        let d = Destination::AzureBlob {
            sas_url: "https://a.blob.core.windows.net/uploads/3a1b?sv=1&sig=x".into(),
            custom_domain: Some("files.example.com".into()),
            final_url: None,
        };
        assert_eq!(d.final_url(), "https://files.example.com/uploads/3a1b");
    }

    #[test]
    fn azure_explicit_final_url_wins() {
        let d = Destination::AzureBlob {
            sas_url: "https://a.blob.core.windows.net/c/b?sv=1".into(),
            custom_domain: Some("ignored.example.com".into()),
            final_url: Some("https://cdn.example.com/file".into()),
        };
        assert_eq!(d.final_url(), "https://cdn.example.com/file");
    }

    #[test]
    fn validate_rejects_non_https() {
        let d = azure("http://a.blob.core.windows.net/c/b?sv=1");
        assert!(d.validate(1).is_err());
    }

    #[test]
    fn validate_rejects_azure_without_query() {
        let d = azure("https://a.blob.core.windows.net/c/b");
        assert!(d.validate(1).is_err());
    }

    #[test]
    fn validate_accepts_good_azure() {
        let d = azure("https://a.blob.core.windows.net/c/b?sv=1&sig=x");
        assert!(d.validate(5).is_ok());
    }

    #[test]
    fn s3_part_count_must_match() {
        let d = Destination::S3Multipart {
            part_urls: vec!["https://s3/p1".into(), "https://s3/p2".into()],
            complete_url: "https://s3/complete".into(),
            abort_url: "https://s3/abort".into(),
            final_url: "https://s3/final".into(),
        };
        assert!(d.validate(2).is_ok());
        assert!(d.validate(3).is_err());
    }

    #[test]
    fn s3_requires_https_everywhere() {
        let d = Destination::S3Multipart {
            part_urls: vec!["https://s3/p1".into()],
            complete_url: "http://s3/complete".into(),
            abort_url: "https://s3/abort".into(),
            final_url: "https://s3/final".into(),
        };
        assert!(d.validate(1).is_err());
    }

    #[test]
    fn discard_needs_https_final() {
        assert!(Destination::Discard {
            final_url: "https://x/y".into()
        }
        .validate(0)
        .is_ok());
        assert!(Destination::Discard {
            final_url: "ftp://x/y".into()
        }
        .validate(0)
        .is_err());
    }

    /// A minimal session for state-helper tests (discard destination).
    fn session(content_length: Option<u64>, chunk_size: u64, chunk_count: u64) -> SessionState {
        SessionState {
            id: "id".into(),
            file_name: "f".into(),
            content_type: "text/plain".into(),
            content_length,
            chunk_size,
            chunk_count,
            upload_token: "u".into(),
            delete_token: "d".into(),
            destination: Destination::Discard {
                final_url: "https://x/y".into(),
            },
            final_url: "https://x/y".into(),
            status: SessionStatus::Uploading,
            staged: vec![false; chunk_count as usize],
            relayed: vec![None; chunk_count as usize],
            final_index: None,
            final_chunk_len: None,
            lazy_part_urls: Vec::new(),
            last_activity_ms: 0.0,
            created_ms: 0.0,
        }
    }

    #[test]
    fn ordered_relay_results_gated_on_completeness() {
        let mut s = session(Some(3), 1, 3);
        assert!(s.ordered_relay_results().is_none());
        s.relayed = vec![Some("a".into()), Some("b".into()), Some("c".into())];
        assert_eq!(s.ordered_relay_results(), Some(vec!["a".into(), "b".into(), "c".into()]));
        assert!(s.all_relayed());
    }

    #[test]
    fn create_request_content_length_is_optional() {
        // absent → unknown length
        let json = r#"{"destination":{"type":"discard","finalUrl":"https://example.com/x"}}"#;
        let req: CreateRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.content_length, None);
        // explicit null → unknown length
        let json = r#"{"contentLength":null,"destination":{"type":"discard","finalUrl":"https://example.com/x"}}"#;
        let req: CreateRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.content_length, None);
        // present → known length, unchanged
        let json = r#"{"contentLength":7,"destination":{"type":"discard","finalUrl":"https://example.com/x"}}"#;
        let req: CreateRequest = serde_json::from_str(json).unwrap();
        assert_eq!(req.content_length, Some(7));
    }

    fn s3(part_urls: Vec<String>, complete: &str) -> Destination {
        Destination::S3Multipart {
            part_urls,
            complete_url: complete.into(),
            abort_url: "https://s3/abort".into(),
            final_url: "https://s3/final".into(),
        }
    }

    #[test]
    fn validate_unknown_s3_requires_empty_part_urls() {
        assert!(s3(vec![], "https://s3/complete")
            .validate_unknown()
            .is_ok());
        assert!(s3(vec!["https://s3/p1".into()], "https://s3/complete")
            .validate_unknown()
            .is_err());
        // control URLs are still https-checked
        assert!(s3(vec![], "http://s3/complete")
            .validate_unknown()
            .is_err());
    }

    #[test]
    fn validate_unknown_azure_and_discard_unchanged() {
        assert!(azure("https://a.blob.core.windows.net/c/b?sv=1&sig=x")
            .validate_unknown()
            .is_ok());
        assert!(azure("https://a.blob.core.windows.net/c/b")
            .validate_unknown()
            .is_err());
        assert!(azure("http://a.blob.core.windows.net/c/b?sv=1")
            .validate_unknown()
            .is_err());
        assert!(Destination::Discard {
            final_url: "https://x/y".into()
        }
        .validate_unknown()
        .is_ok());
        assert!(Destination::Discard {
            final_url: "ftp://x/y".into()
        }
        .validate_unknown()
        .is_err());
    }

    #[test]
    fn part_url_prefers_create_time_then_lazy() {
        // known-length: create-time part URLs
        let mut s = session(Some(2), 1, 2);
        s.destination = s3(vec!["https://s3/p1".into(), "https://s3/p2".into()], "https://s3/complete");
        assert_eq!(s.part_url(1), Some("https://s3/p2".into()));
        // unknown-length: create-time list is empty → lazily stored URLs win
        let mut s = session(None, 1, 0);
        s.destination = s3(vec![], "https://s3/complete");
        assert_eq!(s.part_url(0), None);
        s.ensure_chunk_slot(1);
        s.lazy_part_urls[1] = Some("https://s3/lazy2".into());
        assert_eq!(s.part_url(1), Some("https://s3/lazy2".into()));
        assert_eq!(s.part_url(0), None);
    }

    #[test]
    fn chunk_slots_grow_on_demand() {
        let mut s = session(None, 4, 0);
        assert!(s.staged.is_empty());
        s.ensure_chunk_slot(4);
        assert_eq!(s.staged.len(), 5);
        assert_eq!(s.relayed.len(), 5);
        assert_eq!(s.lazy_part_urls.len(), 5);
        assert_eq!(s.highest_staged(), None);
        s.staged[2] = true;
        s.staged[4] = true;
        assert_eq!(s.highest_staged(), Some(4));
        // never shrinks
        s.ensure_chunk_slot(1);
        assert_eq!(s.staged.len(), 5);
    }

    #[test]
    fn computed_total_is_final_index_times_chunk_size_plus_final_len() {
        let mut s = session(None, 16, 0);
        assert_eq!(s.computed_total(), None);
        s.final_index = Some(4);
        assert_eq!(s.computed_total(), None); // final length not recorded yet
        s.final_chunk_len = Some(3);
        assert_eq!(s.computed_total(), Some(4 * 16 + 3));
        // a single final chunk (F = 0) is just its own length
        s.final_index = Some(0);
        s.final_chunk_len = Some(7);
        assert_eq!(s.computed_total(), Some(7));
    }

    #[test]
    fn stream_end_by_session_kind() {
        // known-length: always the fixed plan count
        let s = session(Some(48), 16, 3);
        assert_eq!(s.stream_end(), Some(3));
        // unknown-length: unknown until the final chunk is marked
        let mut s = session(None, 16, 0);
        assert_eq!(s.stream_end(), None);
        s.final_index = Some(2);
        assert_eq!(s.stream_end(), Some(3));
        // after /complete fixes the total, the count takes over
        s.content_length = Some(40);
        s.chunk_count = 3;
        assert_eq!(s.stream_end(), Some(3));
    }

    #[test]
    fn state_deserializes_records_from_previous_code() {
        // A record persisted by the pre-unknown-length build (no finalIndex /
        // finalChunkLen / lazyPartUrls) must load with defaults — a deploy can
        // land mid-session.
        let json = r#"{
            "id":"abcdefgh","fileName":"a.bin","contentType":"application/octet-stream",
            "contentLength":100,"chunkSize":50,"chunkCount":2,
            "uploadToken":"u","deleteToken":"d",
            "destination":{"type":"discard","finalUrl":"https://x/y"},
            "finalUrl":"https://x/y","status":"uploading",
            "staged":[true,false],"relayed":[null,null],
            "lastActivityMs":1.0,"createdMs":1.0
        }"#;
        let s: SessionState = serde_json::from_str(json).unwrap();
        assert_eq!(s.content_length, Some(100));
        assert!(!s.is_unknown_length());
        assert_eq!(s.final_index, None);
        assert_eq!(s.final_chunk_len, None);
        assert!(s.lazy_part_urls.is_empty());
        assert_eq!(s.stream_end(), Some(2));
    }
}
