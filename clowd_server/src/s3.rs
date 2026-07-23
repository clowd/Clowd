//! S3 multipart relay helpers (pure, host-testable).
//!
//! The client presigns `partUrls[]` (UNSIGNED-PAYLOAD), `completeUrl`, and
//! `abortUrl` itself — the server never holds account keys (REFACTOR §6). The
//! relay collects one ETag per part; commit posts the standard XML to
//! `completeUrl`; abort issues the presigned AbortMultipartUpload.

/// XML body for `CompleteMultipartUpload`. `etags[i]` is the ETag for part `i+1`
/// (S3 part numbers are 1-based). ETags are emitted verbatim (S3 returns them
/// quoted; the value is passed through unchanged).
pub fn complete_multipart_xml(etags: &[String]) -> String {
    let mut xml = String::from("<CompleteMultipartUpload>");
    for (i, etag) in etags.iter().enumerate() {
        let part_number = i + 1;
        xml.push_str("<Part><PartNumber>");
        xml.push_str(&part_number.to_string());
        xml.push_str("</PartNumber><ETag>");
        xml.push_str(&xml_escape(etag));
        xml.push_str("</ETag></Part>");
    }
    xml.push_str("</CompleteMultipartUpload>");
    xml
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn complete_xml_numbers_parts_from_one() {
        let xml = complete_multipart_xml(&["\"abc\"".into(), "\"def\"".into()]);
        assert!(xml.contains("<PartNumber>1</PartNumber><ETag>\"abc\"</ETag>"));
        assert!(xml.contains("<PartNumber>2</PartNumber><ETag>\"def\"</ETag>"));
        assert!(xml.starts_with("<CompleteMultipartUpload>"));
        assert!(xml.ends_with("</CompleteMultipartUpload>"));
    }

    #[test]
    fn empty_is_wellformed() {
        assert_eq!(complete_multipart_xml(&[]), "<CompleteMultipartUpload></CompleteMultipartUpload>");
    }

    #[test]
    fn escapes_xml_metachars() {
        let xml = complete_multipart_xml(&["a&b<c".into()]);
        assert!(xml.contains("a&amp;b&lt;c"));
    }
}
