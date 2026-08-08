//! The driver loop and its conversation with the shell.
//!
//! One NDJSON object per line, same shape as the persistent host's protocol
//! (`clowd_capture/src/host/protocol.rs`) and under the same rule: **stdout carries only
//! protocol lines**. Every log record goes to stderr and the session's
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
//! Three things end a run early, and all three keep the partial capture:
//! the user pressing Esc (level-sampled every 50ms throughout the waits
//! and latched, because the *target* owns the keyboard focus, not us, and
//! a tap only lasts ~100ms — one sample per step would miss most of them),
//! the user moving the mouse off the parked scroll point, and a `stop`
//! from the shell's HUD. The drift stop alone finalizes through a short
//! grace window in which a `cancel` still wins: the HUD's buttons can only
//! be reached by moving the cursor, so the drift always fires just before
//! the click lands.

use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use windows::Win32::UI::HiDpi::{GetAwarenessFromDpiAwarenessContext, GetThreadDpiAwarenessContext, DPI_AWARENESS_PER_MONITOR_AWARE};

use crate::cli::CliArgs;
use crate::frame::{self, Frame};
use crate::input::{self, HWND};
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
/// colour, whatever it dims while inactive — or frame 0 disagrees with
/// every frame after it.
const START_DELAY: Duration = Duration::from_millis(350);

/// How far the cursor may drift from the parked scroll point before we read
/// it as the user taking the mouse back. Generous enough to survive a
/// nudged desk, small enough that an intentional move always registers.
const CURSOR_DRIFT_PX: i32 = 8;

/// How long a drift-triggered stop keeps listening for a `cancel` before
/// finalizing. The HUD's FINISH and CANCEL buttons can only be reached by
/// moving the cursor off the parked point, so the drift stop always fires
/// first and the click lands a beat later — and finalizing immediately
/// would turn a CANCEL into a kept session with an editor window on top of
/// it. Long enough for a click already in flight, short enough that a
/// plain mouse-grab stop still feels instant. Only drift gets this grace;
/// Esc and a stdin `stop` finalize immediately.
const DRIFT_CANCEL_GRACE: Duration = Duration::from_millis(400);

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
/// dropping to the `WM_MOUSEWHEEL` rung. Higher than `ZERO_STREAK_END`
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
    Status { frames: u32, height_px: u32, state: DriveState },
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
    Scrolling,
    Settling,
    Stitching,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
enum DriveResult {
    /// Reached the bottom of the document.
    Complete,
    /// Esc, mouse movement, a `stop` command, or the target window going
    /// away. Partial capture kept.
    Stopped,
    /// Hit one of the hard caps. Partial capture kept.
    MaxReached,
    /// The target never scrolled — nothing we could inject moved it. A
    /// single-frame session is still written.
    NoMovement,
    /// No session was produced. Part of the contract because the shell has
    /// to handle it; the driver currently reports every failure it can
    /// recognise as `fatal_error` instead, since those all happen before
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
    check_dpi_awareness();

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
    // signalling.
    let esc_latch = AtomicBool::new(false);

    emit(&DriveEvent::Ready);

    input::focus(target);
    input::park_cursor(cfg.point);
    wait_latching_escape(START_DELAY, &esc_latch);

    if cfg.rewind {
        match rewind_to_top(&cfg, &signals, &esc_latch, target, target_rect)? {
            // The user asked to abandon the run while it was still winding
            // back; there is nothing captured to keep.
            Rewind::Cancelled => {
                info!("cancelled during rewind; writing nothing");
                return Ok(());
            }
            Rewind::Finished(why) => info!("rewind finished: {why}"),
        }
    }

    let frame0 = frame::capture_region(cfg.region)?;
    let mut stitcher = Stitcher::new(frame0);

    let started = Instant::now();
    let mut ticks = TICKS_INITIAL;
    let mut zero_streak = 0u32;
    let mut total_dy = 0u32;
    let mut holds = 0u32;
    // Set when the stitcher holds a frame: the next iteration re-captures
    // without wheeling, because the page is already displaced by an amount
    // that could not be measured and wheeling again would compound it.
    let mut skip_wheel = false;
    let mut use_message_wheel = false;

