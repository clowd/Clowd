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
    /// REQUIRED in v2 (client always knows it; enables Content-Length on tails).
    pub content_length: i64,
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

fn is_https(url: &str) -> bool {
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
    pub content_length: u64,
    pub chunk_size: u64,
    pub chunk_count: u64,
    pub upload_token: String,
    pub delete_token: String,
    pub destination: Destination,
    pub final_url: String,
    pub status: SessionStatus,
    /// Which chunks have been staged to R2.
    pub staged: Vec<bool>,
    /// Per-chunk relay result: block id (azure) / ETag (s3) / marker (discard).
    pub relayed: Vec<Option<String>>,
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
        assert_eq!(req.content_length, 100);
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

    #[test]
    fn ordered_relay_results_gated_on_completeness() {
        let mut s = SessionState {
            id: "id".into(),
            file_name: "f".into(),
            content_type: "text/plain".into(),
            content_length: 3,
            chunk_size: 1,
            chunk_count: 3,
            upload_token: "u".into(),
            delete_token: "d".into(),
            destination: Destination::Discard {
                final_url: "https://x/y".into(),
            },
            final_url: "https://x/y".into(),
            status: SessionStatus::Uploading,
            staged: vec![false, false, false],
            relayed: vec![None, None, None],
            last_activity_ms: 0.0,
            created_ms: 0.0,
        };
        assert!(s.ordered_relay_results().is_none());
        s.relayed = vec![Some("a".into()), Some("b".into()), Some("c".into())];
        assert_eq!(s.ordered_relay_results(), Some(vec!["a".into(), "b".into(), "c".into()]));
        assert!(s.all_relayed());
    }
}
