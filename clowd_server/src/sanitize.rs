//! Filename sanitization for `Content-Disposition` (parity with `Creds.SanitizeFileName`,
//! Creds.cs:22-28): strip control chars, `"` and `\`, fall back to "file".

use percent_encoding::{utf8_percent_encode, AsciiSet, NON_ALPHANUMERIC};

/// RFC 5987 `attr-char` set: `ALPHA / DIGIT / "!" "#" "$" "&" "+" "-" "." "^" "_"
/// "`" "|" "~"` are left as-is; everything else (including all non-ASCII) is
/// percent-encoded. Built by taking `NON_ALPHANUMERIC` and un-marking the
/// allowed punctuation.
const ATTR_CHAR: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'!')
    .remove(b'#')
    .remove(b'$')
    .remove(b'&')
    .remove(b'+')
    .remove(b'-')
    .remove(b'.')
    .remove(b'^')
    .remove(b'_')
    .remove(b'`')
    .remove(b'|')
    .remove(b'~');

pub fn sanitize_filename(name: Option<&str>) -> String {
    let Some(name) = name else {
        return "file".into();
    };
    let cleaned: String = name
        .chars()
        .filter(|c| !c.is_control() && *c != '"' && *c != '\\')
        .collect();
    if cleaned.trim().is_empty() {
        "file".into()
    } else {
        cleaned
    }
}

/// `Content-Disposition: inline; …` header value.
///
/// JS `Headers` values are ByteStrings (ISO-8859-1), so a raw non-Latin-1
/// filename (e.g. `スクリーンショット.mp4`, very likely for a screen-capture app)
/// would make `Headers.set` throw a `TypeError`. For non-ASCII names we emit both
/// an ASCII `filename` fallback and an RFC 5987 `filename*=UTF-8''…` parameter so
/// modern browsers show the correct name and the header value stays ASCII.
pub fn content_disposition(name: Option<&str>) -> String {
    let name = sanitize_filename(name);
    if name.is_ascii() {
        return format!("inline; filename=\"{name}\"");
    }
    let ascii_fallback: String = name
        .chars()
        .map(|c| if c.is_ascii_graphic() || c == ' ' { c } else { '_' })
        .collect();
    let ascii_fallback = if ascii_fallback.trim().is_empty() {
        "file".to_string()
    } else {
        ascii_fallback
    };
    let encoded = utf8_percent_encode(&name, ATTR_CHAR).to_string();
    format!("inline; filename=\"{ascii_fallback}\"; filename*=UTF-8''{encoded}")
}

/// Make a client-supplied content type safe to put in an HTTP header value.
///
/// Content types are ASCII by spec, but creation is unauthenticated and the value
/// is echoed into the tail `Content-Type` and the Azure `x-ms-blob-content-type`
/// header — a non-Latin-1 byte would make `Headers.set` throw. Keep only printable
/// ASCII, fall back to `application/octet-stream` when nothing usable remains.
pub fn header_safe_content_type(ct: &str) -> String {
    let cleaned: String = ct
        .chars()
        .filter(|c| c.is_ascii_graphic() || *c == ' ')
        .collect();
    let trimmed = cleaned.trim();
    if trimmed.is_empty() {
        "application/octet-stream".to_string()
    } else {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_normal_names() {
        assert_eq!(sanitize_filename(Some("recording.mp4")), "recording.mp4");
        assert_eq!(sanitize_filename(Some("my file (1).png")), "my file (1).png");
    }

    #[test]
    fn strips_dangerous_chars() {
        assert_eq!(sanitize_filename(Some("a\"b\\c")), "abc");
        assert_eq!(sanitize_filename(Some("line\nbreak\t")), "linebreak");
    }

    #[test]
    fn falls_back_to_file() {
        assert_eq!(sanitize_filename(None), "file");
        assert_eq!(sanitize_filename(Some("")), "file");
        assert_eq!(sanitize_filename(Some("\"\"")), "file");
        assert_eq!(sanitize_filename(Some("   ")), "file");
    }

    #[test]
    fn disposition_format() {
        assert_eq!(content_disposition(Some("a.png")), "inline; filename=\"a.png\"");
        assert_eq!(content_disposition(None), "inline; filename=\"file\"");
    }

    #[test]
    fn disposition_rfc5987_for_non_ascii() {
        let d = content_disposition(Some("スクリーンショット.mp4"));
        // stays pure ASCII (safe for a ByteString header value)
        assert!(d.is_ascii(), "header value must be ASCII: {d}");
        // ASCII fallback replaces each non-ASCII char (9 katakana), extension kept
        assert!(d.contains("filename=\"_________.mp4\""), "{d}");
        // RFC 5987 parameter carries the UTF-8 percent-encoded name
        assert!(d.contains("filename*=UTF-8''"), "{d}");
        assert!(d.contains("%E3%82%B9"), "must percent-encode utf-8 bytes: {d}");
    }

    #[test]
    fn content_type_is_header_safe() {
        assert_eq!(header_safe_content_type("video/mp4"), "video/mp4");
        assert_eq!(header_safe_content_type("  "), "application/octet-stream");
        assert_eq!(header_safe_content_type(""), "application/octet-stream");
        // strips non-Latin-1 / non-printable bytes
        assert!(header_safe_content_type("text/html\u{1F4A9}").is_ascii());
        assert_eq!(header_safe_content_type("тип"), "application/octet-stream");
    }
}
