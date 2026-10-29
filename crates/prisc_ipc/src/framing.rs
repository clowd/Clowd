//! The stdin framing contract (HELPER_PROTOCOL.md section 2.1, 2.3, 2.4) as data: the reason
//! texts a refused line is reported with, and the golden vectors both stdin loops run.
//!
//! There are two command loops: [`crate::read_commands`] in every Rust helper, and the managed
//! loop in `ui/Prisc.Ipc/IpcChild.cs` (Prisc.VideoRender; a managed loop is cheaper than a native
//! callback per command). They are kept identical by data, not by review: `cargo codegen` writes
//! [`REASONS`] into the generated `IpcProtocol` class and [`golden_vectors`] into
//! `ui/Prisc.Ipc.Tests/Generated/FramingVectors.g.cs`, and both loops' tests run every vector.
//!
//! The vectors speak a two-command test vocabulary that neither loop knows about:
//! `{"type":"ping","n":<u32>}` and `{"type":"echo","text":<string>}`. Each test decodes it with
//! its own codec, so what the vectors pin is everything around the codec: line splitting, the
//! 1 MiB cap, CR and BOM stripping, lossy UTF-8, blank lines, the final unterminated line, the
//! malformed-line reasons, and where `quit` and EOF fall in the sequence.
//!
//! Quit-then-read (section 2.3): `quit` fires the quit path once and the loop **keeps reading**.
//! Lines after it are still delivered in order, a second `quit` and the final EOF change nothing.
//! Section 2.3 makes `quit` idempotent and orders it after every earlier line; stopping the read
//! would add a third rule (later lines silently dropped) and would leave a parent that writes after
//! `quit` blocked on a full pipe nobody drains. The vector `quit_then_read` pins it.

use crate::lifetime::QuitReason;
use crate::stdin::MAX_LINE;

/// A line longer than [`MAX_LINE`] bytes (excluding the `\n`).
pub const OVERSIZE_REASON: &str = "command line longer than 1048576 bytes";

/// Valid JSON that is not an object with a string `type`.
pub const NO_TYPE_REASON: &str = "not a JSON object with a string \"type\"";

/// Not JSON at all; `{0}` is the parser's own words (they differ between the two loops).
pub const NOT_JSON_REASON: &str = "not JSON ({0})";

/// A `type` the helper's catalog does not have; `{0}` is the type.
pub const UNKNOWN_COMMAND_REASON: &str = "unknown command '{0}'";

/// A known `type` with a missing or invalid field; `{0}` is the type, `{1}` the parser's words.
pub const INVALID_COMMAND_REASON: &str = "invalid '{0}' command: {1}";

/// Every reason, as (C# constant name, text), in the order `IpcProtocol` lists them. The texts
/// with `{0}` / `{1}` are composite format strings on both sides ([`fill`] here,
/// `string.Format` in C#).
pub const REASONS: &[(&str, &str, &str)] = &[
    ("OversizeReason", OVERSIZE_REASON, "A command line longer than MaxLineBytes."),
    (
        "NoTypeReason",
        NO_TYPE_REASON,
        "Valid JSON that is not an object with a string type.",
    ),
    ("NotJsonReason", NOT_JSON_REASON, "Not JSON; {0} is the parser's message."),
    (
        "UnknownCommandReason",
        UNKNOWN_COMMAND_REASON,
        "A type the catalog does not have; {0} is the type.",
    ),
    (
        "InvalidCommandReason",
        INVALID_COMMAND_REASON,
        "A known type with a missing or invalid field; {0} is the type, {1} the parser's message.",
    ),
];

/// Replaces `{0}`, `{1}`, ... in a reason template, the way C#'s `string.Format` does for these
/// templates (they contain no other braces).
pub fn fill(template: &str, args: &[&str]) -> String {
    let mut text = template.to_owned();
    for (i, arg) in args.iter().enumerate() {
        text = text.replace(&format!("{{{i}}}"), arg);
    }
    text
}

/// A piece of a vector's stdin bytes. A 1 MiB line is described, not spelled out, so the
/// generated C# stays small.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Segment {
    /// These bytes, verbatim.
    Bytes(&'static [u8]),
    /// `count` copies of one byte.
    Repeat(u8, usize),
}