    let outcome = loop {
        if signals.cancelled() {
            info!("cancelled after {} frames; writing nothing", stitcher.frames());
            return Ok(());
        }
        if let Some(stop) = stop_requested(&signals, &esc_latch, &cfg, target, target_rect) {
            info!("stopping: {}", stop.reason);
            // A drift stop is as likely the user reaching for the HUD as it
            // is a grab of the mouse; hold finalization briefly so a CANCEL
            // click already in flight still means "write nothing".
            if stop.cursor_drift && cancel_within(&signals, DRIFT_CANCEL_GRACE) {
                info!("cancel arrived within the drift grace window; writing nothing");
                return Ok(());
            }
            break DriveResult::Stopped;
        }
        if let Some(cap) = cap_reached(&stitcher, started) {
            info!("stopping: {cap}");
            break DriveResult::MaxReached;
        }

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
                input::wheel_burst(ticks, input::WheelDir::Down);
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
            ticks = adapt_ticks(ticks, dy, cfg.region.height().max(0) as u32);
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
        // input path that is not landing: an elevated target eating our
        // SendInput, or a surface that ignores synthetic wheel entirely.
        if zero_streak >= ZERO_STREAK_FALLBACK {
            if use_message_wheel {
                break DriveResult::NoMovement;
            }
            info!("no movement after {zero_streak} wheel bursts; falling back to WM_MOUSEWHEEL");
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
    if signals.cancelled() {
        info!("cancelled during the final step; writing nothing");
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
    Cancelled,
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
/// That is also why the `WM_MOUSEWHEEL` fallback ladder is deliberately
/// *not* run here: "nothing moved going up" is ambiguous (already at the
/// top, or input is being ignored) and the downward phase is where that
/// distinction can actually be drawn.
///
/// Esc and a `stop` end the rewind and begin the capture from here — the
/// same "end this phase, keep going with what you have" they mean during
/// the capture, where what you have is the current scroll position. Only
/// `cancel` abandons the run.
fn rewind_to_top(
    cfg: &DriveArgs,
    signals: &Signals,
    esc_latch: &AtomicBool,
    target: HWND,
    target_rect: Option<ScreenRect>,
) -> anyhow::Result<Rewind> {
    let started = Instant::now();
    let mut still_streak = 0u32;
    let mut previous = frame::capture_region(cfg.region)?;

    for burst in 1..=REWIND_MAX_BURSTS {
        if signals.cancelled() {
            return Ok(Rewind::Cancelled);
        }
        // Same stop conditions as the capture loop — Esc, a `stop`, the
        // cursor being taken back, the target window going away. Drift gets
        // the same grace as it does there: the HUD's buttons can only be
        // reached by moving the cursor, so a CANCEL click is always a beat
        // behind the drift it caused.
        if let Some(stop) = stop_requested(signals, esc_latch, cfg, target, target_rect) {
            if stop.cursor_drift && cancel_within(signals, DRIFT_CANCEL_GRACE) {
                return Ok(Rewind::Cancelled);
            }
            return Ok(Rewind::Finished(format!("{}; capturing from here", stop.reason)));
        }

        emit(&DriveEvent::Status {
            frames: 0,
            height_px: 0,
            state: DriveState::Rewinding,
        });

        input::wheel_burst(REWIND_TICKS, input::WheelDir::Up);
        let current = settle(cfg.region, esc_latch)?;

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
    });
}

/// Why the run should stop now, if it should. Everything here keeps the
/// partial capture; only `cursor_drift` distinguishes the one reason the
/// shell's own HUD buttons trigger as a side effect, which finalizes
/// through [`DRIFT_CANCEL_GRACE`] instead of immediately.
struct StopReason {
    reason: String,
    cursor_drift: bool,
}

impl StopReason {
    fn immediate(reason: impl Into<String>) -> Self {
        Self {
            reason: reason.into(),
            cursor_drift: false,
        }
    }
}

fn stop_requested(
    signals: &Signals,
    esc_latch: &AtomicBool,
    cfg: &DriveArgs,
    target: HWND,
    target_rect: Option<ScreenRect>,
) -> Option<StopReason> {
    if signals.stop.load(Ordering::Acquire) {
        return Some(StopReason::immediate("stop command from the shell"));
    }
    // The latch catches taps that landed inside a wait; the direct read
    // covers a key held down right now.
    if esc_latch.swap(false, Ordering::AcqRel) || input::escape_pressed() {
        return Some(StopReason::immediate("Esc pressed"));
    }
    let drift = input::cursor_drift(cfg.point);
    if drift > CURSOR_DRIFT_PX {
        return Some(StopReason {
            reason: format!("cursor moved {drift}px off the scroll point"),
            cursor_drift: true,
        });
    }
    if !input::is_window(target) {
        return Some(StopReason::immediate("target window closed"));
    }
    if target_rect.is_some() && input::window_rect(target) != target_rect {
        return Some(StopReason::immediate("target window moved or resized"));
    }
    None
}

/// Wait up to `grace` for a `cancel` to arrive on stdin after a
/// cursor-drift stop. A cancel wins — the caller writes nothing, which is
/// the outcome the user was reaching for. A `stop` merely confirms what is
/// already happening (stopping is idempotent) and ends the wait early.
fn cancel_within(signals: &Signals, grace: Duration) -> bool {
    let deadline = Instant::now() + grace;
    loop {
        if signals.cancelled() {
            return true;
        }
        if signals.stop.load(Ordering::Acquire) {
            return false;
        }
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return false;
        }
        std::thread::sleep(left.min(SETTLE_POLL));
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

fn cap_reached(stitcher: &Stitcher, started: Instant) -> Option<String> {
    if stitcher.frames() >= MAX_FRAMES {
        return Some(format!("frame cap ({MAX_FRAMES})"));
    }
    if stitcher.height() >= MAX_HEIGHT_PX {
        return Some(format!("height cap ({MAX_HEIGHT_PX}px)"));
    }
    let elapsed = started.elapsed();
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
fn adapt_ticks(ticks: u32, dy: u32, region_height: u32) -> u32 {
    if region_height == 0 {
        return ticks;
    }
    if dy < region_height / 8 {
        (ticks * 2).min(TICKS_MAX)
    } else if dy > region_height * 2 / 3 {
        (ticks / 2).max(TICKS_MIN)
    } else {
        ticks
    }
}

/// Everything in this driver — the region rect, `SetCursorPos`, the BitBlt,
/// the `WM_MOUSEWHEEL` lParam — is in physical virtual-desktop pixels, and
/// that only holds while the process is per-monitor DPI aware. Otherwise
/// Windows silently virtualises coordinates and every one of those lands on
/// the wrong pixel on a scaled monitor. `app.manifest` declares PerMonitorV2;
/// this reports if it somehow did not take.
fn check_dpi_awareness() {
    let awareness = unsafe { GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) };
    if awareness != DPI_AWARENESS_PER_MONITOR_AWARE {
        warn!("process is not per-monitor DPI aware ({awareness:?}); coordinates may be virtualised");
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
    fn cancelled(&self) -> bool {
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
            })
            .unwrap(),
            r#"{"type":"status","frames":12,"height_px":4180,"state":"scrolling"}"#
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
    fn drift_grace_lets_a_cancel_win() {
        let signals = Signals::default();
        signals.cancel.store(true, Ordering::Release);
        assert!(cancel_within(&signals, DRIFT_CANCEL_GRACE));
    }

    #[test]
    fn drift_grace_treats_a_stop_as_already_stopping() {
        // FINISH clicked after the drift already decided to stop: the stop
        // is idempotent, and the wait must end early rather than sit out
        // the full grace.
        let signals = Signals::default();
        signals.stop.store(true, Ordering::Release);
        let started = Instant::now();
        assert!(!cancel_within(&signals, Duration::from_secs(5)));
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn drift_grace_expires_quietly_without_signals() {
        let signals = Signals::default();
        assert!(!cancel_within(&signals, Duration::ZERO));
    }

    #[test]
    fn drift_grace_catches_a_cancel_that_arrives_mid_wait() {
        // The race the grace window exists for: the CANCEL click lands a
        // beat after the drift stop fired.
        let signals = Arc::new(Signals::default());
        let clicker = {
            let signals = Arc::clone(&signals);
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_millis(80));
                signals.cancel.store(true, Ordering::Release);
            })
        };
        assert!(cancel_within(&signals, Duration::from_secs(5)));
        clicker.join().unwrap();
    }
}
