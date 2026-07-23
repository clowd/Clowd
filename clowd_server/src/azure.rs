//! Azure Block Blob relay helpers (pure, host-testable).
//!
//! The new server does raw REST against a client-supplied **blob-level SAS URL**
//! (`create+write`), replacing the legacy Azure SDK `OpenWriteAsync` over a
//! container SAS (`AzureBlobDestination.cs`). Block ids are the base64 of a
//! fixed-width zero-padded chunk number so that:
//!   * every block id is the same length (an Azure `Put Block` requirement), and
//!   * a retry recomputes the identical id → the relay is idempotent.

use base64::engine::general_purpose::STANDARD;
use base64::Engine;
use percent_encoding::{utf8_percent_encode, NON_ALPHANUMERIC};

/// Width of the zero-padded chunk number before base64. 5 digits covers up to
/// 99_999 chunks (our max upload is ~640), and keeps every block id 8 chars.
const BLOCK_INDEX_WIDTH: usize = 5;

/// Deterministic, fixed-length Azure block id for chunk `n`.
/// e.g. n=17 → "00017" → base64 "MDAwMTc=".
pub fn block_id(n: u64) -> String {
    let label = format!("{n:0width$}", width = BLOCK_INDEX_WIDTH);
    STANDARD.encode(label.as_bytes())
}

/// XML body for `Put Block List`, listing every block 0..count in chunk order.
pub fn block_list_xml(count: u64) -> String {
    let mut xml = String::from("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
    for n in 0..count {
        xml.push_str("<Latest>");
        xml.push_str(&block_id(n));
        xml.push_str("</Latest>");
    }
    xml.push_str("</BlockList>");
    xml
}

/// Append query parameters to a SAS URL that already carries a `?sv=…` query,
/// preserving the existing signature.
pub fn append_query(sas_url: &str, extra: &str) -> String {
    if sas_url.contains('?') {
        format!("{sas_url}&{extra}")
    } else {
        format!("{sas_url}?{extra}")
    }
}

/// `Put Block` URL for chunk `n`.
pub fn put_block_url(sas_url: &str, n: u64) -> String {
    let encoded = utf8_percent_encode(&block_id(n), NON_ALPHANUMERIC).to_string();
    append_query(sas_url, &format!("comp=block&blockid={encoded}"))
}

/// `Put Block List` (commit) URL.
pub fn put_block_list_url(sas_url: &str) -> String {
    append_query(sas_url, "comp=blocklist")
}

/// Final public URL: the SAS URL minus its query string.
pub fn strip_query(url: &str) -> String {
    match url.split_once('?') {
        Some((base, _)) => base.to_string(),
        None => url.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;

    #[test]
    fn block_ids_are_fixed_length_and_deterministic() {
        let a = block_id(0);
        let b = block_id(17);
        let c = block_id(99_999);
        assert_eq!(a.len(), b.len());
        assert_eq!(b.len(), c.len());
        // deterministic → idempotent retries
        assert_eq!(block_id(17), "MDAwMTc=");
        assert_eq!(a, block_id(0));
        // decodes back to the padded label
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(b.as_bytes())
            .unwrap();
        assert_eq!(decoded, b"00017");
    }

    #[test]
    fn block_list_orders_all_chunks() {
        let xml = block_list_xml(3);
        assert!(xml.starts_with("<?xml"));
        let i0 = xml.find(&block_id(0)).unwrap();
        let i1 = xml.find(&block_id(1)).unwrap();
        let i2 = xml.find(&block_id(2)).unwrap();
        assert!(i0 < i1 && i1 < i2, "blocks must be listed in chunk order");
        assert_eq!(xml.matches("<Latest>").count(), 3);
    }

    #[test]
    fn append_query_handles_existing_query() {
        assert_eq!(
            append_query("https://x/y?sv=1&sig=a", "comp=block"),
            "https://x/y?sv=1&sig=a&comp=block"
        );
        assert_eq!(append_query("https://x/y", "comp=block"), "https://x/y?comp=block");
    }

    #[test]
    fn put_block_url_encodes_block_id() {
        let u = put_block_url("https://acct.blob.core.windows.net/c/blob?sv=1&sig=a", 17);
        assert!(u.contains("comp=block"));
        assert!(u.contains("blockid="));
        // "=" padding of the base64 must be percent-encoded
        assert!(u.contains("%3D"), "block id base64 padding must be percent-encoded: {u}");
    }

    #[test]
    fn strip_query_yields_final_url() {
        assert_eq!(
            strip_query("https://acct.blob.core.windows.net/c/blob?sv=1&sig=a"),
            "https://acct.blob.core.windows.net/c/blob"
        );
        assert_eq!(
            strip_query("https://acct.blob.core.windows.net/c/blob"),
            "https://acct.blob.core.windows.net/c/blob"
        );
    }
}
