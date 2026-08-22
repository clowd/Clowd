//! The driver loop and its conversation with the shell.
//!
//! One NDJSON object per line, the shape described in CAPTURE_PROTOCOL.md
//! §2.2, under the rule: **stdout carries only protocol lines**. Every log record goes to stderr and the session's
//! `scroll.log`, so the shell can treat any `{…}` line as an event.
//!
//! ```jsonc
//! {"type":"ready"}                                                   // about to scroll
//! {"type":"status","frames":12,"height_px":4180,"state":"scrolling"}
//! {"type":"done","result":"complete","frames":31,"height_px":9800}
//! {"type":"fatal_error","message":"…"}
//! ```
//!
//! Back the other way: `{"type":"stop"}` (finish now, keep what we have)
//! and `{"type":"cancel"}` (abort, write nothing). Both are polled at the
//! top of each step rather than interrupting one, so a stop that lands
//! mid-settle takes effect a fraction of a second later.
//!
//! The process exits 0 for every outcome the shell can act on, `done`
//! included — a `done` with `result` other than `failed` means
//! `session.json` is on disk. Only an unrecoverable setup failure produces
//! `fatal_error`, and it still exits 0: the shell reads the event, not the
//! code.
//!
//! Two things end a run early, and both keep the partial capture: the user
//! pressing Esc (level-sampled every 50ms throughout the waits and latched,
//! because the *target* owns the keyboard focus, not us, and a tap only
//! lasts ~100ms — one sample per step would miss most of them), and a
//! `stop` from the shell's HUD.
//!
//! Taking the mouse back does *not* end the run — it pauses it. The driver
//! parks the cursor on the scroll point because that is what aims the
//! wheel, so a user who moves the pointer is both scrolling something else
//! and dragging the picture out from under the capture. Past
//! [`CURSOR_DRIFT_PX`] the loop stops wheeling, says so on the status
//! channel, and waits; once the pointer has been still for
//! [`RESUME_STILL_FOR`] it is parked back on the scroll point and the
//! capture carries on where it left off. Paused time is excluded from the
//! wall-clock cap, so an interruption cannot silently truncate a run.

use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::cli::CliArgs;
use crate::frame::{self, Frame};
use crate::input::{self, Target};
use crate::output;
use crate::stitch::{AppendResult, Stitcher};
use clowd_rust_core::geometry::{ScreenPoint, ScreenRect};

/// Wheel notches in the first burst. One notch is a crawl on most apps
/// (many frames, a slow run), while a burst big enough to clear the
/// viewport leaves the stitcher no overlap to register against. Two is the
/// starting guess; the loop corrects it from what the target actually did.
const TICKS_INITIAL: u32 = 2;
const TICKS_MIN: u32 = 1;
const TICKS_MAX: u32 = 8;

/// Settle loop: how often to re-photograph the region while waiting for it
/// to stop moving, and how long to wait before giving up and taking the
/// latest frame anyway. Chromium animates a wheel scroll over ~100–300 ms,
/// longer with `scroll-behavior: smooth`; 800 ms covers that without
/// stalling the run on a page that simply never holds still (a carousel, a
/// playing video), where the timeout is the escape hatch.
const SETTLE_POLL: Duration = Duration::from_millis(50);
const SETTLE_TIMEOUT: Duration = Duration::from_millis(800);

/// Minimum time after the wheel injection before two identical captures
/// may count as settled. Right after injection, "nothing changed" has two
/// readings: the page finished moving, or the target has not processed the
/// wheel yet (an RDP session, a compositor under load, a busy Electron
/// app). The first comparison lands ~50ms in — well inside the second
/// reading — and a stale pair there hands the stitcher the pre-scroll
/// frame, which reads as `dy == 0`; two of those end the run in the middle
/// of the document. 150ms is comfortably past a healthy target's first
/// repaint while leaving most of the settle budget intact.
const SETTLE_MIN: Duration = Duration::from_millis(150);

/// Between foregrounding the target and the first capture. The window has
/// to finish repainting its activated state — focus rings, title bar
/// color, whatever it dims while inactive — or frame 0 disagrees with
/// every frame after it.
const START_DELAY: Duration = Duration::from_millis(350);

/// How far the cursor may drift from the parked scroll point before the run
/// pauses. Generous enough to survive a nudged desk, small enough that an
/// intentional move always registers. In capture-space units, so on a Retina
/// display it is ten points rather than ten pixels — a threshold about
/// hand movement, not about image detail.
const CURSOR_DRIFT_PX: i32 = 10;

/// How long the cursor must hold still before a paused run parks it back on
/// the scroll point and resumes. Long enough that it never fires while
/// someone is still moving the mouse — including the pauses in the middle
/// of a deliberate movement — and short enough that letting go of the mouse
/// visibly restarts the capture.
const RESUME_STILL_FOR: Duration = Duration::from_secs(3);

/// How far the cursor may wander between two polls and still count as
/// "still". A hand resting on a mouse nudges it a pixel at a time, and
/// without a little slop the resume timer would never elapse; measured from
/// where the still period *began*, not from the last poll, so a slow
/// continuous creep still counts as movement.
const PAUSE_STILL_SLOP_PX: i32 = 2;

/// How often a paused run re-reads the cursor.
const PAUSE_POLL: Duration = Duration::from_millis(50);

/// How long the cursor must be still before the HUD starts counting down to
/// the resume. Short enough that letting go of the mouse is acknowledged
/// almost at once, long enough that drawing breath mid-movement does not
/// start a countdown that immediately reverts — which reads as a flicker,
/// not as feedback.
const COUNTDOWN_AFTER: Duration = Duration::from_secs(1);

/// How long one pause may last before the run gives up and finalizes what
/// it has. Only reachable by a cursor that keeps *moving* for this long —
/// stillness resumes after [`RESUME_STILL_FOR`] — which means the user is
/// doing something else entirely and is not coming back to a capture that
/// would otherwise sit there holding a border window on their screen
/// forever.
const PAUSE_MAX: Duration = Duration::from_secs(60);

