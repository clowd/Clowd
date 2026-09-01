//! The stdout JSON protocol and the 1 Hz status thread.
//!
//! stdout is the protocol channel and carries nothing but one JSON object per
//! line; every free-form byte this process produces (libobs chatter, warnings,
//! panics) goes to stderr instead. The parent — the Clowd shell — reads stdout
//! line by line, so every emitter here writes exactly one line and flushes it:
//! a status line still sitting in a buffer is a status line the shell will not
//! act on until something else happens to push it out.
//!
//! The emitters are callable from any thread (they take the stdout lock for the
//! duration of one line, which is also what keeps two threads from interleaving
//! halves of two objects). The status thread owns everything else in here.

use std::collections::VecDeque;
use std::io::Write;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use obs_platform::region::Rect;

use crate::obscure;

/// Gate on the status thread's output. False during the prompt phase — there
/// is no mirror yet, so there is no frame rate to report — and flipped on for
/// good by `set_sharing(true)` when the user presses OK.
static SHARING: AtomicBool = AtomicBool::new(false);

/// Sampling period of the status thread, and therefore the rate at which
/// `status` lines appear on stdout.
const TICK_MS: u64 = 1000;

/// Writes one JSON line to stdout (protocol channel; stderr carries all
/// free-form output).
///
/// Write errors are deliberately swallowed: a closed stdout means the parent
/// is gone, and the stdin reader's EOF path is what turns that into an orderly
/// quit. Panicking here — on the graphics-adjacent status thread, or worse
/// inside a command ack — would be a far worse way to discover the same thing.
pub fn emit_json(value: serde_json::Value) {
    let stdout = std::io::stdout();
    let mut lock = stdout.lock();
    let _ = writeln!(lock, "{value}");
    let _ = lock.flush();
}

/// The one place the wire shape of a region is written, so every emitter that
/// carries one agrees on the field names.
fn region_json(region: Rect) -> serde_json::Value {
    serde_json::json!({
        "x": region.x,
        "y": region.y,
        "w": region.w,
        "h": region.h,
    })
}

/// libobs is up and the prompt window is on screen. The parent may now tell
/// the user to point their meeting app's share picker at that window.
pub fn emit_initialized() {
    emit_json(serde_json::json!({ "type": "initialized" }));
}

/// The user pressed OK: the prompt window has shed its chrome, moved off
/// screen and is now the mirror surface. `region` is what is actually being
/// mirrored, which is the requested region after clamping.
pub fn emit_sharing_started(region: Rect) {
    emit_json(serde_json::json!({
        "type": "sharing_started",
        "region": region_json(region),
    }));
}

/// Ack for a `move` command. `region` is the region that was ACTUALLY applied
/// after clamping, not the one the command asked for, so the parent can keep
/// its own border window in sync with what is really being captured.
pub fn emit_region_changed(region: Rect) {
    emit_json(serde_json::json!({
        "type": "region_changed",
        "region": region_json(region),
    }));
}

/// Ack for an `obscure` / `unobscure` command. Both the mode name and its
/// strength are reported so the parent never has to remember which modes carry
/// one — `strength` is 0 for the modes that do not.
pub fn emit_obscure(mode: obscure::Mode) {
    emit_json(serde_json::json!({
        "type": "obscure",
        "mode": obscure::name(mode),
        "strength": obscure::strength(mode),
    }));
}

/// The process is exiting without ever having started mirroring: the user
/// dismissed the prompt (the close button, Escape) or a `quit` arrived while
/// it was still up.
///
/// It is the negative counterpart of [`emit_sharing_started`], and the pair is
/// what lets the parent decide whether to put its own border and floating
/// controls on screen. Waiting for the exit code instead would not do: the
/// process exits 0 either way, so "the user shared" and "the user backed out"
/// are indistinguishable from outside, and the parent would have to guess.
///
/// Emitted immediately before the exit, so it is the last protocol line of a
/// declined session; after it the pipe closes.
pub fn emit_cancelled() {
    emit_json(serde_json::json!({ "type": "cancelled" }));
}

/// A stdin line was malformed, or the command it named failed. Never fatal:
/// the process keeps mirroring and keeps reading commands.
pub fn emit_command_error(msg: &str) {
    emit_json(serde_json::json!({
        "type": "command_error",
        "message": msg,
    }));
}

/// Opens or closes the gate on `status` lines. Called once with `true` from
/// the UI thread the moment the mirror starts (right after
/// [`emit_sharing_started`]).
pub fn set_sharing(on: bool) {
    SHARING.store(on, Ordering::Relaxed);
}

