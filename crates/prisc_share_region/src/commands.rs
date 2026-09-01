//! stdin command grammar. The Clowd shell drives this process the same way it
//! drives obs-express: line-oriented text in, JSON status lines out. Parsing
//! lives here, apart from the reader thread and from the UI, so the grammar is
//! unit-testable without a window, an OBS context or a live pipe.
//!
//! Unlike obs-express (whose parser returns `Option` and whose caller just logs
//! "unknown command"), every rejection here carries a human-readable reason:
//! the caller turns it into a `{"type":"command_error","message":...}` line, so
//! the shell can surface *why* a command was refused rather than watching it
//! vanish. A malformed line is never fatal.
//!
//! A rejection is not written from this thread, though — it is queued like an
//! accepted command and written when the UI thread reaches it. See
//! [`Command::Error`] for why that ordering matters.

use std::io::{BufRead, ErrorKind};

use obs_platform::region::Rect;

use crate::obscure::Mode;

/// Default obscure strength when the command omits one. Halfway up the 1..=100
/// scale: visibly obscured without being an opaque smear.
const DEFAULT_STRENGTH: u32 = 50;

/// Everything the shell can ask this process to do, including the lines that
/// turned out not to be a request at all.
///
/// Queued by value across threads (`ui::post_command`) and drained on the UI
/// thread. Not `Copy`, only `Clone`: [`Command::Error`] carries an owned reason
/// string, which is the price of putting rejections through the same queue as
/// accepted commands.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    /// `quit` / `q`, and also synthesised on stdin EOF: exit 0.
    Quit,
    /// `move X,Y,W,H` — re-plan the capture, resize the canvas and the mirror
    /// window. The rect is a request, not a promise: the UI layer clamps it and
    /// acks whatever it actually applied.
    Move(Rect),
    /// `obscure <mode> [strength]` / `unobscure` — change what the preview
    /// draws. Handled entirely by the graphics-thread draw callback.
    Obscure(Mode),
    /// A line that was refused, carrying the reason the UI thread will put on
    /// the wire as `command_error`.
    ///
    /// This exists so that EVERY protocol response is written by the one thread
    /// that drains this queue, in the order the commands arrived. Writing a
    /// rejection straight from the reader thread would race the acks: the shell
    /// could send `move ...` then `obscure blurr` and see the `command_error`
    /// for the second line before the `region_changed` for the first, because
    /// the move's ack is not written until the UI thread has actually applied it
    /// (a `PostMessage` round trip on Windows, up to one poll tick on macOS, and
    /// longer still while `Mirror::set_region` is inside `reset_video`). A
    /// caller that correlates "the next line is the answer to my command" — the
    /// model Clowd's ObsCapturer uses — would then attribute the failure to the
    /// wrong command.
    Error(String),
}

/// Whitespace-split, first token dispatched case-insensitively. `Err` is a
/// reason string suitable for `status::emit_command_error`.
///
/// The region grammar has exactly one implementation — `parse_region`, shared
/// with the `--region` CLI flag — so `move 10,20,300,200` and
/// `move 10 20 300 200` cannot drift apart: the spaced form is re-joined with
/// commas and handed to the same parser. Negative X/Y parse fine (monitors to
/// the left of, or above, the primary have negative virtual-desktop origins).
pub fn parse_command(line: &str) -> Result<Command, String> {
    // A UTF-8 BOM on the very first line is not the caller being sloppy, it is
    // what several runtimes emit when a pipe writer is handed a BOM-carrying
    // encoding (PowerShell's default `StreamWriter`, .NET's `UTF8Encoding(true)`).
    // It is invisible in a log and it is not whitespace, so it would otherwise
    // ride along on the head token and turn exactly one command per session —
    // always the first — into "unknown command". Strip it and move on.
    let line = line.trim_start_matches('\u{feff}');
    let mut parts = line.split_whitespace();
    // An empty or whitespace-only line has no head token. The reader thread
    // already skips blank lines; anyone else calling this gets a reason.
    let head = parts
        .next()
        .ok_or_else(|| "empty command".to_string())?
        .to_ascii_lowercase();
    let args: Vec<&str> = parts.collect();

    match head.as_str() {
        "quit" | "q" => {
            // Trailing junk on `quit` is not worth refusing to exit over.
            Ok(Command::Quit)
        }
        "move" => parse_move(&args).map(Command::Move),
        "obscure" => parse_obscure(&args).map(Command::Obscure),
        // `unobscure` is pure sugar for `obscure none`; both exist because the
        // shell's two call sites read more naturally with one or the other.
        "unobscure" => Ok(Command::Obscure(Mode::None)),
        _ => Err(format!("unknown command '{head}'")),
    }
}