// The pause's shape, enforced where it is defined rather than in a test.
const _: () = {
    // A resume has to be reachable: the run must be able to sit still for
    // the resume delay without tripping the give-up cap first.
    assert!(RESUME_STILL_FOR.as_secs() < PAUSE_MAX.as_secs());
    // Polling has to be fine-grained enough to see movement inside the
    // window it is measuring stillness over.
    assert!(PAUSE_POLL.as_millis() < RESUME_STILL_FOR.as_millis());
    // The countdown has to have something left to count: it starts once the
    // cursor has been still this long and ticks down whole seconds from
    // there, so it must begin at least a second before the resume.
    assert!(COUNTDOWN_AFTER.as_millis() + 1_000 <= RESUME_STILL_FOR.as_millis());
    // Slop strictly under the drift threshold: at or above it, a cursor
    // sitting just past the threshold would read as moving forever and the
    // run would never resume.
    assert!(PAUSE_STILL_SLOP_PX < CURSOR_DRIFT_PX);
};

/// Hard caps. Infinite-scroll feeds never end, so something has to; each
/// one produces `max_reached` with the partial composite kept, and the
/// shell explains why.
const MAX_FRAMES: u32 = 120;
const MAX_HEIGHT_PX: u32 = 20_000;
const MAX_ELAPSED: Duration = Duration::from_secs(120);

/// Notches per burst while rewinding to the top. Far larger than the
/// downward `TICKS_*` because nothing is stitched on the way up — the only
/// question is whether the page still moves, so overshooting a screen
/// costs nothing and makes the rewind a fraction of the capture.
const REWIND_TICKS: u32 = 15;

/// Consecutive rewind bursts that changed nothing before we call it the
/// top. Two for the same reason as [`ZERO_STREAK_END`].
const REWIND_STILL_STREAK: u32 = 2;

/// Hard caps on the rewind. "Scroll up until nothing moves" does not
/// terminate in an app that lazily loads history upward — Slack, Discord,
/// any chat log — so it has to give up, and give up quickly enough that
/// the user reads it as a pause rather than a hang. Hitting either cap
/// starts the capture from wherever the rewind reached; it is never a
/// failure.
const REWIND_MAX_BURSTS: u32 = 40;
const REWIND_MAX_ELAPSED: Duration = Duration::from_secs(8);

// The rewind's shape, enforced where it is defined rather than in a test.
const _: () = {
    // Nothing is stitched on the way up, so a burst that overshoots a
    // screen costs nothing — and one that does not outpace the capture
    // would make the rewind longer than the run it precedes.
    assert!(REWIND_TICKS > TICKS_MAX);
    // One unchanged burst can be a frame caught mid-repaint.
    assert!(REWIND_STILL_STREAK >= 2);
    // Long enough to wind back a real document, short enough that a page
    // which never stops loading reads as a pause and not a hang.
    assert!(REWIND_MAX_ELAPSED.as_secs() <= 10);
};

/// Consecutive steps with no new content that mean "the document ended".
/// Two, not one: a frame captured mid-animation can coincidentally match
/// its predecessor.
const ZERO_STREAK_END: u32 = 2;

/// Consecutive steps with no new content — *when nothing has ever moved* —
/// before concluding the wheel is not reaching the target at all and
/// dropping to [`input::wheel_message`]'s rung. Higher than `ZERO_STREAK_END`
/// because this one is diagnosing a broken input path, not the bottom of a
/// page, and the cost of being wrong is a wasted rung rather than a wasted
/// run.
const ZERO_STREAK_FALLBACK: u32 = 3;

// ── Wire protocol ──────────────────────────────────────────────────────

/// Driver → shell events.
#[derive(Debug, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum DriveEvent {
    /// The target is resolved and focused; scrolling is about to start.
    Ready,
    /// Progress. Emitted at each phase change of every step, so the shell's
    /// HUD can show both a frame count and what the driver is waiting on.
    ///
    /// `resume_in_s` rides along only on the `resuming` state — the whole
    /// seconds left before a paused run takes the cursor back. Omitted
    /// everywhere else so every other status line keeps the exact shape it
    /// has always had.
    Status {
        frames: u32,
        height_px: u32,
        state: DriveState,
        #[serde(skip_serializing_if = "Option::is_none")]
        resume_in_s: Option<u64>,
    },
    /// The run ended. Unless `result` is `failed`, `session.json` is
    /// already on disk.
    Done { result: DriveResult, frames: u32, height_px: u32 },
    /// Setup or output failed; there is no session. The shell shows a
    /// dialog and deletes the directory.
    FatalError { message: String },
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "snake_case")]
enum DriveState {
    /// Winding the document back to the top before frame 0. Emitted only
    /// when the rewind is enabled, and always before any other state — the
    /// HUD needs it or the pause reads as a hang.
    Rewinding,
    /// The user has the mouse; nothing is being scrolled or captured until
    /// they put it down. Emitted the moment the cursor leaves the scroll
    /// point, so the HUD's readout matches the fact that the frame count has
    /// stopped advancing — and again if a countdown gets interrupted.
    Paused,
    /// The cursor has gone still and the run is about to take it back.
    /// Carries `resume_in_s`, and is re-emitted on each whole second so the
    /// HUD can count down; movement drops straight back to [`Paused`].
    Resuming,
    Scrolling,
    Settling,
    Stitching,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
enum DriveResult {
    /// Reached the bottom of the document.
    Complete,
    /// Esc, a `stop` command, the target window going away, or a pause the
    /// user never came back from. Partial capture kept.
    Stopped,
    /// Hit one of the hard caps. Partial capture kept.
    MaxReached,
    /// The target never scrolled — nothing we could inject moved it. A
    /// single-frame session is still written.
    NoMovement,
    /// No session was produced. Part of the contract because the shell has
    /// to handle it; the driver currently reports every failure it can
    /// recognize as `fatal_error` instead, since those all happen before
    /// there is anything worth keeping.
    #[allow(dead_code)]
    Failed,
}

/// Shell → driver commands.
#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum DriveCommand {
    /// Finish now and keep what has been captured.
    Stop,
    /// Abandon the run; write nothing.
    Cancel,
}

/// Serialize one event as an NDJSON line on stdout and flush it. The std
/// stdout handle's mutex keeps concurrent writes line-atomic, and a
/// serializer failure is swallowed rather than allowed to half-write a line
/// into the stream.
fn emit(event: &DriveEvent) {
    let line = match serde_json::to_string(event) {
        Ok(l) => l,
        Err(e) => {
            error!("failed to serialize drive event: {e}");
            return;
        }
    };
    let mut out = std::io::stdout().lock();
    if let Err(e) = writeln!(out, "{line}").and_then(|()| out.flush()) {
        warn!("failed to emit drive event: {e}");
    }
}

