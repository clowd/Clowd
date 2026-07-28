//! Paste (hastebin-compatible) logic that needs no Workers runtime: key
//! generation, key/extension parsing, body limits, and the JSON body shapes.
//!
//! Parity with the vendored haste-server (`lib/document_handler.js`,
//! `lib/key_generators/phonetic.js`, `config.js`): 10-character phonetic keys,
//! a 400 000 byte limit, `key.split('.')[0]` extension stripping, and the exact
//! `{"key":…}` / `{"data":…,"key":…}` / `{"message":…}` response bodies.

use serde::Serialize;

/// Characters in a generated key (haste `config.js` `keyLength`).
pub const KEY_LENGTH: usize = 10;

/// Largest paste body accepted (haste `config.js` `maxLength`). Haste counts
/// UTF-16 code units of the decoded string; we count bytes, which is identical
/// for ASCII and stricter for everything else — the cap is a resource limit, not
/// an API contract.
pub const MAX_LENGTH: usize = 400_000;

/// Longest key accepted from a URL (path-safety bound, see [`is_valid_key`]).
pub const MAX_KEY_LENGTH: usize = 64;

/// `Cache-Control` for a stored paste. Pastes are write-once, so they can be
/// cached forever.
pub const IMMUTABLE_CACHE: &str = "public, max-age=31536000, immutable";

/// `Cache-Control` for the embedded frontend assets.
pub const STATIC_CACHE: &str = "public, max-age=3600";

/// Haste's 404 body message (`document_handler.js`).
pub const NOT_FOUND_MESSAGE: &str = "Document not found.";

/// Haste's 500 body message when the store rejects a write.
pub const STORE_ERROR_MESSAGE: &str = "Error adding document.";

/// Vowel alphabet (`randOf('aeiou')`).
const VOWELS: &[u8] = b"aeiou";
/// Consonant alphabet (`randOf('bcdfghjklmnpqrstvwxyz')`).
const CONSONANTS: &[u8] = b"bcdfghjklmnpqrstvwxyz";

/// Random bytes [`phonetic_key`] consumes for a `len`-character key: one to pick
/// the starting parity (haste's `Math.round(Math.random())`), then one per
/// character.
pub const fn key_random_bytes(len: usize) -> usize {
    len + 1
}

/// Port of haste `lib/key_generators/phonetic.js`: `len` characters alternating
/// consonant and vowel over the same alphabets.
///
/// The caller supplies the entropy so this stays host-testable: `random[0]`
/// chooses which parity gets the consonants, `random[1 + i]` chooses the
/// character at position `i`. A short `random` truncates the key rather than
/// reusing bytes; bytes past `key_random_bytes(len)` are ignored.
///
/// Reducing a byte modulo the alphabet size is very slightly non-uniform (256 is
/// not a multiple of 5 or 21). That matches haste, whose keys are collision
/// handles rather than secrets — the surviving keyspace is still ≈1.3e10 for the
/// default length, and unguessability is not part of the paste threat model.
pub fn phonetic_key(len: usize, random: &[u8]) -> String {
    let Some((&parity, rest)) = random.split_first() else {
        return String::new();
    };
    let start = (parity & 1) as usize;
    rest.iter()
        .take(len)
        .enumerate()
        .map(|(i, &b)| {
            let alphabet = if i % 2 == start { CONSONANTS } else { VOWELS };
            alphabet[b as usize % alphabet.len()] as char
        })
        .collect()
}

/// A fresh [`KEY_LENGTH`]-character phonetic key, drawing from the same platform
/// RNG as [`crate::ids::new_id`].
pub fn new_key() -> String {
    let mut buf = [0u8; key_random_bytes(KEY_LENGTH)];
    getrandom::fill(&mut buf).expect("platform RNG unavailable");
    phonetic_key(KEY_LENGTH, &buf)
}

/// `^[A-Za-z0-9]{1,64}$` — the generated-key charset, and a path-traversal guard
/// for keys arriving from the URL. Checked before any R2 access.
pub fn is_valid_key(key: &str) -> bool {
    !key.is_empty()
        && key.len() <= MAX_KEY_LENGTH
        && key
            .bytes()
            .all(|b| b.is_ascii_alphanumeric())
}