/// `X,Y,W,H` as one token, or `X Y W H` as four.
///
/// Both forms collapse to the same string before parsing: every token is split
/// on commas again, the empty pieces are dropped, and what is left is re-joined
/// with single commas. That is a no-op for the canonical comma form, rebuilds
/// it from the spaced form, and also absorbs the half-and-half spelling a
/// shell's string interpolation tends to produce ("10, 20, 300, 200", whose
/// tokens carry trailing commas that would otherwise double up). The component
/// count is then left entirely to `parse_region`, so there is one arity check
/// and one error vocabulary rather than two that can disagree.
fn parse_move(args: &[&str]) -> Result<Rect, String> {
    if args.is_empty() {
        return Err("move requires a region: X,Y,W,H".to_string());
    }
    let components: Vec<&str> = args
        .iter()
        .flat_map(|token| token.split(','))
        .map(str::trim)
        .filter(|piece| !piece.is_empty())
        .collect();
    obs_platform::region::parse_region(&components.join(",")).map_err(|e| e.to_string())
}

/// `blur [strength]`, `pixelate [strength]`, `hide`, `none`. The mode token is
/// mandatory: a bare `obscure` is ambiguous (turn it on? off?) and guessing
/// would be worse than an error the shell can log.
fn parse_obscure(args: &[&str]) -> Result<Mode, String> {
    let mode = args
        .first()
        .ok_or_else(|| "obscure requires a mode: none, blur, pixelate or hide".to_string())?
        .to_ascii_lowercase();
    let strength = args.get(1);
    match mode.as_str() {
        "blur" => Ok(Mode::Blur(parse_strength(strength)?)),
        "pixelate" => Ok(Mode::Pixelate(parse_strength(strength)?)),
        // Neither of these has a strength to vary, so a stray number is a sign
        // the caller thinks it is setting something. Say so instead of
        // silently dropping it.
        "hide" => reject_strength(strength).map(|_| Mode::Hide),
        "none" => reject_strength(strength).map(|_| Mode::None),
        other => Err(format!(
            "unknown obscure mode '{other}' (expected none, blur, pixelate or hide)"
        )),
    }
}

/// Strength is 1..=100 — 0 is not "off" (that is `obscure none`), it is a
/// degenerate blur radius, so it is rejected along with everything above 100.
///
/// The bounds are `obscure`'s own constants rather than literals repeated here.
/// That module clamps into the same range defensively before the value reaches
/// the graphics thread, so having two independent copies of the range would let
/// a future widening pass validation here and then be silently clamped there —
/// the caller would be acked with a strength it never gets.
fn parse_strength(arg: Option<&&str>) -> Result<u32, String> {
    const MIN: u32 = crate::obscure::MIN_STRENGTH;
    const MAX: u32 = crate::obscure::MAX_STRENGTH;

    let Some(text) = arg else {
        return Ok(DEFAULT_STRENGTH);
    };
    let value: u32 = text
        .parse()
        .map_err(|_| format!("invalid strength '{text}' (expected a number {MIN}..={MAX})"))?;
    if !(MIN..=MAX).contains(&value) {
        return Err(format!(
            "strength {value} out of range (expected {MIN}..={MAX})"
        ));
    }
    Ok(value)
}

fn reject_strength(arg: Option<&&str>) -> Result<(), String> {
    match arg {
        Some(text) => Err(format!(
            "this obscure mode takes no strength (got '{text}')"
        )),
        None => Ok(()),
    }
}