// ── Entry point ────────────────────────────────────────────────────────

/// Everything the driver needs, validated out of the CLI once.
struct DriveArgs {
    session_dir: PathBuf,
    region: ScreenRect,
    point: ScreenPoint,
    hwnd: i64,
    /// Wind the document back to the top before frame 0. On unless the
    /// shell passed `--no-rewind`.
    rewind: bool,
}

impl DriveArgs {
    fn from_cli(args: &CliArgs) -> anyhow::Result<Self> {
        let session_dir = args
            .session_dir
            .clone()
            .ok_or_else(|| anyhow!("the scrolling capture driver requires --session-dir"))?;
        let region = args
            .region
            .ok_or_else(|| anyhow!("the scrolling capture driver requires --region X,Y,W,H"))?;
        let point = args
            .point
            .ok_or_else(|| anyhow!("the scrolling capture driver requires --point PX,PY"))?;
        // The overlay clamps the point into the region before writing the
        // marker, so this only fires if the two arguments disagree. Clamping
        // aims the wheel at the region's edge instead of somewhere the user
        // cannot see — a worse capture, but still the capture they asked for.
        let point = clamp_into(point, region);
        if point != args.point.unwrap_or(point) {
            warn!("scroll point was outside the capture region {region:?}; clamped to {point:?}");
        }
        Ok(Self {
            session_dir,
            region,
            point,
            hwnd: args.hwnd,
            rewind: !args.no_rewind,
        })
    }
}

/// Clamp a point onto `rect`'s last addressable pixel row/column — the
/// same bound the overlay's own clamp uses (`session_output::write_scroll_action`), so a point
/// that came through it is left untouched.
fn clamp_into(point: ScreenPoint, rect: ScreenRect) -> ScreenPoint {
    let clamp = |v: i32, min: i32, max: i32| v.clamp(min, max.max(min));
    ScreenPoint::new(
        clamp(point.x, rect.min_x(), rect.max_x() - 1),
        clamp(point.y, rect.min_y(), rect.max_y() - 1),
    )
}

/// The driver's entry point, called from `main::run`. Always returns `Ok`
/// for outcomes the shell can act on — failures are reported as
/// `fatal_error` on the protocol channel, which is the only place the
/// shell is listening.
pub fn run(args: CliArgs) -> anyhow::Result<()> {
    let result = DriveArgs::from_cli(&args).and_then(drive);
    if let Err(e) = result {
        error!("scroll driver failed: {e:#}");
        emit(&DriveEvent::FatalError {
            message: format!("{e:#}"),
        });
    }
    Ok(())
}