/// `key.split('.')[0]` — the URL extension selects syntax highlighting in the
/// browser and is not part of the stored key (`document_handler.js`).
pub fn strip_extension(id: &str) -> &str {
    match id.split_once('.') {
        Some((key, _)) => key,
        None => id,
    }
}

/// Why a `POST /p/documents` body was rejected. Each maps to a 400 with the
/// message as the JSON `message` field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BodyError {
    /// Nothing to store. Haste's fork would happily save an empty document; we
    /// reject it so an accidental empty POST doesn't burn a key.
    Empty,
    /// Over [`MAX_LENGTH`] — haste's own wording.
    TooLong,
    /// Not decodable as UTF-8. Haste is JS and cannot express this case; pastes
    /// are text, so binary is a client bug.
    NotUtf8,
}

impl BodyError {
    /// The `message` field of the 400 JSON body.
    pub fn message(self) -> &'static str {
        match self {
            BodyError::Empty => "Document is empty.",
            BodyError::TooLong => "Document exceeds maximum length.",
            BodyError::NotUtf8 => "Document must be valid UTF-8 text.",
        }
    }
}

/// Validate a raw request body and borrow it as text. Order matches haste: size
/// first (so an oversized body is rejected without decoding it).
pub fn validate_body(bytes: &[u8]) -> Result<&str, BodyError> {
    if bytes.is_empty() {
        return Err(BodyError::Empty);
    }
    if bytes.len() > MAX_LENGTH {
        return Err(BodyError::TooLong);
    }
    std::str::from_utf8(bytes).map_err(|_| BodyError::NotUtf8)
}

/// Accumulator for a request body arriving in chunks, which never holds more
/// than [`MAX_LENGTH`] bytes.
///
/// Paste creation is unauthenticated and the platform request cap is orders of
/// magnitude above [`MAX_LENGTH`], so buffering a whole body before checking its
/// size would let any client force multi-megabyte allocations in the shared
/// isolate. Once a body goes over the limit the buffer is dropped and every
/// later chunk ignored — the caller still has to read the stream to EOF before
/// responding, but the overflow is discarded as it arrives rather than kept.
#[derive(Debug, Default)]
pub struct BodyAccumulator {
    bytes: Vec<u8>,
    over_limit: bool,
}

impl BodyAccumulator {
    /// Append one streamed chunk, or note the overflow and retain nothing.
    pub fn push(&mut self, chunk: &[u8]) {
        if self.over_limit {
            return;
        }
        if chunk.len() > MAX_LENGTH - self.bytes.len() {
            self.over_limit = true;
            // Release what we had: the request is already rejected, and the
            // remaining drain can take a while.
            self.bytes = Vec::new();
            return;
        }
        self.bytes.extend_from_slice(chunk);
    }

    /// True once the body has exceeded [`MAX_LENGTH`]. Nothing is buffered from
    /// here on, so a caller that only needs to reach EOF can stop copying.
    pub fn over_limit(&self) -> bool {
        self.over_limit
    }

    /// The complete body, or the [`BodyError`] to answer with. Applies the same
    /// checks as [`validate_body`], with the size check already streamed.
    pub fn finish(self) -> Result<Vec<u8>, BodyError> {
        if self.over_limit {
            return Err(BodyError::TooLong);
        }
        validate_body(&self.bytes)?;
        Ok(self.bytes)
    }
}

/// `{"key":"…"}` — `POST /p/documents` success.
#[derive(Serialize)]
pub struct KeyBody<'a> {
    pub key: &'a str,
}

/// `{"data":"…","key":"…"}` — `GET /p/documents/{key}` success. Field order
/// matches haste's `JSON.stringify({ data: ret, key: key })`.
#[derive(Serialize)]
pub struct DocumentBody<'a> {
    pub data: &'a str,
    pub key: &'a str,
}

/// `{"message":"…"}` — every haste error body.
#[derive(Serialize)]
pub struct MessageBody<'a> {
    pub message: &'a str,
}

/// Serialize one of the body shapes above. Infallible in practice (plain
/// strings), so a serializer failure degrades to a hardcoded message body rather
/// than propagating.
pub fn to_json<T: Serialize>(value: &T) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| String::from(r#"{"message":"Error adding document."}"#))
}