/// stdin reader thread: line-oriented commands, forwarded to the UI loop.
///
/// This thread never touches OBS, the mirror or any window, and it writes
/// nothing to stdout either — it only queues (`ui::post_command`, which wakes
/// the platform event loop). Everything, rejections included, is then applied
/// and answered on the UI thread, which is the only thread allowed to run
/// `AppEvents` and, by that route, the only writer of protocol lines that
/// answer a command (see [`Command::Error`]).
///
/// EOF is equivalent to `quit`: that is the orphan-safety mechanism. If the
/// shell dies without saying goodbye, the pipe closes, and an off-screen window
/// with no chrome has no other way for a user to get rid of it.
pub fn spawn_stdin_thread() {
    std::thread::spawn(|| {
        let stdin = std::io::stdin();
        let mut lines = stdin.lock().lines();
        loop {
            match lines.next() {
                Some(Ok(line)) => {
                    // Blank lines are how a lot of pipe writers flush; they are
                    // not errors and must not produce command_error noise.
                    if line.trim().is_empty() {
                        continue;
                    }
                    let cmd = parse_command(&line).unwrap_or_else(Command::Error);
                    crate::ui::post_command(cmd);
                }
                // Not valid UTF-8. This is a bad LINE, not a bad pipe, and the
                // contract is explicit that nothing arriving on stdin is fatal,
                // so it is answered like any other malformed command and the
                // read continues. Recovery is safe because `read_line` has
                // already consumed the offending bytes (it decodes into the
                // string it then discards), so `Lines` is positioned at the
                // start of the next line rather than stuck re-reading this one.
                Some(Err(e)) if e.kind() == ErrorKind::InvalidData => {
                    crate::ui::post_command(Command::Error(
                        "stdin line was not valid UTF-8".to_string(),
                    ));
                }
                // A real read failure. Not evidence the parent is gone, but
                // there is no way to keep reading commands either, so it is
                // treated like EOF — with a stderr note, because unlike EOF it
                // is not something that happens in normal operation.
                Some(Err(e)) => {
                    eprintln!("Warning: stdin read failed ({e}); quitting");
                    crate::ui::post_command(Command::Quit);
                    return;
                }
                // EOF: the parent closed the pipe, or was killed. Ask the UI
                // thread to quit and stop reading (orphan safety).
                None => {
                    crate::ui::post_command(Command::Quit);
                    return;
                }
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rect(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    #[test]
    fn parses_quit_spellings() {
        assert_eq!(parse_command("quit"), Ok(Command::Quit));
        assert_eq!(parse_command("q"), Ok(Command::Quit));
        assert_eq!(parse_command("  QUIT  "), Ok(Command::Quit));
        assert_eq!(parse_command("Q"), Ok(Command::Quit));
    }

    /// A BOM-prefixed first line (PowerShell's default pipe writer emits one)
    /// must parse as the command it plainly is, not as an unknown one.
    #[test]
    fn ignores_a_leading_byte_order_mark() {
        assert_eq!(parse_command("\u{feff}quit"), Ok(Command::Quit));
        assert_eq!(
            parse_command("\u{feff}obscure blur 70"),
            Ok(Command::Obscure(Mode::Blur(70)))
        );
    }

    #[test]
    fn parses_both_move_forms() {
        let expected = Ok(Command::Move(rect(10, 20, 300, 200)));
        assert_eq!(parse_command("move 10,20,300,200"), expected);
        assert_eq!(parse_command("move 10 20 300 200"), expected);
        // Comma form with incidental spaces after the commas.
        assert_eq!(parse_command("move 10, 20, 300, 200"), expected);
        assert_eq!(parse_command("  MOVE   10,20,300,200  "), expected);
    }

    #[test]
    fn parses_negative_move_coordinates() {
        // A monitor left of and above the primary: both origins negative.
        assert_eq!(
            parse_command("move -1920,-200,640,480"),
            Ok(Command::Move(rect(-1920, -200, 640, 480)))
        );
        assert_eq!(
            parse_command("move -1920 -200 640 480"),
            Ok(Command::Move(rect(-1920, -200, 640, 480)))
        );
    }

    #[test]
    fn rejects_malformed_move() {
        assert!(parse_command("move").is_err());
        assert!(parse_command("move 10,20,300").is_err());
        assert!(parse_command("move 10 20 300").is_err());
        assert!(parse_command("move a,b,c,d").is_err());
        // W/H below the parser's floor of 2.
        assert!(parse_command("move 0,0,1,200").is_err());
        // Negative size is not a size.
        assert!(parse_command("move 0,0,-300,200").is_err());
    }

    #[test]
    fn parses_obscure_modes() {
        assert_eq!(
            parse_command("obscure none"),
            Ok(Command::Obscure(Mode::None))
        );
        assert_eq!(parse_command("unobscure"), Ok(Command::Obscure(Mode::None)));
        assert_eq!(parse_command("UNOBSCURE"), Ok(Command::Obscure(Mode::None)));
        assert_eq!(
            parse_command("obscure hide"),
            Ok(Command::Obscure(Mode::Hide))
        );
        assert_eq!(
            parse_command("obscure blur"),
            Ok(Command::Obscure(Mode::Blur(DEFAULT_STRENGTH)))
        );
        assert_eq!(
            parse_command("obscure blur 90"),
            Ok(Command::Obscure(Mode::Blur(90)))
        );
        assert_eq!(
            parse_command("obscure pixelate"),
            Ok(Command::Obscure(Mode::Pixelate(DEFAULT_STRENGTH)))
        );
        assert_eq!(
            parse_command("OBSCURE PIXELATE 1"),
            Ok(Command::Obscure(Mode::Pixelate(1)))
        );
        assert_eq!(
            parse_command("obscure blur 100"),
            Ok(Command::Obscure(Mode::Blur(100)))
        );
    }

    #[test]
    fn rejects_bad_obscure() {
        // A bare `obscure` is ambiguous, not a toggle.
        assert!(parse_command("obscure").is_err());
        assert!(parse_command("obscure sepia").is_err());
        // 0 is not "off"; `obscure none` is off.
        let err = parse_command("obscure blur 0").unwrap_err();
        assert!(
            err.contains("1..=100"),
            "reason should name the range: {err}"
        );
        let err = parse_command("obscure pixelate 101").unwrap_err();
        assert!(
            err.contains("1..=100"),
            "reason should name the range: {err}"
        );
        assert!(parse_command("obscure blur loads").is_err());
        // Modes without a strength say so rather than dropping the argument.
        assert!(parse_command("obscure hide 50").is_err());
        assert!(parse_command("obscure none 50").is_err());
    }

    #[test]
    fn rejects_unknown_and_empty_lines() {
        assert!(parse_command("").is_err());
        assert!(parse_command("   ").is_err());
        assert!(parse_command("\t").is_err());
        let err = parse_command("bogus").unwrap_err();
        assert!(
            err.contains("bogus"),
            "reason should quote the token: {err}"
        );
        // Commands that exist in the sibling obs-express grammar but not here.
        assert!(parse_command("start").is_err());
        assert!(parse_command("configure /tmp/x.json").is_err());
    }
}