fn drive(cfg: DriveArgs) -> anyhow::Result<()> {
    // Refuse before the border window and the HUD are on screen for a run
    // that cannot work — on macOS a missing permission means every wheel
    // event we post is discarded in silence.
    input::preflight()?;

    let target = input::resolve_target(cfg.hwnd, cfg.point).ok_or_else(|| anyhow!("no window at scroll point {:?}", cfg.point))?;
    // Sampled once: a window that moves or resizes mid-run has invalidated
    // the fixed rect we are photographing, and everything after that point
    // would stitch garbage.
    let target_rect = input::window_rect(target);

    let signals = Arc::new(Signals::default());
    spawn_stdin_reader(Arc::clone(&signals));

    // Esc is level-sampled, and a tap only lasts ~100ms, so every wait in
    // this function polls the key into this latch; `stop_requested`
    // consumes it at the top of each step. Only the driver thread touches
    // it — the atomic is for tidy shared references, not cross-thread
    // signaling.
    let esc_latch = AtomicBool::new(false);

    let run = Run {
        cfg: &cfg,
        signals: &signals,
        esc_latch: &esc_latch,
        target,
        target_rect,
    };

    emit(&DriveEvent::Ready);

    // Before anything is photographed: the target has to actually be the
    // window at the scroll point. The wheel is routed by cursor position and
    // the capture is a screenshot of the screen, so a window covering the point
    // would receive every scroll *and* be the thing in every frame — a tall,
    // plausible-looking picture of entirely the wrong window. Refusing is the
    // only honest outcome.
    if !input::raise_over_point(target, cfg.point) {
        bail!(
            "the window you selected could not be brought in front of the scroll point (window {} is over it). \
             Move whatever is covering it, or pick a scroll point on a visible part of the window.",
            input::describe_window_at(cfg.point)
        );
    }
    input::park_cursor(cfg.point);
    wait_latching_escape(START_DELAY, &esc_latch);

    if cfg.rewind {
        match rewind_to_top(&run)? {
            // The user asked to abandon the run while it was still winding
            // back; there is nothing captured to keep.
            Rewind::Canceled => {
                info!("canceled during rewind; writing nothing");
                return Ok(());
            }
            Rewind::Finished(why) => info!("rewind finished: {why}"),
        }
    }

    let frame0 = frame::capture_region(cfg.region)?;
    // The viewport, in the units the stitcher measures displacement in. Taken
    // from the frame and never from `cfg.region`: on a Retina display the
    // region is in CG points and the frame is twice its size in pixels, so
    // the region's height would make every step look like a small one and
    // wind the burst size up to its ceiling (see `crate::frame`).
    let viewport_px = frame0.height;
    let mut stitcher = Stitcher::new(frame0);

    let started = Instant::now();
    let mut ticks = TICKS_INITIAL;
    let mut zero_streak = 0u32;
    let mut total_dy = 0u32;
    let mut holds = 0u32;
    // Time spent waiting for the user to give the mouse back. Subtracted
    // from the wall-clock cap: that cap is there to bound how long we
    // scroll someone's window for, and a pause is not scrolling it.
    let mut paused_for = Duration::ZERO;
    // Set when the stitcher holds a frame, or when the user moved the mouse
    // while a frame was being captured: the next iteration re-captures
    // without wheeling, because the page is already displaced by an amount
    // that could not be measured and wheeling again would compound it.
    let mut skip_wheel = false;
    let mut use_message_wheel = false;

    let outcome = loop {
        if signals.canceled() {
            info!("canceled after {} frames; writing nothing", stitcher.frames());
            return Ok(());
        }
        if let Some(reason) = stop_requested(&run) {
            info!("stopping: {reason}");
            break DriveResult::Stopped;
        }
        // The user has the mouse: hold everything until they put it down,
        // then park it back on the scroll point and carry on. Deliberately
        // ahead of the wheel injection — a scroll sent while they are
        // pointing at something else lands in whatever they are pointing at.
        match pause_while_drifting(&run, stitcher.frames(), stitcher.height(), DriveState::Scrolling) {
            Paused::Ready {
                waited,
            } => {
                paused_for += waited;
                if !waited.is_zero() {
                    // The page may have moved while they had the mouse, and
                    // by an amount no wheel of ours accounts for. Photograph
                    // where it actually is before scrolling it further.
                    skip_wheel = true;
                }
            }
            Paused::Canceled => {
                info!("canceled while paused; writing nothing");
                return Ok(());
            }
            Paused::Stopped(reason) => {
                info!("stopping: {reason}");
                break DriveResult::Stopped;
            }
        }
        if let Some(cap) = cap_reached(&stitcher, started, paused_for) {
            info!("stopping: {cap}");
            break DriveResult::MaxReached;
        }

        let wheeled = !skip_wheel;
        if skip_wheel {
            // The page has not moved since the last look; a frame caught
            // mid-repaint is clean a moment later, so look again from the
            // same position instead of scrolling further away from the
            // reference the stitcher is still registering against.
            skip_wheel = false;
        } else {
            status(&stitcher, DriveState::Scrolling);
            if use_message_wheel {
                if !input::wheel_message(target, cfg.point, ticks) {
                    break DriveResult::NoMovement;
                }
            } else {
                input::wheel_burst(cfg.point, ticks, input::WheelDir::Down);
            }
        }

        status(&stitcher, DriveState::Settling);
        let frame = match settle(cfg.region, &esc_latch) {
            Ok(f) => f,
            Err(e) => {
                // Losing the screen DC mid-run is not a reason to throw away
                // what is already composited.
                warn!(
                    "capture failed after {} frames, keeping the partial capture: {e:#}",
                    stitcher.frames()
                );
                break DriveResult::Stopped;
            }
        };

        // The mouse moved while that frame was being taken, so it may hold a
        // hover highlight, a drag, or a page the user scrolled themselves —
        // and once appended, none of that comes back out. Drop it, and let
        // the pause at the top of the loop deal with the mouse; the re-capture
        // afterwards still carries the displacement of the wheel burst above.
        if input::cursor_drift(cfg.point) > CURSOR_DRIFT_PX {
            info!(
                "the cursor moved while frame {} was being captured; discarding it",
                stitcher.frames()
            );
            skip_wheel = true;
            continue;
        }

        status(&stitcher, DriveState::Stitching);
        let dy = match stitcher.append(frame) {
            AppendResult::Appended {
                dy,
            } => {
                holds = 0;
                dy
            }
            AppendResult::Hold => {
                // Re-capture without wheeling and let the retry register
                // against the same reference from the same position; two
                // in a row means it is not going to recover.
                holds += 1;
                if holds >= 2 {
                    warn!("two consecutive unregisterable frames; keeping the partial capture");
                    break DriveResult::Stopped;
                }
                skip_wheel = true;
                continue;
            }
            AppendResult::Failed => {
                warn!("stitcher gave up; keeping the partial capture");
                break DriveResult::Stopped;
            }
        };

        if dy > 0 {
            zero_streak = 0;
            total_dy = total_dy.saturating_add(dy);
            let next_ticks = adapt_ticks(ticks, dy, viewport_px);
            // One line per registered step, because the two ways a capture
            // goes subtly wrong are both invisible in the finished image
            // unless you can see this sequence: a step that moves most of a
            // viewport leaves the stitcher almost no overlap to register
            // against, and a dy near a multiple of the viewport on periodic
            // content (lined text, a chat log) is what a mis-registration
            // looks like. `wheeled` is here so the free re-captures are not
            // mistaken for scrolls that moved nothing.
            info!(
                "step {}: {ticks} notch(es){} moved {dy}px of a {viewport_px}px viewport; next burst {next_ticks}",
                stitcher.frames(),
                if wheeled { "" } else { " (no wheel)" }
            );
            ticks = next_ticks;
            continue;
        }

        // A step that never injected a wheel — the retry after a held frame,
        // or the re-capture after a pause — proves nothing about where the
        // document ends. Counting it would let a user jiggling the mouse
        // twice in a row declare a half-read page "complete".
        if !wheeled {
            continue;
        }

        zero_streak += 1;
        if total_dy > 0 {
            // We have moved before, so an unchanging picture means the
            // document ended.
            if zero_streak >= ZERO_STREAK_END {
                break DriveResult::Complete;
            }
            continue;
        }

        // Nothing has *ever* moved. That is not a short document, it is an
        // input path that is not landing: an elevated Windows target whose
        // UIPI eats our SendInput, a macOS window that never came forward, or
        // a surface that ignores synthetic wheel entirely.
        if zero_streak >= ZERO_STREAK_FALLBACK {
            if use_message_wheel {
                break DriveResult::NoMovement;
            }
            info!("no movement after {zero_streak} wheel bursts; posting the wheel to the target instead");
            use_message_wheel = true;
            zero_streak = 0;
        }
    };

    // A run that never moved reports that regardless of how it ended: the
    // shell tells the user why they got a single screenshot instead of a
    // long one, and the session is still written.
    let outcome = if stitcher.frames() == 1 && total_dy == 0 {
        DriveResult::NoMovement
    } else {
        outcome
    };

    // Last chance for a cancel that arrived while we were settling — the
    // point of cancel is that no session appears.
    if signals.canceled() {
        info!("canceled during the final step; writing nothing");
        return Ok(());
    }

    let (frames, height_px) = (stitcher.frames(), stitcher.height());
    let composite = stitcher.finish();
    let quality = composite.quality;
    let path = output::write_session(&cfg.session_dir, composite)?;
    info!("scroll session written to {path:?} ({frames} frames, {height_px}px, {outcome:?}, quality {quality:?})");
    emit(&DriveEvent::Done {
        result: outcome,
        frames,
        height_px,
    });
    Ok(())
}

/// How the rewind ended. Only a `cancel` aborts the run — every other
/// ending, including hitting a cap, just means "start capturing here".
enum Rewind {
    Finished(String),
    Canceled,
}

