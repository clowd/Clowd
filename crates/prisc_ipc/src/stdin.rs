//! The stdin half (HELPER_PROTOCOL.md §2.1, §2.3, §2.4): one JSON command per line, `quit` and
//! EOF handled here.

use std::io::{self, BufRead};
use std::ops::ControlFlow;
use std::sync::atomic::{AtomicBool, Ordering};

use serde::de::DeserializeOwned;

use crate::catalog::QUIT;
use crate::framing::{fill, INVALID_COMMAND_REASON, NOT_JSON_REASON, NO_TYPE_REASON, OVERSIZE_REASON, UNKNOWN_COMMAND_REASON};
use crate::lifetime::{self, Lifetime, QuitReason};

/// Longest line accepted in either direction (§2.1). The C# side reads it from the generated
/// `IpcProtocol.MaxLineBytes`.
pub const MAX_LINE: usize = 1024 * 1024;

/// One stdin line, as the helper sees it.
#[derive(Debug, PartialEq)]
pub enum Inbound<C> {
    Command(C),
    /// Not JSON, an unknown `type`, a missing or invalid field, or longer than [`MAX_LINE`].
    /// The helper answers it with a non-fatal `bad_command`, written from its own
    /// command-draining thread in arrival order with its acks (§2.4).
    Malformed {
        reason: String,
    },
}

/// Spawns the one stdin thread (`ipc-stdin`; a second call panics). Reads raw lines, strips a
/// trailing `\r` and a leading UTF-8 BOM, decodes lossily and parses. `{"type":"quit"}` and
/// EOF or a read error fire the quit path ([`QuitReason::Command`] / [`QuitReason::StdinClosed`])
/// and never reach `sink`. Empty lines are skipped. The thread ends when `sink` returns
/// `ControlFlow::Break` or at EOF; after `quit` it keeps reading (the quit-then-read rule,
/// [`crate::framing`]). The C# loop in IpcChild.cs runs the same golden vectors.
///
/// `quit` is triggered on this thread after `sink` has returned for every earlier line, so a
/// helper whose `on_quit` posts into the same queue `sink` feeds keeps arrival order (§2.3).
pub fn read_commands<C: DeserializeOwned + Send + 'static>(sink: impl FnMut(Inbound<C>) -> ControlFlow<()> + Send + 'static) {
    static STARTED: AtomicBool = AtomicBool::new(false);
    assert!(!STARTED.swap(true, Ordering::SeqCst), "prisc_ipc::read_commands called twice");
    std::thread::Builder::new()
        .name("ipc-stdin".into())
        .spawn(move || pump(io::stdin().lock(), sink, &lifetime::GLOBAL))
        .expect("spawn the ipc-stdin thread");
}

pub(crate) fn pump<C: DeserializeOwned>(
    mut reader: impl BufRead,
    mut sink: impl FnMut(Inbound<C>) -> ControlFlow<()>,
    lifetime: &'static Lifetime,
) {
    let mut buf = Vec::new();
    loop {
        let inbound = match next_line(&mut reader, &mut buf) {
            Ok(Line::Eof) => break,
            Err(e) => {
                eprintln!("prisc_ipc: stdin read failed ({e}); treating it as closed");
                break;
            }
            Ok(Line::TooLong) => Inbound::Malformed {
                reason: OVERSIZE_REASON.to_owned(),
            },
            Ok(Line::Complete) => match parse(&buf) {
                Some(Parsed::Quit) => {
                    lifetime.trigger(QuitReason::Command);
                    continue;
                }
                Some(Parsed::Inbound(inbound)) => inbound,
                None => continue,
            },
        };
        if let Inbound::Malformed {
            reason,
        } = &inbound
        {
            eprintln!("prisc_ipc: bad command: {reason}");
        }
        if sink(inbound).is_break() {
            return;
        }
    }
    lifetime.trigger(QuitReason::StdinClosed);
}

enum Line {
    Complete,
    TooLong,
    Eof,
}

/// Reads up to and excluding the next `\n` into `buf`, never holding more than [`MAX_LINE`]
/// bytes: a longer line is consumed to its end and reported as [`Line::TooLong`]. A last line
/// without a newline still counts.
fn next_line(reader: &mut impl BufRead, buf: &mut Vec<u8>) -> io::Result<Line> {
    buf.clear();
    let mut any = false;
    let mut too_long = false;
    loop {
        let available = match reader.fill_buf() {
            Ok(available) => available,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        };
        if available.is_empty() {
            return Ok(match (any, too_long) {
                (false, _) => Line::Eof,
                (true, false) => Line::Complete,
                (true, true) => Line::TooLong,
            });
        }
        any = true;
        let newline = available.iter().position(|&b| b == b'\n');
        let chunk = &available[..newline.unwrap_or(available.len())];
        if !too_long {
            if buf.len() + chunk.len() > MAX_LINE {
                too_long = true;
                buf.clear();
            } else {
                buf.extend_from_slice(chunk);
            }
        }
        let used = newline.map_or(available.len(), |i| i + 1);
        reader.consume(used);
        if newline.is_some() {
            return Ok(if too_long { Line::TooLong } else { Line::Complete });
        }
    }
}

