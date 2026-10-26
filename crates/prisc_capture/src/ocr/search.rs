//! SEARCH action URL builder. Pure string work — the ShellExecuteW /
//! `open` launch lives in the platform interop layer, not here.

/// Cap on the query length in CHARS (not bytes): keeps the URL well under
/// every browser/OS command-line limit even when each char percent-encodes
/// to 9 bytes (a 4-byte UTF-8 sequence).
const MAX_QUERY_CHARS: usize = 200;

/// When truncating, prefer breaking at the last space within this many
/// chars of the limit so we don't chop a word in half; beyond that window a
/// space-break would discard too much of the query to be worth it.
const BREAK_WINDOW_CHARS: usize = 40;

/// Build the Google search URL for a lifted OCR result, or None when the
/// text has no searchable content. Hardcoded engine — a configurable search
/// engine is explicitly out of scope for v1.
pub fn search_url(text: &str) -> Option<String> {
    let query = normalize_and_truncate(text)?;
    Some(format!("https://www.google.com/search?q={}", percent_encode(&query)))
}

/// Collapse all whitespace runs (spaces, newlines, tabs) to single spaces,
/// trim, and truncate to MAX_QUERY_CHARS. Separate from search_url so the
/// char-count invariants are directly testable without decoding the URL.
fn normalize_and_truncate(text: &str) -> Option<String> {
    // split_whitespace both trims and collapses runs — OCR output is full of
    // line breaks and double spaces that would otherwise bloat the query.
    let normalized = text
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if normalized.is_empty() {
        return None;
    }
    // char_indices, NEVER byte slicing: a byte cut through a CJK codepoint
    // would panic (or worse, silently produce invalid UTF-8 for the encoder).
    let Some((limit, _)) = normalized
        .char_indices()
        .nth(MAX_QUERY_CHARS)
    else {
        return Some(normalized); // short enough — keep everything
    };
    let head = &normalized[..limit];
    // Prefer the last space within the final BREAK_WINDOW_CHARS so the query
    // ends on a word boundary. `head` is exactly MAX_QUERY_CHARS chars, so
    // rev().nth(BREAK_WINDOW_CHARS - 1) always exists.
    let window_start = head
        .char_indices()
        .rev()
        .nth(BREAK_WINDOW_CHARS - 1)
        .map(|(i, _)| i)
        .unwrap_or(0);
    let cut = match head.rfind(' ') {
        Some(space) if space >= window_start => space,
        _ => limit,
    };
    Some(normalized[..cut].to_string())
}

/// Uppercase %XX per UTF-8 byte, everything outside RFC 3986 unreserved
/// [A-Za-z0-9-._~]. Hand-rolled on purpose: ~15 lines beats a new
/// dependency, and encoding EVERY reserved char (including space as %20,
/// not '+') is unambiguous for a query parameter.
fn percent_encode(s: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut out = String::with_capacity(s.len() * 3);
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => out.push(b as char),
            _ => {
                out.push('%');
                out.push(HEX[(b >> 4) as usize] as char);
                out.push(HEX[(b & 0x0F) as usize] as char);
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Exact-string pin: the simplest query, space as %20.
    #[test]
    fn encodes_simple_query() {
        assert_eq!(
            search_url("hello world").as_deref(),
            Some("https://www.google.com/search?q=hello%20world")
        );
    }

    /// Every URL-significant char must be encoded — a raw & or # would
    /// truncate or split the query on Google's side, a raw + would decode
    /// as a space and silently corrupt the search.
    #[test]
    fn encodes_reserved_characters() {
        assert_eq!(
            search_url("a&b=c?d#e+f%g h").as_deref(),
            Some("https://www.google.com/search?q=a%26b%3Dc%3Fd%23e%2Bf%25g%20h")
        );
    }

    /// OCR full_text is newline-joined; the query must read as one line.
    #[test]
    fn collapses_multiline_input() {
        assert_eq!(
            search_url("first line\nsecond\t\tline\r\n third ").as_deref(),
            Some("https://www.google.com/search?q=first%20line%20second%20line%20third")
        );
    }

    #[test]
    fn trims_surrounding_whitespace() {
        assert_eq!(search_url("  hi  ").as_deref(), Some("https://www.google.com/search?q=hi"));
    }

    /// Empty and whitespace-only lift results have nothing to search.
    #[test]
    fn empty_input_is_none() {
        assert_eq!(search_url(""), None);
        assert_eq!(search_url(" \n\t "), None);
    }

    /// 300 CJK chars: the truncation must count CHARS and cut on a char
    /// boundary — byte slicing here would panic mid-codepoint.
    #[test]
    fn truncates_cjk_on_char_boundary() {
        let input = "漢".repeat(300);
        let query = normalize_and_truncate(&input).expect("non-empty");
        assert_eq!(query.chars().count(), MAX_QUERY_CHARS); // no spaces to prefer
        let url = search_url(&input).expect("non-empty");
        assert!(url.is_ascii(), "percent-encoding must produce pure ASCII");
    }

    /// With a space inside the break window, the cut lands on it; the word
    /// fragment after it is dropped rather than chopped mid-word.
    #[test]
    fn truncation_prefers_word_boundary() {
        // 190 chars, a space, then a long word crossing the 200-char limit.
        let input = format!("{} {}", "a".repeat(190), "b".repeat(50));
        let query = normalize_and_truncate(&input).expect("non-empty");
        assert_eq!(query, "a".repeat(190));
    }

    /// No space anywhere near the limit: hard cut at exactly the limit.
    #[test]
    fn truncation_hard_cuts_unbroken_text() {
        let input = "x".repeat(500);
        let query = normalize_and_truncate(&input).expect("non-empty");
        assert_eq!(query.chars().count(), MAX_QUERY_CHARS);
    }

    /// 4-byte scalars (emoji) stress both char-boundary truncation and the
    /// per-byte encoder.
    #[test]
    fn emoji_does_not_panic() {
        let input = "🦀".repeat(250);
        let url = search_url(&input).expect("non-empty");
        assert!(url.is_ascii());
        // Crab is F0 9F A6 80 — spot-check the first encoded scalar.
        assert!(url.contains("%F0%9F%A6%80"));
    }
}