/// Wind the document back to the top before frame 0.
///
/// The same mechanism as the end detection, run in reverse: burst the
/// wheel upward, wait for the region to settle, and stop once two
/// consecutive bursts change nothing. Nothing is stitched on the way up,
/// so the bursts are large ([`REWIND_TICKS`]) and only the *fact* of
/// movement matters, not how much.
///
/// It never fails the run. A page that will not wind back — a chat log
/// that loads history upward forever, a target that ignores synthetic
/// wheel entirely — hits a cap and the capture starts from wherever it
/// got to, which is exactly what would have happened without a rewind.
/// That is also why the [`input::wheel_message`] fallback rung is
/// deliberately *not* run here: "nothing moved going up" is ambiguous (already at the
/// top, or input is being ignored) and the downward phase is where that
/// distinction can actually be drawn.
///
/// Esc and a `stop` end the rewind and begin the capture from here — the
/// same "end this phase, keep going with what you have" they mean during
/// the capture, where what you have is the current scroll position. Only
/// `cancel` abandons the run.
fn rewind_to_top(run: &Run) -> anyhow::Result<Rewind> {
    // Pushed forward by however long each pause lasted, so `REWIND_MAX_ELAPSED`
    // measures time spent winding rather than time spent waiting for the user.
    let mut started = Instant::now();
    let mut still_streak = 0u32;
    let mut previous = frame::capture_region(run.cfg.region)?;

    for burst in 1..=REWIND_MAX_BURSTS {
        if run.signals.canceled() {
            return Ok(Rewind::Canceled);
        }
        // Same stop conditions as the capture loop — Esc, a `stop`, the
        // target window going away — and the same pause when the user takes
        // the mouse back. The elapsed cap below is measured against the
        // clock, not against the pause, for the same reason it is during the
        // capture: waiting for the user is not winding their document.
        if let Some(reason) = stop_requested(run) {
            return Ok(Rewind::Finished(format!("{reason}; capturing from here")));
        }
        match pause_while_drifting(run, 0, 0, DriveState::Rewinding) {
            Paused::Ready {
                waited,
            } => started += waited,
            Paused::Canceled => return Ok(Rewind::Canceled),
            Paused::Stopped(reason) => return Ok(Rewind::Finished(format!("{reason}; capturing from here"))),
        }

        emit(&DriveEvent::Status {
            frames: 0,
            height_px: 0,
            state: DriveState::Rewinding,
            resume_in_s: None,
        });

        input::wheel_burst(run.cfg.point, REWIND_TICKS, input::WheelDir::Up);
        let current = settle(run.cfg.region, run.esc_latch)?;

        if band_equal(&previous, &current) {
            still_streak += 1;
            if still_streak >= REWIND_STILL_STREAK {
                return Ok(Rewind::Finished(format!("reached the top after {burst} burst(s)")));
            }
        } else {
            still_streak = 0;
        }
        previous = current;

        if started.elapsed() >= REWIND_MAX_ELAPSED {
            return Ok(Rewind::Finished(format!(
                "gave up after {}s; capturing from here",
                REWIND_MAX_ELAPSED.as_secs()
            )));
        }
    }

    Ok(Rewind::Finished(format!(
        "gave up after {REWIND_MAX_BURSTS} bursts; capturing from here"
    )))
}

fn status(stitcher: &Stitcher, state: DriveState) {
    emit(&DriveEvent::Status {
        frames: stitcher.frames(),
        height_px: stitcher.height(),
        state,
        resume_in_s: None,
    });
}

/// Everything about a run that is fixed once it starts: what to scroll and
/// where, and the two channels the user can interrupt it through. Bundled
/// because every polling helper below needs all of it and none of it moves
/// — `target_rect` in particular is sampled once, since a window that has
/// been moved or resized has invalidated the rect we are photographing.
struct Run<'a> {
    cfg: &'a DriveArgs,
    signals: &'a Signals,
    esc_latch: &'a AtomicBool,
    target: Target,
    target_rect: Option<ScreenRect>,
}

/// Why the run should stop now, if it should. Everything here keeps the
/// partial capture. The cursor is deliberately not one of them — see
/// [`pause_while_drifting`].
fn stop_requested(run: &Run) -> Option<String> {
    if run.signals.stop.load(Ordering::Acquire) {
        return Some("stop command from the shell".into());
    }
    // The latch catches taps that landed inside a wait; the direct read
    // covers a key held down right now.
    if run.esc_latch.swap(false, Ordering::AcqRel) || input::escape_pressed() {
        return Some("Esc pressed".into());
    }
    if !input::is_window(run.target) {
        return Some("target window closed".into());
    }
    if run.target_rect.is_some() && input::window_rect(run.target) != run.target_rect {
        return Some("target window moved or resized".into());
    }
    None
}

/// How a pause ended.
enum Paused {
    /// The cursor is on the scroll point and the run may carry on. `waited`
    /// is `ZERO` when there was never anything to wait for, which is the
    /// overwhelmingly common case.
    Ready { waited: Duration },
    /// The user ended the run while it was paused. Keep what we have.
    Stopped(String),
    /// The user abandoned the run while it was paused. Write nothing.
    Canceled,
}

