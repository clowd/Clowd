//! The UI thread's command queue. Commands arrive on stdin as `share_region::Command`
//! (crates/prisc_capture/HELPER_PROTOCOL.md §4.2) and are validated into [`Command`] here,
//! apart from the reader thread and from the UI, so the rules are unit-testable without a
//! window, an OBS context or a live pipe.
//!
//! A rejection is not written from the reader thread: it is queued like an accepted command
//! and written when the UI thread reaches it. See [`Command::Malformed`] for why that
//! ordering matters.

use prisc_ipc::catalog::share_region::{self as wire, ObscureMode};
use prisc_ipc::Inbound;
use prisc_obs_platform::region::Rect;

use crate::obscure::Mode;

/// Default obscure strength when the command omits one. Halfway up the 1..=100
/// scale: visibly obscured without being an opaque smear.
const DEFAULT_STRENGTH: u32 = 50;

/// Smallest region side, the same floor as the `--region` parser.
const MIN_SIDE: u32 = 2;

/// Everything the UI thread is asked to do, including the lines that turned
/// out not to be a request at all.
///
/// Queued by value across threads (`ui::post_command`) and drained on the UI
/// thread. Not `Copy`, only `Clone`: [`Command::Malformed`] carries an owned
/// reason string, which is the price of putting rejections through the same
/// queue as accepted commands.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    /// Posted by the quit path (`quit`, stdin EOF, a signal, a dead stdout):
    /// stop mirroring, emit `done`, exit 0.
    Quit,
    /// Re-plan the capture, resize the canvas and the mirror window. The rect
    /// is a request, not a promise: the UI layer clamps it and acks whatever
    /// it actually applied.
    Move(Rect),
    /// Change what the preview draws. Handled entirely by the graphics-thread
    /// draw callback.
    Obscure(Mode),
    /// A line that was refused, carrying the reason the UI thread puts on the
    /// wire as `error{code:"bad_command"}`.
    ///
    /// This exists so that EVERY protocol response is written by the one thread
    /// that drains this queue, in the order the commands arrived. Writing a
    /// rejection straight from the reader thread would race the acks: the shell
    /// could send a `move` then a bad `obscure` and see the error for the second
    /// line before the `region_changed` for the first, because the move's ack is
    /// not written until the UI thread has actually applied it (a `PostMessage`
    /// round trip on Windows, up to one poll tick on macOS, and longer still
    /// while `Mirror::set_region` is inside `reset_video`). A caller that
    /// correlates "the next reply is the answer to my command" would then
    /// attribute the failure to the wrong command.
    Malformed(String),
}

/// One stdin line, as the UI thread sees it.
pub fn from_wire(inbound: Inbound<wire::Command>) -> Command {
    let command = match inbound {
        Inbound::Command(command) => command,
        Inbound::Malformed { reason } => return Command::Malformed(reason),
    };
    let validated = match command {
        wire::Command::Move { region } => validate_region(region).map(Command::Move),
        wire::Command::Obscure { mode, strength } => validate_obscure(mode, strength).map(Command::Obscure),
    };
    validated.unwrap_or_else(Command::Malformed)
}

/// Negative X/Y are fine (monitors to the left of, or above, the primary have
/// negative virtual-desktop origins); a side below the floor is not a region.
fn validate_region(region: wire::Rect) -> Result<Rect, String> {
    if region.w < MIN_SIDE || region.h < MIN_SIDE {
        return Err(format!("move: W and H must be >= {MIN_SIDE}"));
    }
    Ok(Rect {
        x: region.x,
        y: region.y,
        w: region.w,
        h: region.h,
    })
}