enum Parsed<C> {
    Quit,
    Inbound(Inbound<C>),
}

/// `None` for a blank line.
fn parse<C: DeserializeOwned>(raw: &[u8]) -> Option<Parsed<C>> {
    let raw = raw.strip_suffix(b"\r").unwrap_or(raw);
    // .NET's default UTF8Encoding and PowerShell's pipe writer prefix a BOM; it is not
    // whitespace and would otherwise make exactly one command per session unparseable.
    let raw = raw
        .strip_prefix(b"\xEF\xBB\xBF")
        .unwrap_or(raw);
    let text = String::from_utf8_lossy(raw);
    let text = text.trim();
    if text.is_empty() {
        return None;
    }
    let value: serde_json::Value = match serde_json::from_str(text) {
        Ok(value) => value,
        Err(e) => {
            return Some(Parsed::Inbound(Inbound::Malformed {
                reason: fill(NOT_JSON_REASON, &[&e.to_string()]),
            }))
        }
    };
    let kind = value
        .get("type")
        .and_then(|t| t.as_str())
        .map(str::to_owned);
    let Some(kind) = kind else {
        return Some(Parsed::Inbound(Inbound::Malformed {
            reason: NO_TYPE_REASON.to_owned(),
        }));
    };
    if kind == QUIT.name {
        return Some(Parsed::Quit);
    }
    Some(Parsed::Inbound(match serde_json::from_value::<C>(value) {
        Ok(command) => Inbound::Command(command),
        // The parser's own words, except for the one case it words badly.
        Err(e) if e.to_string().starts_with("unknown variant") => Inbound::Malformed {
            reason: fill(UNKNOWN_COMMAND_REASON, &[&kind]),
        },
        Err(e) => Inbound::Malformed {
            reason: fill(INVALID_COMMAND_REASON, &[&kind, &e.to_string()]),
        },
    }))
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;

    use super::*;
    use crate::catalog::{capture, share_region};
    use crate::lifetime::tests::counting;

    /// Runs `input` through the pump; returns what reached the sink and every quit call.
    fn run<C: DeserializeOwned>(input: &[u8]) -> (Vec<Inbound<C>>, Vec<QuitReason>) {
        let (lifetime, calls) = counting();
        let mut seen = Vec::new();
        pump(
            Cursor::new(input.to_vec()),
            |inbound| {
                seen.push(inbound);
                ControlFlow::Continue(())
            },
            lifetime,
        );
        let calls = calls.lock().unwrap().clone();
        (seen, calls)
    }

    // The framing itself (blank lines, CR and BOM stripping, lossy UTF-8, the 1 MiB cap, quit and
    // EOF) is pinned by the golden vectors in framing.rs, which the C# loop runs too. These tests
    // cover what the vectors cannot: the real catalogs' serde messages and the sink's own control.

    #[test]
    fn malformed_lines_name_the_problem() {
        let (seen, _) = run::<share_region::Command>(
            b"not json\n{\"type\":\"mvoe\"}\n{\"type\":\"move\"}\n[1,2]\n{\"type\":\"obscure\",\"mode\":\"sepia\"}\n",
        );
        let reasons: Vec<&str> = seen
            .iter()
            .map(|inbound| match inbound {
                Inbound::Malformed {
                    reason,
                } => reason.as_str(),
                Inbound::Command(c) => panic!("parsed {c:?}"),
            })
            .collect();
        assert!(reasons[0].starts_with("not JSON"), "{}", reasons[0]);
        assert_eq!(reasons[1], "unknown command 'mvoe'");
        assert!(
            reasons[2].starts_with("invalid 'move' command: missing field `region`"),
            "{}",
            reasons[2]
        );
        assert!(reasons[3].contains("\"type\""), "{}", reasons[3]);
        assert!(reasons[4].contains("unknown ObscureMode value 'sepia'"), "{}", reasons[4]);
    }

    #[test]
    fn a_break_from_the_sink_stops_reading_without_quitting() {
        let (lifetime, calls) = counting();
        let mut count = 0;
        pump::<capture::Command>(
            Cursor::new(b"{\"type\":\"pause\",\"paused\":true}\n{\"type\":\"pause\",\"paused\":true}\n".to_vec()),
            |_| {
                count += 1;
                ControlFlow::Break(())
            },
            lifetime,
        );
        assert_eq!(count, 1);
        assert!(calls.lock().unwrap().is_empty());
    }

    #[test]
    fn a_read_error_counts_as_eof() {
        struct Failing;
        impl io::Read for Failing {
            fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
                Err(io::Error::other("gone"))
            }
        }
        let (lifetime, calls) = counting();
        pump::<capture::Command>(io::BufReader::new(Failing), |_| ControlFlow::Continue(()), lifetime);
        assert_eq!(*calls.lock().unwrap(), [QuitReason::StdinClosed]);
    }
}