/// Hold the run for as long as the user has the mouse.
///
/// The driver parks the cursor on the scroll point because that is what
/// aims the wheel, so the moment the user moves the pointer two things go
/// wrong at once: our scrolls land wherever they are now pointing, and the
/// frames we photograph pick up whatever they are doing — a hover
/// highlight, a text selection, a menu. Stopping the run over it (which is
/// what this used to do) throws away a capture the user never asked to end.
///
/// So: past [`CURSOR_DRIFT_PX`] this waits. Every poll that finds the
/// cursor somewhere new restarts the clock, so a user who keeps moving
/// stays paused; [`RESUME_STILL_FOR`] of stillness parks the cursor back on
/// the scroll point and hands the run back. `stop`, `cancel`, Esc and the
/// target window going away are all still honored while paused — a paused
/// run is not an unresponsive one.
///
/// `resume_state` is what the run goes back to doing: the rewind and the
/// capture loop pause identically but resume into different phases, and a
/// HUD left saying the wrong one is worse than one left saying "paused".
fn pause_while_drifting(run: &Run, frames: u32, height_px: u32, resume_state: DriveState) -> Paused {
    if input::cursor_drift(run.cfg.point) <= CURSOR_DRIFT_PX {
        return Paused::Ready {
            waited: Duration::ZERO,
        };
    }

    let began = Instant::now();
    info!("the cursor left the scroll point; pausing");
    let paused = |resume_in_s: Option<u64>| {
        emit(&DriveEvent::Status {
            frames,
            height_px,
            state: if resume_in_s.is_some() {
                DriveState::Resuming
            } else {
                DriveState::Paused
            },
            resume_in_s,
        })
    };
    paused(None);

    // Where the current still period started, and when. Both restart on any
    // movement past the slop, so this measures "still since", not "moved
    // since the last poll" — a slow continuous creep never resumes.
    let mut still_at = input::cursor_pos();
    let mut still_since = Instant::now();
    // The countdown currently on the HUD, so it is re-emitted once per whole
    // second rather than at every 50ms poll.
    let mut counting_down: Option<u64> = None;

    loop {
        if run.signals.canceled() {
            return Paused::Canceled;
        }
        if let Some(reason) = stop_requested(run) {
            return Paused::Stopped(reason);
        }
        if began.elapsed() >= PAUSE_MAX {
            return Paused::Stopped(format!(
                "the mouse was in use for {}s; keeping the capture so far",
                PAUSE_MAX.as_secs()
            ));
        }

        wait_latching_escape(PAUSE_POLL, run.esc_latch);

        let Some(now_at) = input::cursor_pos() else {
            // No cursor reading is not evidence of stillness; wait for one.
            continue;
        };
        if still_at.is_none_or(|was| input::chebyshev(now_at, was) > PAUSE_STILL_SLOP_PX) {
            still_at = Some(now_at);
            still_since = Instant::now();
            // Moving again — take back the promise, don't just freeze the
            // number where it stood.
            if counting_down.take().is_some() {
                paused(None);
            }
            continue;
        }

        let still_for = still_since.elapsed();
        if still_for < RESUME_STILL_FOR {
            // Whole seconds left, rounded up, so the last tick shown is "1"
            // and the resume lands as it would have hit zero.
            if still_for >= COUNTDOWN_AFTER {
                let left = RESUME_STILL_FOR.saturating_sub(still_for);
                let secs = left.as_secs() + u64::from(left.subsec_nanos() > 0);
                if counting_down != Some(secs) {
                    counting_down = Some(secs);
                    paused(Some(secs));
                }
            }
            continue;
        }

        // They have let go. Take the pointer back and carry on — the wheel
        // has to be aimed from the point the user picked, and every frame
        // from here has to look like every frame before the pause.
        input::park_cursor(run.cfg.point);
        let waited = began.elapsed();
        info!("the cursor has been still for {RESUME_STILL_FOR:?}; re-parked and resuming after {waited:?}");
        emit(&DriveEvent::Status {
            frames,
            height_px,
            state: resume_state,
            resume_in_s: None,
        });
        return Paused::Ready {
            waited,
        };
    }
}

/// Latch Esc into `latch` if it is down right now. A level read on
/// purpose: `GetAsyncKeyState`'s "pressed since last call" bit is a race
/// against every other poller in the process (see
/// [`input::escape_pressed`]), while frequent level sampling into a latch
/// buys the same reliability honestly.
fn poll_escape(latch: &AtomicBool) {
    if input::escape_pressed() {
        latch.store(true, Ordering::Release);
    }
}

/// Sleep for `total`, polling Esc into `latch` every [`SETTLE_POLL`]. A
/// key tap is held for roughly 100ms, so 50ms sampling catches it — where
/// a single sample per step (spanning wheel + settle + stitch, hundreds of
/// milliseconds) missed almost every tap of the advertised stop key.
fn wait_latching_escape(total: Duration, latch: &AtomicBool) {
    let deadline = Instant::now() + total;
    loop {
        poll_escape(latch);
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return;
        }
        std::thread::sleep(left.min(SETTLE_POLL));
    }
}

/// Has the run hit one of its hard caps? `paused_for` is subtracted from
/// the wall clock: [`MAX_ELAPSED`] bounds how long we spend scrolling
/// someone's window, and time spent waiting for them to put the mouse down
/// is not that — without this, one interruption silently truncates a
/// capture that was going fine.
fn cap_reached(stitcher: &Stitcher, started: Instant, paused_for: Duration) -> Option<String> {
    if stitcher.frames() >= MAX_FRAMES {
        return Some(format!("frame cap ({MAX_FRAMES})"));
    }
    if stitcher.height() >= MAX_HEIGHT_PX {
        return Some(format!("height cap ({MAX_HEIGHT_PX}px)"));
    }
    let elapsed = started.elapsed().saturating_sub(paused_for);
    if elapsed >= MAX_ELAPSED {
        return Some(format!("time cap ({}s)", elapsed.as_secs()));
    }
    None
}

/// Re-photograph the region until it holds still, then return the frame it
/// held still at.
///
/// This is what makes the driver work on smooth-scrolling apps: a fixed
/// delay either wastes time on a fast page or photographs a browser
/// mid-animation, and a blurred-together frame poisons the stitch. Waiting
/// for two consecutive identical captures handles animation and
/// lazily-loaded content with one mechanism — but only after
/// [`SETTLE_MIN`]: matching pairs earlier than that usually mean the
/// target has not processed the wheel yet, not that it has finished
/// (see [`settle_accepts`]). On timeout we take the latest frame
/// regardless — a page with something perpetually moving on it is still
/// worth capturing. The waits double as Esc sampling windows, since this
/// is where each step spends most of its time.
fn settle(region: ScreenRect, esc_latch: &AtomicBool) -> anyhow::Result<Frame> {
    let started = Instant::now();
    let deadline = started + SETTLE_TIMEOUT;
    let mut previous = frame::capture_region(region)?;
    loop {
        wait_latching_escape(SETTLE_POLL, esc_latch);
        let current = frame::capture_region(region)?;
        if settle_accepts(started.elapsed(), band_equal(&previous, &current)) {
            return Ok(current);
        }
        previous = current;
        if Instant::now() >= deadline {
            info!(
                "region never settled within {}ms; taking the latest frame",
                SETTLE_TIMEOUT.as_millis()
            );
            return Ok(previous);
        }
    }
}

/// May a matching pair of captures end the settle wait? Only once
/// [`SETTLE_MIN`] has passed since the injection: before that, an
/// unchanged picture is more likely a target that has not repainted the
/// wheel yet than one that finished animating, and accepting it returns
/// the pre-scroll frame — which the stitcher reads as `dy == 0`, two of
/// which declare the document complete mid-scroll.
fn settle_accepts(since_injection: Duration, pair_matches: bool) -> bool {
    pair_matches && since_injection >= SETTLE_MIN
}