/// Strength is 1..=100 for blur and pixelate. 0 is not "off" (that is mode
/// `none`), it is a degenerate blur radius, so it is rejected along with
/// everything above 100. `hide` and `none` have no strength to vary, so a
/// stray one is a sign the caller thinks it is setting something, and is
/// refused rather than silently dropped.
///
/// The bounds are `obscure`'s own constants rather than literals repeated
/// here. That module clamps into the same range defensively before the value
/// reaches the graphics thread, so two independent copies of the range would
/// let a future widening pass validation here and then be silently clamped
/// there: the caller would be acked with a strength it never gets.
fn validate_obscure(mode: ObscureMode, strength: Option<u32>) -> Result<Mode, String> {
    const MIN: u32 = crate::obscure::MIN_STRENGTH;
    const MAX: u32 = crate::obscure::MAX_STRENGTH;
    let ranged = || match strength {
        None => Ok(DEFAULT_STRENGTH),
        Some(value) if (MIN..=MAX).contains(&value) => Ok(value),
        Some(value) => Err(format!("strength {value} out of range (expected {MIN}..={MAX})")),
    };
    let none = || match strength {
        None => Ok(()),
        Some(value) => Err(format!("obscure mode '{}' takes no strength (got {value})", mode.as_str())),
    };
    match mode {
        ObscureMode::Blur => ranged().map(Mode::Blur),
        ObscureMode::Pixelate => ranged().map(Mode::Pixelate),
        ObscureMode::Hide => none().map(|()| Mode::Hide),
        ObscureMode::None => none().map(|()| Mode::None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rect(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    fn parse(line: &str) -> Command {
        from_wire(Inbound::Command(serde_json::from_str(line).unwrap()))
    }

    fn rejected(line: &str) -> String {
        match parse(line) {
            Command::Malformed(reason) => reason,
            other => panic!("{line} was accepted as {other:?}"),
        }
    }

    #[test]
    fn moves_keep_negative_origins() {
        assert_eq!(
            parse(r#"{"type":"move","region":{"x":10,"y":20,"w":300,"h":200}}"#),
            Command::Move(rect(10, 20, 300, 200))
        );
        assert_eq!(
            parse(r#"{"type":"move","region":{"x":-1920,"y":-200,"w":640,"h":480}}"#),
            Command::Move(rect(-1920, -200, 640, 480))
        );
    }

    #[test]
    fn rejects_degenerate_moves() {
        assert!(rejected(r#"{"type":"move","region":{"x":0,"y":0,"w":1,"h":200}}"#).contains(">= 2"));
        assert!(rejected(r#"{"type":"move","region":{"x":0,"y":0,"w":300,"h":0}}"#).contains(">= 2"));
    }

    #[test]
    fn parses_obscure_modes() {
        assert_eq!(parse(r#"{"type":"obscure","mode":"none"}"#), Command::Obscure(Mode::None));
        assert_eq!(parse(r#"{"type":"obscure","mode":"hide"}"#), Command::Obscure(Mode::Hide));
        assert_eq!(parse(r#"{"type":"obscure","mode":"blur"}"#), Command::Obscure(Mode::Blur(DEFAULT_STRENGTH)));
        assert_eq!(parse(r#"{"type":"obscure","mode":"blur","strength":90}"#), Command::Obscure(Mode::Blur(90)));
        assert_eq!(
            parse(r#"{"type":"obscure","mode":"pixelate"}"#),
            Command::Obscure(Mode::Pixelate(DEFAULT_STRENGTH))
        );
        assert_eq!(parse(r#"{"type":"obscure","mode":"pixelate","strength":1}"#), Command::Obscure(Mode::Pixelate(1)));
        assert_eq!(parse(r#"{"type":"obscure","mode":"blur","strength":100}"#), Command::Obscure(Mode::Blur(100)));
    }

    #[test]
    fn rejects_bad_strengths() {
        // 0 is not "off"; mode none is off.
        assert!(rejected(r#"{"type":"obscure","mode":"blur","strength":0}"#).contains("1..=100"));
        assert!(rejected(r#"{"type":"obscure","mode":"pixelate","strength":101}"#).contains("1..=100"));
        // Modes without a strength say so rather than dropping the argument.
        assert!(rejected(r#"{"type":"obscure","mode":"hide","strength":50}"#).contains("no strength"));
        assert!(rejected(r#"{"type":"obscure","mode":"none","strength":50}"#).contains("no strength"));
    }

    #[test]
    fn malformed_lines_are_queued_with_their_reason() {
        assert_eq!(
            from_wire(Inbound::Malformed { reason: "unknown command 'mvoe'".into() }),
            Command::Malformed("unknown command 'mvoe'".into())
        );
    }
}
