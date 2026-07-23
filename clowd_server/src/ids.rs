//! Id and capability-token generation + validation.
//!
//! Parity with the legacy C# `UploadRegistry` (`RandomToken`) and
//! `RedirectStore.IsValidId`:
//! - id  = 12 random bytes, url-safe base64, no padding  (→ 16 chars)
//! - token = 32 random bytes, url-safe base64, no padding (→ 43 chars)
//! - id validation regex `^[A-Za-z0-9_-]{8,64}$` (doubles as a path-traversal guard)
//! - token comparison is constant-time (`CryptographicOperations.FixedTimeEquals`).

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

/// Bytes of entropy in an upload id (parity with `UploadRegistry.RandomToken(12)`).
pub const ID_BYTES: usize = 12;
/// Bytes of entropy in a capability token (parity with `RandomToken(32)`).
pub const TOKEN_BYTES: usize = 32;

/// url-safe base64 (no padding) of `n` cryptographically-random bytes.
fn random_token(n: usize) -> String {
    let mut buf = vec![0u8; n];
    getrandom::getrandom(&mut buf).expect("platform RNG unavailable");
    URL_SAFE_NO_PAD.encode(&buf)
}

/// A fresh 12-byte upload id.
pub fn new_id() -> String {
    random_token(ID_BYTES)
}

/// A fresh 32-byte capability token (upload or delete).
pub fn new_token() -> String {
    random_token(TOKEN_BYTES)
}

/// `^[A-Za-z0-9_-]{8,64}$` — the id charset is url-safe base64, and the regex
/// doubles as a path-traversal guard. Invalid ids short-circuit to not-found.
pub fn is_valid_id(id: &str) -> bool {
    let len = id.len();
    if !(8..=64).contains(&len) {
        return false;
    }
    id.bytes()
        .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
}

/// Constant-time token comparison over UTF-8 bytes. A length mismatch returns
/// false immediately (the length is not secret; parity with `FixedTimeEquals`).
pub fn token_matches(presented: &str, expected: &str) -> bool {
    let a = presented.as_bytes();
    let b = expected.as_bytes();
    if a.len() != b.len() {
        return false;
    }
    a.ct_eq(b).into()
}

/// SHA-256 (url-safe base64, no padding) of a capability token. Stored in the KV
/// redirect record so `DELETE /uploads/{id}` can be authorized directly against
/// KV after the Durable Object's post-completion linger has wiped its storage
/// (the short link outlives the session — see REFACTOR §3.1/§4.4).
pub fn hash_token(token: &str) -> String {
    let digest = Sha256::digest(token.as_bytes());
    URL_SAFE_NO_PAD.encode(digest)
}

/// Constant-time check that `presented` hashes to `expected_hash` (both are the
/// url-safe base64 SHA-256 produced by [`hash_token`], hence equal length).
pub fn hash_matches(presented: &str, expected_hash: &str) -> bool {
    let computed = hash_token(presented);
    let a = computed.as_bytes();
    let b = expected_hash.as_bytes();
    if a.len() != b.len() {
        return false;
    }
    a.ct_eq(b).into()
}

/// Extract a bearer token from an `Authorization` header value, if present.
pub fn bearer(header_value: Option<&str>) -> Option<&str> {
    let v = header_value?;
    let rest = v
        .strip_prefix("Bearer ")
        .or_else(|| v.strip_prefix("bearer "))?;
    let rest = rest.trim();
    if rest.is_empty() {
        None
    } else {
        Some(rest)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn id_is_16_chars_and_valid() {
        let id = new_id();
        assert_eq!(id.len(), 16, "12 bytes url-safe b64 no-pad == 16 chars");
        assert!(is_valid_id(&id));
    }

    #[test]
    fn token_is_43_chars() {
        let t = new_token();
        assert_eq!(t.len(), 43, "32 bytes url-safe b64 no-pad == 43 chars");
    }

    #[test]
    fn ids_are_unique() {
        let a = new_id();
        let b = new_id();
        assert_ne!(a, b);
    }

    #[test]
    fn valid_id_accepts_url_safe_charset() {
        assert!(is_valid_id("abcABC012_-xy"));
        assert!(is_valid_id("aj20lajkQ1x0")); // 12 chars
        assert!(is_valid_id("8fz-K2v1Qx0pLmNa"));
    }

    #[test]
    fn valid_id_rejects_bad_input() {
        assert!(!is_valid_id(""));
        assert!(!is_valid_id("short7c")); // 7 chars
        assert!(!is_valid_id("has space1234"));
        assert!(!is_valid_id("../etc/passwd")); // path traversal
        assert!(!is_valid_id("dot.name1234"));
        assert!(!is_valid_id("plus+slash/1234"));
        assert!(!is_valid_id(&"x".repeat(65))); // too long
    }

    #[test]
    fn token_match_is_correct() {
        assert!(token_matches("secret-token", "secret-token"));
        assert!(!token_matches("secret-token", "secret-tokeN"));
        assert!(!token_matches("short", "longer-token"));
        assert!(!token_matches("", "x"));
        assert!(token_matches("", ""));
    }

    #[test]
    fn hash_token_is_stable_and_matches() {
        let tok = "some-delete-token";
        let h = hash_token(tok);
        assert_eq!(h, hash_token(tok), "hash is deterministic");
        assert!(hash_matches(tok, &h));
        assert!(!hash_matches("wrong-token", &h));
        assert!(!hash_matches(tok, "not-a-hash"));
    }

    #[test]
    fn bearer_parsing() {
        assert_eq!(bearer(Some("Bearer abc")), Some("abc"));
        assert_eq!(bearer(Some("bearer abc")), Some("abc"));
        assert_eq!(bearer(Some("Bearer   spaced  ")), Some("spaced"));
        assert_eq!(bearer(Some("Basic abc")), None);
        assert_eq!(bearer(Some("Bearer ")), None);
        assert_eq!(bearer(None), None);
    }
}
