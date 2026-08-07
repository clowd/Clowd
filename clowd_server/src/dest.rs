//! Outbound destination relay/commit/abort against capability URLs (wasm only).
//!
//! Parity with the legacy `AzureBlobDestination`: blocks are staged as they
//! arrive and only become visible once the block list commits — which is exactly
//! why tails serve from R2 staging until completion. Abort deliberately does
//! nothing to Azure (uncommitted blocks are GC'd after ~7 days).

use worker::{Method, Result};

use crate::model::Destination;
use crate::sanitize;
use crate::wasm_util::{ensure_success, send};
use crate::{azure, s3};

/// Relay one chunk to its destination. Returns the per-chunk relay result:
/// azure block id / s3 ETag / a marker for discard. Idempotent by construction
/// (fixed block ids / part numbers).
///
/// `part_url` is the presigned S3 UploadPart URL for chunk `n`, resolved by the
/// caller via `SessionState::part_url` — create-time for known-length sessions,
/// lazily collected (`x-clowd-part-url`) for unknown-length ones. Ignored for
/// azure/discard.
pub async fn relay_chunk(dest: &Destination, n: u64, bytes: Vec<u8>, part_url: Option<&str>) -> Result<String> {
    match dest {
        Destination::AzureBlob {
            sas_url,
            ..
        } => {
            let url = azure::put_block_url(sas_url, n);
            let resp = send(&url, Method::Put, Some(bytes), &[]).await?;
            ensure_success(resp, "azure Put Block").await?;
            Ok(azure::block_id(n))
        }
        Destination::S3Multipart {
            ..
        } => {
            let url = part_url.ok_or_else(|| worker::Error::RustError(format!("no presigned part url for chunk {n}")))?;
            let resp = send(url, Method::Put, Some(bytes), &[]).await?;
            let resp = ensure_success(resp, "s3 UploadPart").await?;
            let etag = resp
                .headers()
                .get("etag")?
                .unwrap_or_default();
            if etag.is_empty() {
                return Err(worker::Error::RustError("s3 UploadPart returned no ETag".into()));
            }
            Ok(etag)
        }
        Destination::Discard {
            ..
        } => Ok("discard".into()),
    }
}

/// Commit the destination object. `results` are the per-chunk relay results in
/// chunk order (used by S3; Azure recomputes block ids deterministically).
pub async fn commit(dest: &Destination, results: &[String], content_type: &str, file_name: &str, chunk_count: u64) -> Result<()> {
    match dest {
        Destination::AzureBlob {
            sas_url,
            ..
        } => {
            let url = azure::put_block_list_url(sas_url);
            let xml = azure::block_list_xml(chunk_count);
            let disposition = sanitize::content_disposition(Some(file_name));
            let headers = [
                ("x-ms-version", "2021-08-06"),
                ("x-ms-blob-content-type", content_type),
                ("x-ms-blob-content-disposition", disposition.as_str()),
                ("Content-Type", "application/xml"),
            ];
            let resp = send(&url, Method::Put, Some(xml.into_bytes()), &headers).await?;
            ensure_success(resp, "azure Put Block List").await?;
        }
        Destination::S3Multipart {
            complete_url,
            ..
        } => {
            let xml = s3::complete_multipart_xml(results);
            let resp = send(
                complete_url,
                Method::Post,
                Some(xml.into_bytes()),
                &[("Content-Type", "application/xml")],
            )
            .await?;
            ensure_success(resp, "s3 CompleteMultipartUpload").await?;
        }
        Destination::Discard {
            ..
        } => {}
    }
    Ok(())
}

/// Best-effort abort. S3 issues the presigned AbortMultipartUpload; Azure and
/// discard do nothing (uncommitted Azure blocks are GC'd; not committing is the
/// abort — parity with `AzureBlobDestination.AbortAsync`).
pub async fn abort(dest: &Destination) -> Result<()> {
    if let Destination::S3Multipart {
        abort_url,
        ..
    } = dest
    {
        // Best effort: ignore the status — nothing to recover if it fails.
        let _ = send(abort_url, Method::Delete, None, &[]).await;
    }
    Ok(())
}