/// Whether [`emit_sharing_started`] has already gone out, i.e. the user
/// accepted the prompt. Read on the exit path to decide whether the session
/// still owes the parent a [`emit_cancelled`] line.
pub fn sharing() -> bool {
    SHARING.load(Ordering::Relaxed)
}

/// Trailing-window frame rate behind the `fps` status field.
///
/// The obvious implementation — the lifetime ratio `total_frames / elapsed` —
/// reads permanently low, because the frame counter trails the clock by a
/// fixed deficit (the frames the display had not drawn yet when sampling
/// began) and that deficit is never repaid: a healthy 30 fps mirror would
/// report ~29 for the first minute and only crawl towards 30 afterwards.
/// Differencing two samples cancels the deficit — it is present at both ends —
/// so a trailing window reports the rate the mirror is actually sustaining
/// *now*, which is also what a stall should visibly move.
#[derive(Default)]
struct FpsWindow {
    /// `(elapsed_ms, total_frames)` samples, oldest first.
    samples: VecDeque<(u64, u64)>,
}

impl FpsWindow {
    /// Span the rate is averaged over. 5 s is the shortest window that keeps
    /// the +/-1 frame quantisation of a 1 Hz sample (+/-0.2 fps here) far
    /// enough inside the rounding a consumer applies to print a whole number.
    const WINDOW_MS: u64 = 5000;

    /// Throws away every sample, so the next `push` starts a fresh baseline.
    /// Used when sharing begins: the samples taken during the prompt phase
    /// describe a window that was not drawing anything at all, and averaging
    /// them into the first live reading would report a frame rate the mirror
    /// never ran at.
    fn reset(&mut self) {
        self.samples.clear();
    }

    /// Records a sample and returns the frame rate over the window ending at
    /// it.
    fn push(&mut self, time_ms: u64, total: u64) -> f64 {
        self.samples.push_back((time_ms, total));

        // Keep the newest sample that is already a full window old as the
        // baseline, so the span stays >= WINDOW_MS rather than collapsing to
        // whatever is left after trimming.
        while self.samples.len() > 2 && time_ms.saturating_sub(self.samples[1].0) >= Self::WINDOW_MS
        {
            self.samples.pop_front();
        }

        let (base_ms, base_frames) = self.samples[0];
        if time_ms > base_ms {
            // Before the window has filled this is simply a shorter span: it
            // is already deficit-free, only noisier. The frame counter is
            // monotonic in practice, but saturating the difference means a
            // counter that somehow went backwards reports 0 rather than
            // wrapping into an astronomical rate.
            (total.saturating_sub(base_frames) as f64 * 1000.0) / (time_ms - base_ms) as f64
        } else if time_ms > 0 {
            // First sample only — nothing to difference against yet.
            (total as f64 * 1000.0) / time_ms as f64
        } else {
            // A zero clock has nothing to divide by; never emit NaN/inf.
            0.0
        }
    }
}