/// One thing a loop must do, in order.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Expected {
    /// A command reached the helper; the text is the JSON it must equal (compared as JSON values,
    /// so key order and whitespace do not matter).
    Command(&'static str),
    /// A line was refused with exactly this reason.
    Malformed(&'static str),
    /// A line was refused with a reason that starts with this (the rest is the parser's words).
    MalformedPrefix(&'static str),
    /// The quit path fired, for this reason. It fires once: a later `quit` or the EOF after it
    /// adds nothing.
    Quit(QuitReason),
}

/// One golden vector: bytes on stdin, and what the loop must do with them, in order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FramingVector {
    pub name: &'static str,
    pub input: Vec<Segment>,
    pub expected: Vec<Expected>,
}

impl FramingVector {
    /// The stdin bytes.
    pub fn bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::new();
        for segment in &self.input {
            match *segment {
                Segment::Bytes(b) => bytes.extend_from_slice(b),
                Segment::Repeat(b, count) => bytes.extend(std::iter::repeat_n(b, count)),
            }
        }
        bytes
    }
}

const PING_1: &str = r#"{"type":"ping","n":1}"#;
const PING_2: &str = r#"{"type":"ping","n":2}"#;

/// Every golden vector (see the module docs for the vocabulary).
pub fn golden_vectors() -> Vec<FramingVector> {
    use Expected::{Command, Malformed, MalformedPrefix, Quit};
    use Segment::{Bytes, Repeat};

    let ping_1_len = PING_1.len();
    let echo_open = br#"{"type":"echo","text":""#;
    vec![
        FramingVector {
            name: "commands_crlf_and_blank_lines",
            input: vec![Bytes(b"{\"type\":\"ping\",\"n\":1}\r\n\n   \r\n\t\n{\"type\":\"ping\",\"n\":2}\n")],
            expected: vec![Command(PING_1), Command(PING_2), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "a_bom_is_stripped_on_any_line",
            input: vec![Bytes(
                b"\xEF\xBB\xBF{\"type\":\"ping\",\"n\":1}\n\xEF\xBB\xBF{\"type\":\"ping\",\"n\":2}\r\n",
            )],
            expected: vec![Command(PING_1), Command(PING_2), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "invalid_utf8_is_decoded_lossily",
            input: vec![Bytes(b"{\"type\":\"echo\",\"text\":\"a\xFFb\"}\n")],
            expected: vec![
                Command("{\"type\":\"echo\",\"text\":\"a\u{FFFD}b\"}"),
                Quit(QuitReason::StdinClosed),
            ],
        },
        FramingVector {
            name: "a_final_line_without_a_newline_counts",
            input: vec![Bytes(b"{\"type\":\"ping\",\"n\":1}\n{\"type\":\"ping\",\"n\":2}")],
            expected: vec![Command(PING_1), Command(PING_2), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "only_one_trailing_cr_is_stripped_and_whitespace_is_ignored",
            input: vec![Bytes(b"  {\"type\":\"ping\",\"n\":1}\r\r\n")],
            expected: vec![Command(PING_1), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "a_line_of_exactly_the_limit_is_accepted",
            input: vec![Bytes(PING_1.as_bytes()), Repeat(b' ', MAX_LINE - ping_1_len), Bytes(b"\n")],
            expected: vec![Command(PING_1), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "the_limit_counts_a_trailing_cr",
            input: vec![Bytes(PING_1.as_bytes()), Repeat(b' ', MAX_LINE - ping_1_len), Bytes(b"\r\n")],
            expected: vec![Malformed(OVERSIZE_REASON), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "an_oversize_line_is_refused_and_the_next_line_still_parses",
            input: vec![
                Bytes(echo_open),
                Repeat(b'x', MAX_LINE),
                Bytes(b"\"}\n{\"type\":\"ping\",\"n\":1}\n"),
            ],
            expected: vec![Malformed(OVERSIZE_REASON), Command(PING_1), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "an_oversize_final_line_without_a_newline_is_refused",
            input: vec![Repeat(b'x', MAX_LINE + 1)],
            expected: vec![Malformed(OVERSIZE_REASON), Quit(QuitReason::StdinClosed)],
        },
        FramingVector {
            name: "malformed_lines_name_the_problem",
            input: vec![Bytes(
                b"not json\n{\"type\":\"pnig\"}\n{\"type\":\"ping\"}\n[1,2]\n{\"type\":5}\n{\"n\":1}\n{\"type\":\"ping\",\"n\":1} x\n",
            )],
            expected: vec![
                MalformedPrefix("not JSON ("),
                Malformed("unknown command 'pnig'"),
                MalformedPrefix("invalid 'ping' command: "),
                Malformed(NO_TYPE_REASON),
                Malformed(NO_TYPE_REASON),
                Malformed(NO_TYPE_REASON),
                MalformedPrefix("not JSON ("),
                Quit(QuitReason::StdinClosed),
            ],
        },
        FramingVector {
            name: "quit_then_read",
            input: vec![Bytes(
                b"{\"type\":\"ping\",\"n\":1}\n{\"type\":\"quit\"}\n{\"type\":\"quit\"}\n{\"type\":\"ping\",\"n\":2}\n",
            )],
            expected: vec![Command(PING_1), Quit(QuitReason::Command), Command(PING_2)],
        },
        FramingVector {
            name: "quit_with_extra_fields_a_bom_and_crlf",
            input: vec![Bytes(b"\xEF\xBB\xBF{\"type\":\"quit\",\"why\":\"done\"}\r\n")],
            expected: vec![Quit(QuitReason::Command)],
        },
        FramingVector {
            name: "eof_alone_quits",
            input: vec![],
            expected: vec![Quit(QuitReason::StdinClosed)],
        },
    ]
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;
    use std::ops::ControlFlow;
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    use serde::{Deserialize, Serialize};

    use super::*;
    use crate::lifetime::Lifetime;
    use crate::stdin::{pump, Inbound};

    /// The vectors' test vocabulary.
    #[derive(Debug, PartialEq, Serialize, Deserialize)]
    #[serde(tag = "type", rename_all = "snake_case")]
    enum Probe {
        Ping { n: u32 },
        Echo { text: String },
    }

    /// What the pump did, in order.
    #[derive(Debug, PartialEq)]
    enum Seen {
        Command(serde_json::Value),
        Malformed(String),
        Quit(QuitReason),
    }

    fn run(vector: &FramingVector) -> Vec<Seen> {
        let seen = Arc::new(Mutex::new(Vec::new()));
        let lifetime: &'static Lifetime = Box::leak(Box::new(Lifetime::new()));
        let on_quit = seen.clone();
        lifetime.set_on_quit(
            Arc::new(move |reason| {
                on_quit
                    .lock()
                    .unwrap()
                    .push(Seen::Quit(reason))
            }),
            Duration::from_secs(3600),
        );
        let sink = seen.clone();
        pump::<Probe>(
            Cursor::new(vector.bytes()),
            move |inbound| {
                sink.lock().unwrap().push(match inbound {
                    Inbound::Command(c) => Seen::Command(serde_json::to_value(c).unwrap()),
                    Inbound::Malformed {
                        reason,
                    } => Seen::Malformed(reason),
                });
                ControlFlow::Continue(())
            },
            lifetime,
        );
        let seen = std::mem::take(&mut *seen.lock().unwrap());
        seen
    }

    fn matches(expected: &Expected, seen: &Seen) -> bool {
        match (expected, seen) {
            (Expected::Command(json), Seen::Command(value)) => serde_json::from_str::<serde_json::Value>(json).unwrap() == *value,
            (Expected::Malformed(reason), Seen::Malformed(actual)) => reason == actual,
            (Expected::MalformedPrefix(prefix), Seen::Malformed(actual)) => actual.starts_with(prefix),
            (Expected::Quit(reason), Seen::Quit(actual)) => reason == actual,
            _ => false,
        }
    }

    #[test]
    fn every_golden_vector_passes_through_the_rust_loop() {
        for vector in golden_vectors() {
            let seen = run(&vector);
            assert_eq!(seen.len(), vector.expected.len(), "{}: {seen:?}", vector.name);
            for (expected, seen) in vector.expected.iter().zip(&seen) {
                assert!(matches(expected, seen), "{}: expected {expected:?}, got {seen:?}", vector.name);
            }
        }
    }

    #[test]
    fn vector_names_are_unique_and_every_reason_is_covered() {
        let vectors = golden_vectors();
        for (i, v) in vectors.iter().enumerate() {
            assert!(
                vectors[i + 1..]
                    .iter()
                    .all(|o| o.name != v.name),
                "{}",
                v.name
            );
        }
        let all: Vec<&Expected> = vectors
            .iter()
            .flat_map(|v| &v.expected)
            .collect();
        assert!(all.contains(&&Expected::Malformed(OVERSIZE_REASON)));
        assert!(all.contains(&&Expected::Malformed(NO_TYPE_REASON)));
        assert!(all.contains(&&Expected::Quit(QuitReason::Command)));
    }

    #[test]
    fn the_reason_texts_agree_with_the_limit_and_fill_like_string_format() {
        assert_eq!(OVERSIZE_REASON, format!("command line longer than {MAX_LINE} bytes"));
        assert_eq!(
            fill(INVALID_COMMAND_REASON, &["move", "missing field"]),
            "invalid 'move' command: missing field"
        );
        assert_eq!(fill(UNKNOWN_COMMAND_REASON, &["x"]), "unknown command 'x'");
        assert_eq!(fill(NOT_JSON_REASON, &["eof"]), "not JSON (eof)");
        for (name, text, doc) in REASONS {
            assert!(!name.is_empty() && !text.is_empty() && !doc.is_empty());
        }
    }
}
