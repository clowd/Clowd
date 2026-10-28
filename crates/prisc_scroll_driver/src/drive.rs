//! The driver loop and its conversation with the shell.
//!
//! The conversation is the `scroll` catalog of Prisc's helper protocol
//! (crates/prisc_capture/HELPER_PROTOCOL.md §4.4): `ready`, then `status` at
//! each phase change of every step, then one terminal `done`. **stdout carries
//! only protocol lines**: every log record goes to stderr and the session's
//! `scroll.log`.
//!
//! Back the other way: `stop` (finish now, keep what we have) and `quit`
//! (abandon the run, write nothing; also stdin EOF and signals). Both are
//! polled at the top of each step rather than interrupting one, so a stop
//! that lands mid-settle takes effect a fraction of a second later.
//!
//! Every outcome ends in `done`, cancel included, and exits 0; `session.json`
//! is on disk for every `result` but `cancelled`. A failure is a fatal
//! `error` and exit 1 (exit 2 for unusable arguments).
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

use std::ops::ControlFlow;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use prisc_ipc::catalog::scroll::{self, Event, ScrollResult, ScrollState};
use prisc_ipc::catalog::ErrorCode;
use prisc_ipc::{Channel, Inbound};

use crate::cli::CliArgs;
use crate::frame::{self, Frame};
use crate::input::{self, Target};
use crate::output;
use crate::stitch::{AppendResult, Stitcher};
use prisc_rust_core::geometry::{ScreenPoint, ScreenRect};

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

// ── Protocol ───────────────────────────────────────────────────────────

static OUT: OnceLock<Channel<Event>> = OnceLock::new();

/// The process's protocol channel, set once by [`run`].
fn out() -> &'static Channel<Event> {
    OUT.get()
        .expect("drive::run sets the channel first")
}

/// A failed run, with the protocol error code it is reported under.
struct Failure {
    code: ErrorCode,
    error: anyhow::Error,
}

impl From<anyhow::Error> for Failure {
    fn from(error: anyhow::Error) -> Self {
        Self {
            code: ErrorCode::Failed,
            error,
        }
    }
}