/// Starts the 1 Hz status thread. It runs for the life of the process (there
/// is no stop flag: every exit is `obs_platform::exit_process`, which takes the
/// whole process down without unwinding) and is silent until
/// [`set_sharing`]`(true)`.
///
/// The thread reads only `obscure::frames()` — a plain atomic bumped by the
/// draw callback on the OBS graphics thread — and its own `Instant`, so it
/// never touches the mirror, the UI or any OBS object, and needs no
/// synchronization with either.
pub fn start_status_thread() {
    std::thread::spawn(move || {
        let start = Instant::now();
        let mut window = FpsWindow::default();
        // Tracks the SHARING edge, not its level: the tick that first sees
        // sharing on has to establish a baseline before it can difference
        // anything, so it emits nothing and the first `status` line lands one
        // tick later. A second of silence at the start beats a first reading
        // averaged over the prompt phase.
        let mut sharing_prev = false;

        loop {
            std::thread::sleep(Duration::from_millis(TICK_MS));

            // Sampled on every tick, shared or not, so the two readings are
            // always taken next to each other in time.
            let time_ms = start.elapsed().as_millis() as u64;
            let frames = obscure::frames();

            if !SHARING.load(Ordering::Relaxed) {
                window.reset();
                sharing_prev = false;
                continue;
            }

            if !sharing_prev {
                sharing_prev = true;
                window.reset();
                window.push(time_ms, frames);
                continue;
            }

            // One decimal is the whole useful resolution of a 1 Hz sample over
            // a 5 s window, and it keeps the line free of the long binary
            // tails an f64 division otherwise prints.
            let fps = (window.push(time_ms, frames) * 10.0).round() / 10.0;
            emit_json(serde_json::json!({
                "type": "status",
                "fps": fps,
            }));
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Sample stream of a healthy mirror at `fps`: the draw callback is
    /// `deficit` frames behind the clock from the first sample onward and
    /// stays there.
    fn steady(window: &mut FpsWindow, fps: u64, deficit: u64, secs: u64) -> f64 {
        let mut last = 0.0;
        for s in 1..=secs {
            let total = (fps * s).saturating_sub(deficit);
            last = window.push(s * 1000, total);
        }
        last
    }

    #[test]
    fn fps_window_cancels_the_frame_deficit() {
        // The lifetime ratio this replaced would report 28.5 here (285/10),
        // which rounds to the "29 FPS" a 30 fps mirror would have displayed.
        let mut w = FpsWindow::default();
        let fps = steady(&mut w, 30, 15, 10);
        assert!((fps - 30.0).abs() < 0.01, "expected ~30, got {fps}");

        // ...and the bias does not shrink with a bigger deficit, only the
        // lifetime ratio's does.
        let mut w = FpsWindow::default();
        let fps = steady(&mut w, 60, 90, 10);
        assert!((fps - 60.0).abs() < 0.01, "expected ~60, got {fps}");
    }

    #[test]
    fn fps_window_smooths_whole_frame_quantisation() {
        // A 30 fps mirror sampled at 1 Hz lands on 29/30/31 frames per tick
        // depending on where the sample falls between frames. Once the window
        // has filled, every reading must round to 30 — a single tick still
        // swings by a whole frame, which is exactly what the window is for, so
        // the first WINDOW_MS of (deliberately shorter, noisier) spans is not
        // held to it.
        let mut w = FpsWindow::default();
        let mut total = 0;
        for (i, delta) in [29, 31, 30, 29, 30, 31, 29, 30, 30, 31, 29, 30]
            .into_iter()
            .enumerate()
        {
            total += delta;
            let time_ms = (i as u64 + 1) * 1000;
            let fps = w.push(time_ms, total);
            if time_ms > FpsWindow::WINDOW_MS {
                assert!((fps - 30.0).abs() < 0.5, "tick {i}: {fps}");
            }
        }
    }

    #[test]
    fn fps_window_reports_a_real_drop() {
        // Smoothing must not hide an actual stall: 5 s at 10 fps after 10 s at
        // 30 fps has fully aged the good samples out of the window.
        let mut w = FpsWindow::default();
        steady(&mut w, 30, 0, 10);
        let mut total = 300;
        let mut fps = 0.0;
        for s in 11..=15 {
            total += 10;
            fps = w.push(s * 1000, total);
        }
        assert!((fps - 10.0).abs() < 0.5, "expected ~10, got {fps}");
    }

    #[test]
    fn fps_window_first_sample_falls_back_to_the_lifetime_ratio() {
        let mut w = FpsWindow::default();
        assert_eq!(w.push(1000, 30), 30.0);
        // A zero clock has nothing to divide by and must not produce NaN/inf.
        let mut w = FpsWindow::default();
        assert_eq!(w.push(0, 0), 0.0);
    }

    #[test]
    fn fps_window_never_reports_a_negative_rate() {
        // The frame counter is monotonic in practice; guard the arithmetic
        // anyway so a counter that went backwards cannot wrap into a huge
        // (or, with signed math, negative) rate.
        let mut w = FpsWindow::default();
        w.push(1000, 500);
        assert_eq!(w.push(2000, 10), 0.0);
    }

    #[test]
    fn reset_drops_the_prompt_phase_baseline() {
        // 20 s of prompt phase during which nothing was drawn, then sharing
        // starts and the mirror immediately runs at 30 fps. Without the reset
        // the window's baseline would still be a sample from the idle phase
        // and the first readings would be far below the real rate.
        let mut w = FpsWindow::default();
        for s in 1..=20 {
            w.push(s * 1000, 0);
        }
        w.reset();
        // The tick that observes the sharing edge only establishes a baseline.
        w.push(21_000, 0);
        let fps = w.push(22_000, 30);
        assert!((fps - 30.0).abs() < 0.01, "expected ~30, got {fps}");
    }
}
