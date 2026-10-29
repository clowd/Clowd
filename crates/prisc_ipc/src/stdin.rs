//! The stdin half (HELPER_PROTOCOL.md §2.1, §2.3, §2.4): one JSON command per line, `quit` and
//! EOF handled here.

use std::io::{self, BufRead};
use std::ops::ControlFlow;
use std::sync::atomic::{AtomicBool, Ordering};

use serde::de::DeserializeOwned;

use crate::catalog::QUIT;
use crate::lifetime::{self, Lifetime, QuitReason};

/// Longest line accepted in either direction (§2.1).
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
/// `ControlFlow::Break` or at EOF.
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
                reason: format!("command line longer than {MAX_LINE} bytes"),
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
                reason: format!("not JSON ({e})"),
            }))
        }
    };
    let kind = value
        .get("type")
        .and_then(|t| t.as_str())
        .map(str::to_owned);
    let Some(kind) = kind else {
        return Some(Parsed::Inbound(Inbound::Malformed {
            reason: "not a JSON object with a string \"type\"".to_owned(),
        }));
    };
    if kind == QUIT.name {
        return Some(Parsed::Quit);
    }
    Some(Parsed::Inbound(match serde_json::from_value::<C>(value) {
        Ok(command) => Inbound::Command(command),
        // The parser's own words, except for the one case it words badly.
        Err(e) if e.to_string().starts_with("unknown variant") => Inbound::Malformed {
            reason: format!("unknown command '{kind}'"),
        },
        Err(e) => Inbound::Malformed {
            reason: format!("invalid '{kind}' command: {e}"),
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

    fn pause(paused: bool) -> Inbound<capture::Command> {
        Inbound::Command(capture::Command::Pause {
            paused,
        })
    }

    #[test]
    fn parses_commands_and_skips_blank_lines() {
        let (seen, quits) =
            run::<capture::Command>(b"{\"type\":\"pause\",\"paused\":true}\r\n\n   \r\n{\"type\":\"pause\",\"paused\":false}");
        assert_eq!(seen, [pause(true), pause(false)]);
        assert_eq!(quits, [QuitReason::StdinClosed]);
    }

    #[test]
    fn strips_a_bom_on_any_line() {
        let (seen, _) = run::<capture::Command>(
            b"\xEF\xBB\xBF{\"type\":\"pause\",\"paused\":true}\n\xEF\xBB\xBF{\"type\":\"pause\",\"paused\":false}\n",
        );
        assert_eq!(seen, [pause(true), pause(false)]);
    }

    #[test]
    fn invalid_utf8_is_decoded_lossily() {
        let (seen, _) = run::<capture::Command>(b"{\"type\":\"settings\",\"args\":[\"a\xFFb\"]}\n");
        assert_eq!(
            seen,
            [Inbound::Command(capture::Command::Settings {
                args: vec!["a\u{FFFD}b".to_owned()],
            })]
        );
    }

    #[test]
    fn an_oversize_line_is_malformed_and_the_next_line_still_parses() {
        let mut input = b"{\"type\":\"settings\",\"args\":[\"".to_vec();
        input.extend(std::iter::repeat_n(b'x', MAX_LINE));
        input.extend_from_slice(b"\"]}\n{\"type\":\"pause\",\"paused\":true}\n");
        let (seen, _) = run::<capture::Command>(&input);
        assert_eq!(seen.len(), 2, "{:?}", seen.first());
        assert!(matches!(&seen[0], Inbound::Malformed { reason } if reason.contains("longer than")));
        assert_eq!(seen[1], pause(true));
    }

    #[test]
    fn a_line_of_exactly_the_limit_is_accepted() {
        let prefix = b"{\"type\":\"settings\",\"args\":[\"";
        let suffix = b"\"]}";
        let mut input = prefix.to_vec();
        input.extend(std::iter::repeat_n(b'x', MAX_LINE - prefix.len() - suffix.len()));
        input.extend_from_slice(suffix);
        input.push(b'\n');
        let (seen, _) = run::<capture::Command>(&input);
        assert!(matches!(&seen[..], [Inbound::Command(capture::Command::Settings { .. })]));
    }

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
    fn quit_never_reaches_the_sink_and_fires_once() {
        let (seen, quits) = run::<capture::Command>(
            b"{\"type\":\"pause\",\"paused\":true}\n{\"type\":\"quit\"}\n{\"type\":\"quit\"}\n{\"type\":\"pause\",\"paused\":false}\n",
        );
        // Lines after quit are still delivered; the helper is on its way out regardless.
        assert_eq!(seen, [pause(true), pause(false)]);
        // quit, a second quit, then EOF: one call, with the first reason.
        assert_eq!(quits, [QuitReason::Command]);
    }

    #[test]
    fn eof_alone_quits() {
        let (seen, quits) = run::<capture::Command>(b"");
        assert!(seen.is_empty());
        assert_eq!(quits, [QuitReason::StdinClosed]);
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