/// Are two captures of the region identical across the content band?
///
/// Columns within `side_ignore` of either edge are excluded: that is where
/// the scrollbar lives, and its thumb moves on every step while browsers
/// fade theirs out for a second afterwards — comparing those columns would
/// mean the region "never settles" on exactly the apps that need settling
/// most. The band is deliberately crude; working out the real header and
/// footer geometry is the stitcher's job, and all this has to decide is
/// whether the picture has stopped moving.
fn band_equal(a: &Frame, b: &Frame) -> bool {
    if a.width != b.width || a.height != b.height {
        return false;
    }
    let stride = a.stride();
    let side_bytes = side_ignore(a.width) as usize * 4;
    let Some(span) = stride
        .checked_sub(side_bytes * 2)
        .filter(|s| *s > 0)
    else {
        // A region narrower than the margins it would ignore: compare it all.
        return a.bgra == b.bgra;
    };
    (0..a.height as usize).all(|y| {
        let start = y * stride + side_bytes;
        a.bgra[start..start + span] == b.bgra[start..start + span]
    })
}

/// Per-side column margin to ignore when asking "did this change?".
/// ShareX's rule, and for the same reasons: wide enough to swallow any
/// scrollbar, proportional so it scales with the region, capped so a narrow
/// region still has a band left to compare.
fn side_ignore(width: u32) -> u32 {
    50.max(width / 20).min(width / 3)
}

/// Aim the next step at between an eighth and two thirds of the viewport:
/// small enough that the stitcher always has overlap to register against,
/// large enough not to spend the frame budget on one article. Notches are
/// never assumed to mean a distance — this reacts to the distance the
/// target actually moved.
///
/// `viewport_px` is the captured frame's height, not the region's: the two
/// differ by the display's scale factor on macOS, and a comparison in mixed
/// units would peg the burst size at one end of its range.
fn adapt_ticks(ticks: u32, dy: u32, viewport_px: u32) -> u32 {
    if viewport_px == 0 {
        return ticks;
    }
    if dy < viewport_px / 8 {
        (ticks * 2).min(TICKS_MAX)
    } else if dy > viewport_px * 2 / 3 {
        (ticks / 2).max(TICKS_MIN)
    } else {
        ticks
    }
}

// ── stdin ──────────────────────────────────────────────────────────────

/// Requests from the shell, set by the reader thread and polled by the
/// loop. Flags rather than a channel: the loop only ever asks "should I
/// still be running", and it asks between steps, never inside one.
#[derive(Default)]
struct Signals {
    stop: AtomicBool,
    cancel: AtomicBool,
}

impl Signals {
    fn canceled(&self) -> bool {
        self.cancel.load(Ordering::Acquire)
    }
}