/// `{"message":"…"}` as a JSON string.
pub fn message_json(message: &str) -> String {
    to_json(&MessageBody {
        message,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn key_has_requested_length_and_charset() {
        let random: Vec<u8> = (0..=KEY_LENGTH as u8).collect();
        let key = phonetic_key(KEY_LENGTH, &random);
        assert_eq!(key.len(), KEY_LENGTH);
        assert!(is_valid_key(&key), "{key}");
        assert!(key.bytes().all(|b| b.is_ascii_lowercase()), "{key}");
    }

    #[test]
    fn key_alternates_consonant_and_vowel() {
        let is_vowel = |c: char| VOWELS.contains(&(c as u8));
        for parity in [0u8, 1u8] {
            let mut random = vec![parity];
            random.extend((0..KEY_LENGTH as u8).map(|i| i * 7));
            let key = phonetic_key(KEY_LENGTH, &random);
            let chars: Vec<char> = key.chars().collect();
            for (i, c) in chars.iter().enumerate() {
                // even index is a consonant when parity is even, and vice versa
                let want_consonant = i % 2 == (parity & 1) as usize;
                assert_eq!(!is_vowel(*c), want_consonant, "parity {parity} index {i} in {key}");
            }
        }
    }

    #[test]
    fn key_is_deterministic_in_its_entropy() {
        let random = [3u8, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        assert_eq!(phonetic_key(KEY_LENGTH, &random), phonetic_key(KEY_LENGTH, &random));
        let other = [2u8, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        // flipping the parity byte flips the whole alternation
        assert_ne!(phonetic_key(KEY_LENGTH, &random), phonetic_key(KEY_LENGTH, &other));
    }

    #[test]
    fn key_indexes_alphabets_by_modulo() {
        // parity 0 => index 0 is a consonant. 0 % 21 == 0 => 'b'; 1 % 5 == 1 => 'e'.
        assert_eq!(phonetic_key(2, &[0, 0, 1]), "be");
        // parity 1 => index 0 is a vowel. 0 % 5 == 0 => 'a'; 1 % 21 == 1 => 'c'.
        assert_eq!(phonetic_key(2, &[1, 0, 1]), "ac");
        // bytes wrap: 21 % 21 == 0 => 'b', 5 % 5 == 0 => 'a'
        assert_eq!(phonetic_key(2, &[0, 21, 5]), "ba");
    }

    #[test]
    fn key_truncates_when_entropy_is_short() {
        assert_eq!(phonetic_key(10, &[0, 0, 0]).len(), 2);
        assert_eq!(phonetic_key(10, &[]), "");
        assert_eq!(phonetic_key(0, &[0, 1, 2]), "");
    }

    #[test]
    fn key_random_bytes_covers_the_key() {
        assert_eq!(key_random_bytes(KEY_LENGTH), 11);
        let random = vec![7u8; key_random_bytes(KEY_LENGTH)];
        assert_eq!(phonetic_key(KEY_LENGTH, &random).len(), KEY_LENGTH);
    }

    #[test]
    fn new_key_is_ten_valid_chars_and_varies() {
        let a = new_key();
        assert_eq!(a.len(), KEY_LENGTH);
        assert!(is_valid_key(&a), "{a}");
        // 1.3e10 keyspace — a repeat across two draws would be a broken RNG.
        assert_ne!(a, new_key());
    }

    #[test]
    fn valid_key_accepts_alphanumerics() {
        assert!(is_valid_key("hopiwequri"));
        assert!(is_valid_key("A"));
        assert!(is_valid_key(&"a".repeat(MAX_KEY_LENGTH)));
    }

    #[test]
    fn valid_key_rejects_path_tricks_and_oversize() {
        assert!(!is_valid_key(""));
        assert!(!is_valid_key("../../etc/passwd"));
        assert!(!is_valid_key("a/b"));
        assert!(!is_valid_key("has space"));
        assert!(!is_valid_key("dot.ext")); // extensions are stripped before validation
        assert!(!is_valid_key("dash-key"));
        assert!(!is_valid_key("under_score"));
        assert!(!is_valid_key(&"a".repeat(MAX_KEY_LENGTH + 1)));
    }

    #[test]
    fn extension_stripping_matches_split_dot_zero() {
        assert_eq!(strip_extension("hopiwequri"), "hopiwequri");
        assert_eq!(strip_extension("hopiwequri.cs"), "hopiwequri");
        assert_eq!(strip_extension("hopiwequri.tar.gz"), "hopiwequri");
        assert_eq!(strip_extension(".cs"), "");
        assert_eq!(strip_extension(""), "");
    }

    #[test]
    fn body_limits() {
        assert_eq!(validate_body(b"hello"), Ok("hello"));
        assert_eq!(validate_body(b""), Err(BodyError::Empty));
        assert_eq!(validate_body(&vec![b'x'; MAX_LENGTH]).map(str::len), Ok(MAX_LENGTH));
        assert_eq!(validate_body(&vec![b'x'; MAX_LENGTH + 1]), Err(BodyError::TooLong));
        assert_eq!(validate_body(&[0xff, 0xfe]), Err(BodyError::NotUtf8));
    }

    fn accumulate(chunks: &[&[u8]]) -> BodyAccumulator {
        let mut body = BodyAccumulator::default();
        for chunk in chunks {
            body.push(chunk);
        }
        body
    }

    #[test]
    fn accumulator_joins_chunks() {
        assert_eq!(accumulate(&[b"hel", b"lo"]).finish(), Ok(b"hello".to_vec()));
        assert_eq!(accumulate(&[]).finish(), Err(BodyError::Empty));
        assert_eq!(accumulate(&[b""]).finish(), Err(BodyError::Empty));
        assert_eq!(accumulate(&[&[0xff, 0xfe]]).finish(), Err(BodyError::NotUtf8));
    }

    #[test]
    fn accumulator_accepts_exactly_the_limit() {
        let mut body = accumulate(&[&vec![b'x'; MAX_LENGTH - 1], b"x"]);
        assert!(!body.over_limit());
        body = accumulate(&[&vec![b'x'; MAX_LENGTH]]);
        assert!(!body.over_limit());
        assert_eq!(body.finish().map(|b| b.len()), Ok(MAX_LENGTH));
    }

    #[test]
    fn accumulator_stops_buffering_past_the_limit() {
        // The limit trips on the chunk that crosses it, not after the whole
        // body has been held in memory.
        let mut body = BodyAccumulator::default();
        body.push(&vec![b'x'; MAX_LENGTH]);
        assert!(!body.over_limit());
        body.push(b"x");
        assert!(body.over_limit());
        assert!(body.bytes.is_empty(), "buffer must be released once over the limit");
        // Further chunks are ignored rather than accumulated, so a hostile body
        // can be drained to EOF at constant memory.
        for _ in 0..64 {
            body.push(&vec![b'x'; 1024 * 1024]);
        }
        assert!(body.bytes.is_empty());
        assert_eq!(body.finish(), Err(BodyError::TooLong));
    }

    #[test]
    fn accumulator_matches_validate_body() {
        for case in [
            b"hello".as_slice(),
            b"",
            &vec![b'x'; MAX_LENGTH],
            &vec![b'x'; MAX_LENGTH + 1],
            &[0xff, 0xfe],
        ] {
            let streamed = accumulate(&case.chunks(7).collect::<Vec<_>>());
            assert_eq!(streamed.finish(), validate_body(case).map(|text| text.as_bytes().to_vec()));
        }
    }

    #[test]
    fn body_error_messages_match_haste() {
        assert_eq!(BodyError::TooLong.message(), "Document exceeds maximum length.");
        assert_eq!(NOT_FOUND_MESSAGE, "Document not found.");
        assert_eq!(STORE_ERROR_MESSAGE, "Error adding document.");
    }

    #[test]
    fn json_bodies_match_haste() {
        assert_eq!(
            to_json(&KeyBody {
                key: "hopiwequri",
            }),
            r#"{"key":"hopiwequri"}"#
        );
        assert_eq!(
            to_json(&DocumentBody {
                data: "a\"b\nc",
                key: "hopiwequri",
            }),
            r#"{"data":"a\"b\nc","key":"hopiwequri"}"#
        );
        assert_eq!(message_json(NOT_FOUND_MESSAGE), r#"{"message":"Document not found."}"#);
    }
}