/// Tags an error with the code it is reported under.
fn coded(code: ErrorCode) -> impl FnOnce(anyhow::Error) -> Failure {
    move |error| Failure {
        code,
        error,
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

/// The driver's entry point, called from `main`. Never returns: every outcome
/// ends in `done` and exit 0, every failure in a fatal `error` and exit 1 (2
/// for unusable arguments). `channel` already carried `hello`.
pub fn run(args: CliArgs, channel: Channel<Event>) -> ! {
    if OUT.set(channel).is_err() {
        panic!("drive::run called twice");
    }

    // Requests from the shell. `stop` arrives as a command; `quit`, stdin
    // EOF, signals and a dead stdout all arrive as the quit path, which here
    // means cancel: abandon the run and write nothing.
    let signals = Arc::new(Signals::default());
    let quit = Arc::clone(&signals);
    prisc_ipc::lifetime::install(
        move |reason| {
            info!("cancel requested ({reason:?})");
            quit.cancel.store(true, Ordering::Release);
        },
        scroll::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN,
    );
    let commands = Arc::clone(&signals);
    prisc_ipc::read_commands::<scroll::Command>(move |inbound| {
        match inbound {
            Inbound::Command(scroll::Command::Stop {}) => {
                info!("stop requested");
                commands.stop.store(true, Ordering::Release);
            }
            // Garbage on the command channel must not take out a capture
            // that is going fine. There are no acks to stay in order with, so
            // it is answered from here.
            Inbound::Malformed {
                reason,
            } => out().error(ErrorCode::BadCommand, reason, false),
        }
        ControlFlow::Continue(())
    });

    let result = DriveArgs::from_cli(&args)
        .map_err(coded(ErrorCode::Usage))
        .and_then(|cfg| drive(cfg, &signals));
    match result {
        Ok((result, frames, height_px)) => {
            out().emit(&Event::Done {
                result,
                frames,
                height_px,
            });
            prisc_ipc::lifetime::exit(prisc_ipc::exit::OK)
        }
        Err(Failure {
            code,
            error,
        }) => {
            error!("scroll driver failed: {error:#}");
            prisc_rust_core::telemetry::capture_error(&error);
            out().fatal(code, format!("{error:#}"))
        }
    }
}

/// How the run ended: the `done` fields.
type Outcome = (ScrollResult, u32, u32);

fn drive(cfg: DriveArgs, signals: &Signals) -> Result<Outcome, Failure> {
    // Refuse before the border window and the HUD are on screen for a run
    // that cannot work — on macOS a missing permission means every wheel
    // event we post is discarded in silence.
    input::preflight().map_err(coded(ErrorCode::Permission))?;

    let target = input::resolve_target(cfg.hwnd, cfg.point).ok_or_else(|| Failure {
        code: ErrorCode::InitFailed,
        error: anyhow!("no window at scroll point {:?}", cfg.point),
    })?;
    // Sampled once: a window that moves or resizes mid-run has invalidated
    // the fixed rect we are photographing, and everything after that point
    // would stitch garbage.
    let target_rect = input::window_rect(target);

    // Esc is level-sampled, and a tap only lasts ~100ms, so every wait in
    // this function polls the key into this latch; `stop_requested`
    // consumes it at the top of each step. Only the driver thread touches
    // it — the atomic is for tidy shared references, not cross-thread
    // signaling.
    let esc_latch = AtomicBool::new(false);

    let run = Run {
        cfg: &cfg,
        signals,
        esc_latch: &esc_latch,
        target,
        target_rect,
    };

    out().emit(&Event::Ready {});

    // Before anything is photographed: the target has to actually be the
    // window at the scroll point. The wheel is routed by cursor position and
    // the capture is a screenshot of the screen, so a window covering the point
    // would receive every scroll *and* be the thing in every frame — a tall,
    // plausible-looking picture of entirely the wrong window. Refusing is the
    // only honest outcome.
    if !input::raise_over_point(target, cfg.point) {
        return Err(Failure::from(anyhow!(
            "the window you selected could not be brought in front of the scroll point (window {} is over it). \
             Move whatever is covering it, or pick a scroll point on a visible part of the window.",
            input::describe_window_at(cfg.point)
        )));
    }
    input::park_cursor(cfg.point);
    wait_latching_escape(START_DELAY, &esc_latch);

    if cfg.rewind {
        match rewind_to_top(&run)? {
            // The user asked to abandon the run while it was still winding
            // back; there is nothing captured to keep.
            Rewind::Canceled => {
                info!("canceled during rewind; writing nothing");
                return Ok((ScrollResult::Cancelled, 0, 0));
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
            return Ok((ScrollResult::Cancelled, stitcher.frames(), stitcher.height()));
        }
        if let Some(reason) = stop_requested(&run) {
            info!("stopping: {reason}");
            break ScrollResult::Stopped;
        }
        // The user has the mouse: hold everything until they put it down,
        // then park it back on the scroll point and carry on. Deliberately
        // ahead of the wheel injection — a scroll sent while they are
        // pointing at something else lands in whatever they are pointing at.
        match pause_while_drifting(&run, stitcher.frames(), stitcher.height(), ScrollState::Scrolling) {
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
                return Ok((ScrollResult::Cancelled, stitcher.frames(), stitcher.height()));
            }
            Paused::Stopped(reason) => {
                info!("stopping: {reason}");
                break ScrollResult::Stopped;
            }
        }
        if let Some(cap) = cap_reached(&stitcher, started, paused_for) {
            info!("stopping: {cap}");
            break ScrollResult::MaxReached;
        }

        let wheeled = !skip_wheel;
        if skip_wheel {
            // The page has not moved since the last look; a frame caught
            // mid-repaint is clean a moment later, so look again from the
            // same position instead of scrolling further away from the
            // reference the stitcher is still registering against.
            skip_wheel = false;
        } else {
            status(&stitcher, ScrollState::Scrolling);
            if use_message_wheel {
                if !input::wheel_message(target, cfg.point, ticks) {
                    break ScrollResult::NoMovement;
                }
            } else {
                input::wheel_burst(cfg.point, ticks, input::WheelDir::Down);
            }
        }

        status(&stitcher, ScrollState::Settling);
        let frame = match settle(cfg.region, &esc_latch) {
            Ok(f) => f,
            Err(e) => {
                // Losing the screen DC mid-run is not a reason to throw away
                // what is already composited.
                warn!(
                    "capture failed after {} frames, keeping the partial capture: {e:#}",
                    stitcher.frames()
                );
                break ScrollResult::Stopped;
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

        status(&stitcher, ScrollState::Stitching);
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
                    break ScrollResult::Stopped;
                }
                skip_wheel = true;
                continue;
            }
            AppendResult::Failed => {
                warn!("stitcher gave up; keeping the partial capture");
                break ScrollResult::Stopped;
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
                break ScrollResult::Complete;
            }
            continue;
        }

        // Nothing has *ever* moved. That is not a short document, it is an
        // input path that is not landing: an elevated Windows target whose
        // UIPI eats our SendInput, a macOS window that never came forward, or
        // a surface that ignores synthetic wheel entirely.
        if zero_streak >= ZERO_STREAK_FALLBACK {
            if use_message_wheel {
                break ScrollResult::NoMovement;
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
        ScrollResult::NoMovement
    } else {
        outcome
    };

    // Last chance for a cancel that arrived while we were settling — the
    // point of cancel is that no session appears.
    if signals.canceled() {
        info!("canceled during the final step; writing nothing");
        return Ok((ScrollResult::Cancelled, stitcher.frames(), stitcher.height()));
    }

    let (frames, height_px) = (stitcher.frames(), stitcher.height());
    let composite = stitcher.finish();
    let quality = composite.quality;
    let path = output::write_session(&cfg.session_dir, composite).map_err(coded(ErrorCode::IoFailed))?;
    info!("scroll session written to {path:?} ({frames} frames, {height_px}px, {outcome:?}, quality {quality:?})");
    Ok((outcome, frames, height_px))
}

/// How the rewind ended. Only a `quit` aborts the run — every other
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
/// `quit` abandons the run.
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
        match pause_while_drifting(run, 0, 0, ScrollState::Rewinding) {
            Paused::Ready {
                waited,
            } => started += waited,
            Paused::Canceled => return Ok(Rewind::Canceled),
            Paused::Stopped(reason) => return Ok(Rewind::Finished(format!("{reason}; capturing from here"))),
        }

        out().emit(&Event::Status {
            frames: 0,
            height_px: 0,
            state: ScrollState::Rewinding,
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

fn status(stitcher: &Stitcher, state: ScrollState) {
    out().emit(&Event::Status {
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
/// the scroll point and hands the run back. `stop`, `quit`, Esc and the
/// target window going away are all still honored while paused — a paused
/// run is not an unresponsive one.
///
/// `resume_state` is what the run goes back to doing: the rewind and the
/// capture loop pause identically but resume into different phases, and a
/// HUD left saying the wrong one is worse than one left saying "paused".
fn pause_while_drifting(run: &Run, frames: u32, height_px: u32, resume_state: ScrollState) -> Paused {
    if input::cursor_drift(run.cfg.point) <= CURSOR_DRIFT_PX {
        return Paused::Ready {
            waited: Duration::ZERO,
        };
    }

    let began = Instant::now();
    info!("the cursor left the scroll point; pausing");
    let paused = |resume_in_s: Option<u64>| {
        out().emit(&Event::Status {
            frames,
            height_px,
            state: if resume_in_s.is_some() {
                ScrollState::Resuming
            } else {
                ScrollState::Paused
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
        out().emit(&Event::Status {
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

// ── Requests ───────────────────────────────────────────────────────────

/// Requests from the shell, set by the stdin reader (`stop`) and the quit
/// path (cancel), and polled by the loop. Flags rather than a channel: the
/// loop only ever asks "should I still be running", and it asks between
/// steps, never inside one.
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

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;
    use prisc_rust_core::geometry::RectExt;

    #[test]
    fn drive_args_require_the_scroll_trio() {
        let base = ["prisc_scroll_driver"];
        let missing_all = CliArgs::parse_from(base);
        assert!(DriveArgs::from_cli(&missing_all).is_err());

        let full = CliArgs::parse_from([
            "prisc_scroll_driver",
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
            "prisc_scroll_driver",
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
            "prisc_scroll_driver",
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
            "prisc_scroll_driver",
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