fn spawn_stdin_reader(signals: Arc<Signals>) {
    std::thread::Builder::new()
        .name("scroll-stdin".into())
        .spawn(move || {
            for line in std::io::stdin().lock().lines() {
                let line = match line {
                    Ok(l) => l,
                    Err(e) if e.kind() == std::io::ErrorKind::BrokenPipe => {
                        warn!("stdin: broken pipe, treating as parent death");
                        break;
                    }
                    Err(e) => {
                        // Anything else — most plausibly an unredirected
                        // stdin with no console behind it — means we have no
                        // command channel, not that the parent is gone.
                        // Abandoning the capture over it would be worse than
                        // running without stop/cancel.
                        warn!("stdin: unusable ({e}); running without a command channel");
                        return;
                    }
                };
                // Strip a UTF-8 BOM as well: .NET's default UTF8Encoding
                // prefixes one to its first line.
                let trimmed = line.trim().trim_start_matches('\u{feff}');
                if trimmed.is_empty() {
                    continue;
                }
                match serde_json::from_str::<DriveCommand>(trimmed) {
                    Ok(DriveCommand::Stop) => {
                        info!("stop requested");
                        signals.stop.store(true, Ordering::Release);
                    }
                    Ok(DriveCommand::Cancel) => {
                        info!("cancel requested");
                        signals.cancel.store(true, Ordering::Release);
                    }
                    // Garbage on the command channel must not take out a
                    // capture that is going fine.
                    Err(e) => warn!("stdin: ignoring unparseable command ({e}): {trimmed}"),
                }
            }
            // The shell holds our stdin pipe open for its whole life, so EOF
            // means it is gone — and a session directory nobody is watching
            // is worse than none at all.
            info!("stdin closed; treating as cancel");
            signals.cancel.store(true, Ordering::Release);
        })
        .expect("spawn scroll stdin reader thread");
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;
    use clowd_rust_core::geometry::RectExt;

    #[test]
    fn events_match_the_wire_contract() {
        assert_eq!(serde_json::to_string(&DriveEvent::Ready).unwrap(), r#"{"type":"ready"}"#);
        assert_eq!(
            serde_json::to_string(&DriveEvent::Status {
                frames: 12,
                height_px: 4180,
                state: DriveState::Scrolling,
                resume_in_s: None,
            })
            .unwrap(),
            r#"{"type":"status","frames":12,"height_px":4180,"state":"scrolling"}"#
        );
        // The countdown field appears only where it means something, so
        // every other status line keeps the shape shells already parse.
        assert_eq!(
            serde_json::to_string(&DriveEvent::Status {
                frames: 12,
                height_px: 4180,
                state: DriveState::Resuming,
                resume_in_s: Some(2),
            })
            .unwrap(),
            r#"{"type":"status","frames":12,"height_px":4180,"state":"resuming","resume_in_s":2}"#
        );
        assert_eq!(
            serde_json::to_string(&DriveEvent::Done {
                result: DriveResult::Complete,
                frames: 31,
                height_px: 9800,
            })
            .unwrap(),
            r#"{"type":"done","result":"complete","frames":31,"height_px":9800}"#
        );
        assert_eq!(
            serde_json::to_string(&DriveEvent::FatalError {
                message: "boom".into(),
            })
            .unwrap(),
            r#"{"type":"fatal_error","message":"boom"}"#
        );
    }

    #[test]
    fn every_state_and_result_is_snake_case() {
        let states = [
            (DriveState::Rewinding, "rewinding"),
            (DriveState::Paused, "paused"),
            (DriveState::Resuming, "resuming"),
            (DriveState::Scrolling, "scrolling"),
            (DriveState::Settling, "settling"),
            (DriveState::Stitching, "stitching"),
        ];
        for (state, expected) in states {
            assert_eq!(serde_json::to_string(&state).unwrap(), format!("\"{expected}\""));
        }
        let results = [
            (DriveResult::Complete, "complete"),
            (DriveResult::Stopped, "stopped"),
            (DriveResult::MaxReached, "max_reached"),
            (DriveResult::NoMovement, "no_movement"),
            (DriveResult::Failed, "failed"),
        ];
        for (result, expected) in results {
            assert_eq!(serde_json::to_string(&result).unwrap(), format!("\"{expected}\""));
        }
    }

    #[test]
    fn commands_parse() {
        assert!(matches!(serde_json::from_str(r#"{"type":"stop"}"#), Ok(DriveCommand::Stop)));
        assert!(matches!(serde_json::from_str(r#"{"type":"cancel"}"#), Ok(DriveCommand::Cancel)));
        assert!(serde_json::from_str::<DriveCommand>(r#"{"type":"shutdown"}"#).is_err());
    }

    #[test]
    fn drive_args_require_the_scroll_trio() {
        let base = ["clowd_scroll_driver"];
        let missing_all = CliArgs::parse_from(base);
        assert!(DriveArgs::from_cli(&missing_all).is_err());

        let full = CliArgs::parse_from([
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "0,0,800,600",
            "--point",
            "400,300",
        ]);
        let args = DriveArgs::from_cli(&full).unwrap();
        assert_eq!(args.region, ScreenRect::from_xy_size(0, 0, 800, 600));
        assert_eq!(args.point, ScreenPoint::new(400, 300));
        assert_eq!(args.hwnd, 0);
    }

    #[test]
    fn rewinding_is_on_unless_the_shell_opts_out() {
        // The default lives in one place — the flag is negative on both
        // sides of the boundary — so a shell that passes nothing rewinds.
        let base = [
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "0,0,800,600",
            "--point",
            "400,300",
        ];
        assert!(
            DriveArgs::from_cli(&CliArgs::parse_from(base))
                .unwrap()
                .rewind
        );

        let opted_out = CliArgs::parse_from(
            base.iter()
                .copied()
                .chain(["--no-rewind"])
                .collect::<Vec<_>>(),
        );
        assert!(
            !DriveArgs::from_cli(&opted_out)
                .unwrap()
                .rewind
        );
    }

    #[test]
    fn drive_args_clamp_a_point_outside_the_region() {
        let outside = CliArgs::parse_from([
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "0,0,800,600",
            "--point",
            "900,-5",
        ]);
        let args = DriveArgs::from_cli(&outside).unwrap();
        assert_eq!(args.point, ScreenPoint::new(799, 0));
    }

    #[test]
    fn drive_args_leave_an_edge_point_alone() {
        // The overlay clamps to max-1 before writing the marker; that exact
        // point must survive the driver's own clamp unchanged.
        let edge = CliArgs::parse_from([
            "clowd_scroll_driver",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "10,20,800,600",
            "--point",
            "809,619",
        ]);
        assert_eq!(DriveArgs::from_cli(&edge).unwrap().point, ScreenPoint::new(809, 619));
    }

    #[test]
    fn ticks_adapt_to_measured_displacement() {
        // A step that barely moved: push harder, up to the ceiling.
        assert_eq!(adapt_ticks(2, 10, 800), 4);
        assert_eq!(adapt_ticks(TICKS_MAX, 10, 800), TICKS_MAX);
        // A step that nearly cleared the viewport: back off, never below one.
        assert_eq!(adapt_ticks(4, 700, 800), 2);
        assert_eq!(adapt_ticks(TICKS_MIN, 700, 800), TICKS_MIN);
        // Comfortably inside the window: leave it alone.
        assert_eq!(adapt_ticks(2, 400, 800), 2);
    }

    #[test]
    fn side_ignore_has_a_floor_and_a_ceiling() {
        // Narrow region: the W/3 cap wins so a band survives to compare.
        assert_eq!(side_ignore(90), 30);
        // Typical region: the 50px floor wins over W/20.
        assert_eq!(side_ignore(800), 50);
        // Wide region: W/20 takes over.
        assert_eq!(side_ignore(2000), 100);
    }

    fn test_frame(width: u32, height: u32, fill: u8) -> Frame {
        Frame {
            bgra: vec![fill; (width * height * 4) as usize],
            width,
            height,
        }
    }

    #[test]
    fn band_equal_ignores_the_side_margins() {
        let a = test_frame(200, 4, 0);
        let mut b = test_frame(200, 4, 0);
        // side_ignore(200) = 50px, so column 10 is inside the ignored margin
        // and column 100 is not.
        b.bgra[10 * 4] = 255;
        assert!(band_equal(&a, &b));
        b.bgra[100 * 4] = 255;
        assert!(!band_equal(&a, &b));
    }

    #[test]
    fn band_equal_rejects_a_size_change() {
        assert!(!band_equal(&test_frame(200, 4, 0), &test_frame(200, 5, 0)));
    }

    #[test]
    fn settle_needs_both_a_match_and_the_minimum_delay() {
        // A pair that matches straight after injection is the target not
        // having processed the wheel yet, not a settled page.
        assert!(!settle_accepts(Duration::ZERO, true));
        assert!(!settle_accepts(SETTLE_MIN - Duration::from_millis(1), true));
        assert!(!settle_accepts(SETTLE_TIMEOUT, false));
        assert!(settle_accepts(SETTLE_MIN, true));
        // The minimum must leave room to actually settle before the
        // timeout escape hatch fires.
        assert!(SETTLE_MIN < SETTLE_TIMEOUT);
    }

    #[test]
    fn paused_time_does_not_count_against_the_wall_clock_cap() {
        // The point of excluding it: a user who takes the mouse for a minute
        // must not come back to a capture that quietly gave up at its time
        // cap while it was waiting for them.
        let stitcher = Stitcher::new(test_frame(200, 100, 0));
        let started = Instant::now() - (MAX_ELAPSED + Duration::from_secs(5));
        assert!(cap_reached(&stitcher, started, Duration::ZERO).is_some());
        assert!(cap_reached(&stitcher, started, MAX_ELAPSED + Duration::from_secs(5)).is_none());
    }
}
