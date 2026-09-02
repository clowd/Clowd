//! `--window-capture`: a JSONL sidecar recording the live geometry of every
//! on-screen window that intersects the capture region, in coordinates
//! relative to that region. Mirrors `--input-capture`: same session-fixed CLI
//! shape, same timebase, same arm/close lifetime, its own file.
//!
//! Threading: one dedicated poll thread enumerates windows, diffs them against
//! the previous sample and writes the resulting rows itself. Unlike the
//! input-capture writer thread this needs no channel — the sampler is already
//! off the graphics thread, so blocking it on file I/O only delays the next
//! window sample. A tiny libobs tick callback publishes the current video
//! frame time into an atomic so the poll thread never reads libobs state
//! itself (`tracker.rs` / `input_capture.rs` callback pattern).
//!
//! Timebase: identical to the input-capture sidecar — `t` is milliseconds
//! since the first frame time sampled after `OutputStarted`, minus the
//! track-0 video encoder's accumulated pause offset — so rows in the two
//! files line up without any cross-referencing. Rows are dropped while the
//! output is paused; a window that moved during a pause reports its new rect
//! on the first poll after the resume.
//!
//! Emission is change-driven, not per frame: a window gets a row when it
//! enters the region, whenever its rect or z-order changes, and one final
//! `0,0,0,0` row when it leaves (is closed, minimized, cloaked, or moved
//! clear of the region). Between rows the editor holds the last value.
//!
//! Size: the file is bounded by real user activity, not capped. Emission is
//! change-driven, so an idle desktop costs nothing, but a continuously
//! dragged window writes one row per poll — worst case roughly
//! `fps × duration × moving windows` rows of ~80 bytes. A cap is deliberately
//! not imposed: truncating mid-recording would desync the editor's model more
//! badly than a large file does.
//!
//! Lifetime: exit paths skip Drop (`platform::exit_process`), so the recorder
//! calls [`WindowCapture::close`] explicitly before *every*
//! `emit_stopped_recording`, exactly as it does for the input-capture
//! sidecar.

use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde::Serialize;

use crate::input_capture::{map_t, to_canvas};
use crate::platform::{self, WindowInfo};
use crate::region::Rect;

/// Upper bound on the poll rate. The recording fps sets the cadence (one
/// sample per encoded frame is all the video can show), but a 120/240 fps
/// recording does not justify enumerating every window that often — window
/// motion is driven by the user's hand, not the encoder.
const MAX_POLL_HZ: u32 = 60;

/// Cadence before the recording starts, while the poll loop is only checking
/// its arm flag. Deliberately short: it is also the worst-case delay between
/// `OutputStarted` and the first window sample, and at 100 ms a 30 fps
/// recording would open with three frames of no geometry at all. One atomic
/// load every 5 ms costs nothing measurable, and it bounds the `Drop` join
/// below by the same amount.
const IDLE_POLL: Duration = Duration::from_millis(5);

/// Cap on distinct windows given a wire id in one session. A pathological
/// window-churning app must not grow the identity map (or the sidecar)
/// without bound; past the cap new windows are simply not tracked.
const MAX_WINDOW_IDS: usize = 4096;

// ---------------------------------------------------------------------------
// Wire rows (field names and order are the contract)
// ---------------------------------------------------------------------------

#[derive(Serialize)]
struct HeaderLine {
    #[serde(rename = "type")]
    ty: &'static str,
    version: u32,
    /// Recording region `[x,y,w,h]`: origin in canvas pixels (the same value
    /// the input-capture header carries, so the two files agree on where the
    /// region sits on the desktop) and `w`/`h` the encoded video's exact
    /// dimensions. Window rows are already relative to this origin — it is
    /// here so a consumer can place them back on the desktop.
    region: (i32, i32, u32, u32),
    fps_num: u32,
    fps_den: u32,
    platform: &'static str,
}

/// Identity for a window id, emitted the first time the window is tracked and
/// again whenever its title or app name changes (a browser switching tabs, a
/// document window being renamed). The latest such row wins.
#[derive(Serialize)]
struct WindowInfoRow {
    #[serde(rename = "type")]
    ty: &'static str,
    id: u32,
    title: String,
    app: String,
    pid: u32,
}

/// One window's geometry at time `t`, in canvas pixels relative to the
/// capture region's top-left.
///
/// Rects are NOT clipped to the region: a window straddling the edge reports
/// negative `x`/`y` or an extent running past `w`/`h`, which is what lets the
/// editor draw the part that is actually on screen. The one reserved value is
/// the all-zero rect, which means "no longer tracked" — a tracked window
/// always has a nonzero extent, so the sentinel is unambiguous.
#[derive(Serialize)]
struct WindowRow {
    #[serde(rename = "type")]
    ty: &'static str,
    t: f64,
    id: u32,
    x: i32,
    y: i32,
    w: u32,
    h: u32,
    /// Stacking order among the tracked windows at this instant, 0 = topmost.
    z: u32,
}

/// The geometry half of a [`WindowRow`], i.e. everything the change detector
/// compares (`t` is excluded — it changes every poll by definition).
#[derive(PartialEq, Eq, Clone, Copy)]
struct Geometry {
    x: i32,
    y: i32,
    w: u32,
    h: u32,
    z: u32,
}

impl Geometry {
    /// The all-zero "left the region" sentinel.
    const GONE: Geometry = Geometry {
        x: 0,
        y: 0,
        w: 0,
        h: 0,
        z: 0,
    };

    fn row(self, t: f64, id: u32) -> WindowRow {
        WindowRow {
            ty: "window",
            t,
            id,
            x: self.x,
            y: self.y,
            w: self.w,
            h: self.h,
            z: self.z,
        }
    }
}

// ---------------------------------------------------------------------------
// Pure geometry
// ---------------------------------------------------------------------------

/// True when two capture-space rectangles share at least one pixel. Edge
/// contact does not count: a window flush against the region's left edge is
/// not visible inside it.
fn intersects(win: (i32, i32, u32, u32), region: Rect) -> bool {
    let (wx, wy, ww, wh) = win;
    if ww == 0 || wh == 0 || region.w == 0 || region.h == 0 {
        return false;
    }
    // i64 throughout: a window rect can overflow i32 on its far edge (a
    // maximized window on a display far right of the primary), and wrapping
    // there would read as "no overlap".
    let (wx1, wy1) = (wx as i64, wy as i64);
    let (wx2, wy2) = (wx1 + ww as i64, wy1 + wh as i64);
    let (rx1, ry1) = (region.x as i64, region.y as i64);
    let (rx2, ry2) = (rx1 + region.w as i64, ry1 + region.h as i64);
    wx1 < rx2 && wx2 > rx1 && wy1 < ry2 && wy2 > ry1
}

/// Converts a window's capture-space bounds into canvas pixels relative to the
/// capture region's top-left.
///
/// Both edges are converted independently and the extent derived from them, so
/// a window's right/bottom edge lands on exactly the canvas pixel the encoded
/// frame puts it on. Converting the extent directly would let `x` and `w`
/// round in opposite directions and drift the far edge by a pixel on fractional
/// (macOS Retina) scales.
fn to_region_relative(win: (i32, i32, u32, u32), region: Rect, canvas_scale: f64) -> Geometry {
    let (wx, wy, ww, wh) = win;
    let ox = to_canvas(region.x, canvas_scale);
    let oy = to_canvas(region.y, canvas_scale);
    let x0 = to_canvas(wx, canvas_scale) - ox;
    let y0 = to_canvas(wy, canvas_scale) - oy;
    let x1 = to_canvas(
        wx.saturating_add(ww.min(i32::MAX as u32) as i32),
        canvas_scale,
    ) - ox;
    let y1 = to_canvas(
        wy.saturating_add(wh.min(i32::MAX as u32) as i32),
        canvas_scale,
    ) - oy;
    Geometry {
        x: x0,
        y: y0,
        w: (x1 - x0).max(0) as u32,
        h: (y1 - y0).max(0) as u32,
        z: 0,
    }
}

// ---------------------------------------------------------------------------
// Tracker state (poll thread)
// ---------------------------------------------------------------------------

/// What was last written for one tracked window, so the next poll can emit
/// only what actually changed.
struct Tracked {
    id: u32,
    geometry: Geometry,
    title: String,
    app: String,
}

/// The poll thread's whole world: the wire-id assignment (persistent for the
/// session, so a window that leaves and comes back keeps its id) and the
/// windows currently inside the region.
struct Tracker {
    /// `(platform window id, pid)` → wire id. Keyed on the pair because
    /// window handles are recycled: a fresh window inheriting a dead one's
    /// handle in a *different* process must not inherit its identity.
    ///
    /// Recycling *within* one process still slips through — a closed window
    /// and its replacement in the same app can share both halves of the key,
    /// and the stream then shows one window retitling and teleporting rather
    /// than a leave followed by an enter. Geometry and titles stay correct;
    /// only entity continuity is wrong, which is why nothing downstream keys
    /// anything durable off a wire id.
    ids: HashMap<(u64, u32), u32>,
    next_id: u32,
    live: HashMap<(u64, u32), Tracked>,
    /// One-shot warning latch for [`MAX_WINDOW_IDS`].
    warned_cap: bool,
}

impl Tracker {
    fn new() -> Tracker {
        Tracker {
            ids: HashMap::new(),
            next_id: 1,
            live: HashMap::new(),
            warned_cap: false,
        }
    }

    /// The wire id for a window, assigning one on first sight. `None` once
    /// [`MAX_WINDOW_IDS`] distinct windows have been seen.
    fn wire_id(&mut self, key: (u64, u32)) -> Option<u32> {
        if let Some(&id) = self.ids.get(&key) {
            return Some(id);
        }
        if self.ids.len() >= MAX_WINDOW_IDS {
            if !self.warned_cap {
                self.warned_cap = true;
                eprintln!(
                    "Warning: the window-capture identity map is full ({MAX_WINDOW_IDS} \
                     windows); further new windows will not be tracked this session"
                );
            }
            return None;
        }
        let id = self.next_id;
        self.ids.insert(key, id);
        self.next_id += 1;
        Some(id)
    }

    /// Drops the tracked set without emitting leave rows, so the next
    /// [`apply`](Self::apply) re-announces every in-region window as new.
    /// Recovery path for a failed write (see `poll_once`); wire ids survive in
    /// `ids`, so windows keep their identity across the re-announce.
    fn forget_live(&mut self) {
        self.live.clear();
    }

    /// Folds one enumeration into the tracked set, writing a row for every
    /// change. `windows` must already be filtered to the capture region and
    /// ordered topmost-first.
    fn apply(
        &mut self,
        t: f64,
        windows: Vec<(u64, u32, Geometry, String, String)>,
        write_line: &mut impl FnMut(&str),
    ) {
        let mut seen: HashSet<(u64, u32)> = HashSet::with_capacity(windows.len());
        // Counted separately from the enumeration index: a window the identity
        // cap rejected must not consume a `z`, or the "0 = topmost, contiguous"
        // contract develops holes once the cap is hit.
        let mut z = 0u32;

        for (win_id, pid, mut geometry, title, app) in windows {
            let key = (win_id, pid);
            let Some(id) = self.wire_id(key) else {
                continue;
            };
            geometry.z = z;
            z += 1;
            seen.insert(key);

            match self.live.get_mut(&key) {
                Some(tracked) => {
                    if tracked.title != title || tracked.app != app {
                        write_info(id, &title, &app, pid, write_line);
                        tracked.title = title;
                        tracked.app = app;
                    }
                    if tracked.geometry != geometry {
                        write_row(geometry.row(t, id), write_line);
                        tracked.geometry = geometry;
                    }
                }
                None => {
                    // Entering the region: identity first, then the position
                    // it entered at, so a consumer never sees an id it has no
                    // info row for.
                    write_info(id, &title, &app, pid, write_line);
                    write_row(geometry.row(t, id), write_line);
                    self.live.insert(
                        key,
                        Tracked {
                            id,
                            geometry,
                            title,
                            app,
                        },
                    );
                }
            }
        }

        // Everything tracked last poll and absent now left the region (moved
        // out, closed, minimized, cloaked): one final all-zero row each.
        self.live.retain(|key, tracked| {
            if seen.contains(key) {
                // (HashSet, not a linear scan: this runs once per tracked
                // window on every poll.)
                return true;
            }
            write_row(Geometry::GONE.row(t, tracked.id), write_line);
            false
        });
    }
}

fn write_row(row: WindowRow, write_line: &mut impl FnMut(&str)) {
    if let Ok(line) = serde_json::to_string(&row) {
        write_line(&line);
    }
}

fn write_info(id: u32, title: &str, app: &str, pid: u32, write_line: &mut impl FnMut(&str)) {
    let row = WindowInfoRow {
        ty: "window_info",
        id,
        title: title.to_string(),
        app: app.to_string(),
        pid,
    };
    if let Ok(line) = serde_json::to_string(&row) {
        write_line(&line);
    }
}

// ---------------------------------------------------------------------------
// Shared state
// ---------------------------------------------------------------------------

/// Everything the tick callback and the poll thread share. Boxed and handed to
/// libobs as the tick callback's `param` (`tracker.rs` pattern).
struct Shared {
    /// Set by `on_output_started`, cleared by `close`: rows only flow while
    /// armed.
    armed: AtomicBool,
    /// Set once the poll loop should exit (`close`).
    stopped: AtomicBool,
    /// First video frame time sampled after arming; 0 = not started (sentinel
    /// — `obs_get_video_frame_time` is a boot-relative monotonic clock and is
    /// never 0 mid-session).
    t0_ns: AtomicU64,
    /// The most recent video frame time, published by the tick callback so
    /// the poll thread never reads libobs video state off the graphics
    /// thread. Window samples are therefore stamped at frame granularity —
    /// the only granularity the recording can show them at.
    frame_ns: AtomicU64,
    /// Poll period in milliseconds, derived from the final fps at arm time.
    poll_ms: AtomicU32,
    /// `*mut obs_output_t` as usize (pause state / pause offset reads).
    output: usize,
    /// The sidecar file, shared with `flush` on the caller's thread.
    file: Mutex<BufWriter<std::fs::File>>,
}

/// libobs tick callback — once per rendered frame on the graphics thread. Two
/// atomic stores and nothing else; all window work happens on the poll thread.
unsafe extern "C" fn tick(param: *mut c_void, _seconds: f32) {
    let shared = &*(param as *const Shared);
    if !shared.armed.load(Ordering::Acquire) {
        return;
    }
    let frame_ns = obs_sys::obs_get_video_frame_time();
    shared.frame_ns.store(frame_ns, Ordering::Release);
    let _ = shared
        .t0_ns
        .compare_exchange(0, frame_ns, Ordering::AcqRel, Ordering::Acquire);
}

/// Accumulated pause time (ns) for this recorder's output, read off the
/// track-0 video encoder for the same reason `input_capture` does: the outputs
/// here are OBS_OUTPUT_ENCODED, whose pause bookkeeping lives on the encoders,
/// so `obs_output_get_pause_offset` would always be 0.
///
/// # Safety
/// `output` must be a live output pointer, and the caller must be past the
/// arm gate — encoder swaps (`handle_configure`) are pre-start only.
unsafe fn pause_offset_ns(output: *mut obs_sys::obs_output_t) -> u64 {
    let encoder = obs_sys::obs_output_get_video_encoder(output);
    obs_sys::obs_encoder_get_pause_offset(encoder)
}

/// Samples the desktop, filters to the capture region, and writes whatever
/// changed.
fn poll_once(shared: &Shared, tracker: &mut Tracker, region: Rect, canvas_scale: f64) {
    let output = shared.output as *mut obs_sys::obs_output_t;
    if unsafe { obs_sys::obs_output_paused(output) } {
        return;
    }
    // Frame time before pause offset: a pause landing between the two reads
    // then yields an offset that is, if anything, too *large*, so `t` never
    // runs ahead of the recording. Sub-frame either way.
    let frame_ns = shared.frame_ns.load(Ordering::Acquire);
    let offset = unsafe { pause_offset_ns(output) };
    let Some(t) = map_t(frame_ns, shared.t0_ns.load(Ordering::Acquire), offset) else {
        return;
    };

    let windows: Vec<(u64, u32, Geometry, String, String)> = platform::enumerate_windows()
        .into_iter()
        .filter(|w: &WindowInfo| intersects((w.x, w.y, w.w, w.h), region))
        .map(|w| {
            (
                w.id,
                w.pid,
                to_region_relative((w.x, w.y, w.w, w.h), region, canvas_scale),
                w.title,
                w.app,
            )
        })
        .collect();

    let Ok(mut file) = shared.file.lock() else {
        return; // a poisoned lock means a previous writer panicked mid-row
    };
    // Re-checked under the lock, which `close` also takes: either this poll
    // wrote first and close's flush follows it, or close disarmed first and
    // this poll writes nothing. Without it a poll that cleared the gate just
    // before `close` could append rows after the parent was told the file was
    // final.
    if !shared.armed.load(Ordering::Acquire) {
        return;
    }
    let mut wrote = false;
    let mut failed = false;
    {
        let mut write_line = |line: &str| {
            wrote = true;
            if let Err(e) = writeln!(file, "{line}") {
                eprintln!("Warning: window-capture write failed: {e}");
                failed = true;
            }
        };
        tracker.apply(t, windows, &mut write_line);
    }
    if failed {
        // `apply` has already committed the new geometry/titles to the tracked
        // set, so a row lost to a transient write error would otherwise never
        // be re-emitted: the consumer would hold a stale rect (or an id with
        // no identity row) until that window happened to move again. Dropping
        // the tracked set makes the next poll re-announce every window in the
        // region from scratch, turning a permanent desync into a one-poll one.
        tracker.forget_live();
    }
    if wrote {
        // Keep the on-disk file current between polls: a crash then loses at
        // most the in-flight sample, matching the input-capture sidecar and
        // the crash-resilient mp4 side.
        let _ = file.flush();
    }
}

fn poll_loop(shared: Arc<Shared>, region: Rect, canvas_scale: f64) {
    let mut tracker = Tracker::new();
    while !shared.stopped.load(Ordering::Acquire) {
        if !shared.armed.load(Ordering::Acquire) {
            std::thread::sleep(IDLE_POLL);
            continue;
        }
        poll_once(&shared, &mut tracker, region, canvas_scale);
        std::thread::sleep(Duration::from_millis(
            shared.poll_ms.load(Ordering::Acquire) as u64,
        ));
    }
}

// ---------------------------------------------------------------------------
// Public handle
// ---------------------------------------------------------------------------

/// Owns the window-capture pipeline: the tick callback and the poll thread.
/// Dropping deregisters the tick callback before the state it points at goes
/// away; final durability is [`close`](Self::close), not Drop.
pub struct WindowCapture {
    /// The sidecar path, echoed in the started/stopped protocol payloads.
    path: PathBuf,
    /// The region origin in canvas pixels, for the header. Window rows are
    /// relative to it, so it appears nowhere else.
    region_origin: (i32, i32),
    /// The encoded video's exact dimensions, reported as the header region's
    /// `w`/`h` (taken from the region plan, not recomputed — the planner
    /// rounds the canvas down to an even size).
    canvas: (u32, u32),
    /// The address handed to libobs is the `Arc`'s heap allocation, so it
    /// stays valid when `self` moves — no extra boxing needed.
    shared: Arc<Shared>,
    /// Joined by `Drop`. The poll thread dereferences the raw output pointer,
    /// so it must be stopped while that output is still alive — which is why
    /// `Recorder` declares its `window_capture` field ABOVE `output` (drop
    /// order is declaration order). The join is what makes that ordering
    /// meaningful: without it `Drop` would return with the thread still
    /// running and the ordering would buy nothing.
    poll: Option<std::thread::JoinHandle<()>>,
}

impl WindowCapture {
    /// Creates the JSONL file, registers the tick callback and starts the poll
    /// thread. Rows only start flowing after
    /// [`on_output_started`](Self::on_output_started).
    pub fn new(
        path: &Path,
        region: Rect,
        canvas_scale: f64,
        canvas: (u32, u32),
        output: *mut obs_sys::obs_output_t,
    ) -> Result<WindowCapture, String> {
        let file = std::fs::File::create(path)
            .map_err(|e| format!("failed to create '{}': {e}", path.display()))?;

        let shared = Arc::new(Shared {
            armed: AtomicBool::new(false),
            stopped: AtomicBool::new(false),
            t0_ns: AtomicU64::new(0),
            frame_ns: AtomicU64::new(0),
            poll_ms: AtomicU32::new(IDLE_POLL.as_millis() as u32),
            output: output as usize,
            file: Mutex::new(BufWriter::new(file)),
        });

        let poll_shared = shared.clone();
        let poll = std::thread::Builder::new()
            .name("window-capture-poll".to_string())
            .spawn(move || poll_loop(poll_shared, region, canvas_scale))
            .map_err(|e| format!("failed to spawn the window-capture poller: {e}"))?;

        let param = Arc::as_ptr(&shared) as *mut c_void;
        unsafe { obs_sys::obs_add_tick_callback(Some(tick), param) };

        Ok(WindowCapture {
            path: path.to_path_buf(),
            region_origin: (
                to_canvas(region.x, canvas_scale),
                to_canvas(region.y, canvas_scale),
            ),
            canvas,
            shared,
            poll: Some(poll),
        })
    }

    /// The sidecar file path, for the protocol payloads.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Called from the run loop on `OutputStarted`: writes the header line
    /// (with the *final* fps — a pre-start configure may have changed the one
    /// construction saw), sets the poll cadence from it, and arms the
    /// pipeline. The header is written and flushed before the arm, so it is
    /// always line 1 no matter when the first poll lands.
    pub fn on_output_started(&self, fps_num: u32) {
        let header = HeaderLine {
            ty: "header",
            version: 1,
            region: (
                self.region_origin.0,
                self.region_origin.1,
                self.canvas.0,
                self.canvas.1,
            ),
            fps_num,
            fps_den: 1,
            platform: platform::PLATFORM_NAME,
        };
        if let Ok(mut file) = self.shared.file.lock() {
            if let Ok(line) = serde_json::to_string(&header) {
                if let Err(e) = writeln!(file, "{line}") {
                    eprintln!("Warning: window-capture write failed: {e}");
                }
            }
            let _ = file.flush();
        }

        self.shared
            .poll_ms
            .store(poll_period_ms(fps_num), Ordering::Release);
        self.shared.armed.store(true, Ordering::Release);
    }

    /// Disarms the pipeline and makes the file durable. MUST run before every
    /// `emit_stopped_recording` — exit paths skip Drop, and a truncated tail
    /// silently desyncs the editor's window overlays.
    ///
    /// Disarm-then-flush ordering matters for the same reason it does in
    /// `input_capture`: the graphics thread keeps ticking and the poll thread
    /// keeps running after `output.stop()`, so without the disarm rows would
    /// keep landing while the parent — told the file is final by
    /// `stopped_recording` — reads it. Once disarmed, at most the poll already
    /// inside `poll_once` can still write, and the flush below waits on its
    /// lock.
    pub fn close(&self) {
        self.shared.armed.store(false, Ordering::Release);
        self.shared.stopped.store(true, Ordering::Release);
        if let Ok(mut file) = self.shared.file.lock() {
            if let Err(e) = file.flush() {
                eprintln!("Warning: window-capture flush failed: {e}");
            }
        }
    }
}

/// Poll period for a recording fps: one sample per encoded frame, capped at
/// [`MAX_POLL_HZ`] and floored at 1 ms so a degenerate fps cannot spin.
fn poll_period_ms(fps: u32) -> u32 {
    let hz = fps.clamp(1, MAX_POLL_HZ);
    (1000 / hz).max(1)
}

impl Drop for WindowCapture {
    fn drop(&mut self) {
        // Deregister first, so the graphics thread stops touching the shared
        // state the callback points at.
        let param = Arc::as_ptr(&self.shared) as *mut c_void;
        unsafe { obs_sys::obs_remove_tick_callback(Some(tick), param) };

        // Then stop AND JOIN the poller, so this returns only once nothing is
        // still dereferencing the output pointer. Paired with `Recorder`
        // declaring its sidecar fields above `output`, that guarantees the
        // thread is gone before the output it reads is released — including
        // on a panic unwind, which is the only path that reaches Drop at all
        // (every ordinary exit goes through `exit_process`). Bounded by one
        // poll period: ≤ 33 ms at 30 fps, 5 ms while disarmed.
        self.shared.stopped.store(true, Ordering::Release);
        if let Some(handle) = self.poll.take() {
            let _ = handle.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const REGION: Rect = Rect {
        x: 100,
        y: 100,
        w: 800,
        h: 600,
    };

    // -- intersection --------------------------------------------------------

    #[test]
    fn intersection_requires_a_shared_pixel() {
        // Fully inside.
        assert!(intersects((200, 200, 100, 100), REGION));
        // Straddling each edge.
        assert!(intersects((50, 200, 100, 100), REGION));
        assert!(intersects((850, 200, 100, 100), REGION));
        assert!(intersects((200, 50, 100, 100), REGION));
        assert!(intersects((200, 650, 100, 100), REGION));
        // Enclosing the whole region.
        assert!(intersects((0, 0, 1920, 1080), REGION));
    }

    #[test]
    fn edge_contact_does_not_intersect() {
        // Right edge of the window exactly on the region's left edge.
        assert!(!intersects((0, 200, 100, 100), REGION));
        // Left edge exactly on the region's right edge (100 + 800 = 900).
        assert!(!intersects((900, 200, 100, 100), REGION));
        assert!(!intersects((200, 0, 100, 100), REGION));
        assert!(!intersects((200, 700, 100, 100), REGION));
    }

    #[test]
    fn degenerate_rects_never_intersect() {
        assert!(!intersects((200, 200, 0, 100), REGION));
        assert!(!intersects((200, 200, 100, 0), REGION));
    }

    #[test]
    fn intersection_handles_negative_and_extreme_coordinates() {
        // Displays left of / above the primary.
        let region = Rect {
            x: -1920,
            y: -100,
            w: 1920,
            h: 1080,
        };
        assert!(intersects((-500, 0, 400, 400), region));
        assert!(!intersects((100, 0, 400, 400), region));
        // A rect whose far edge overflows i32 must not wrap into a false
        // negative (the math is done in i64).
        assert!(!intersects((i32::MAX - 10, -100, u32::MAX, 1080), region));
        assert!(intersects((-2000, -200, u32::MAX, 4000), region));
    }

    // -- coordinate conversion ----------------------------------------------

    #[test]
    fn geometry_is_relative_to_the_region_origin() {
        let g = to_region_relative((150, 250, 400, 300), REGION, 1.0);
        assert_eq!((g.x, g.y, g.w, g.h), (50, 150, 400, 300));
    }

    #[test]
    fn a_window_left_of_the_region_reports_negative_coordinates() {
        // Not clipped: the editor needs the overhang to place the visible part.
        let g = to_region_relative((0, 0, 200, 200), REGION, 1.0);
        assert_eq!((g.x, g.y, g.w, g.h), (-100, -100, 200, 200));
    }

    #[test]
    fn retina_scaling_keeps_both_edges_on_their_canvas_pixel() {
        // macOS: points -> canvas px at 2x.
        let g = to_region_relative((150, 250, 400, 300), REGION, 2.0);
        assert_eq!((g.x, g.y, g.w, g.h), (100, 300, 800, 600));

        // A fractional scale is where converting the extent directly would
        // drift: x rounds up, x+w rounds down, and the far edge slips a pixel.
        // Deriving w from both converted edges keeps them exact.
        let region = Rect {
            x: 0,
            y: 0,
            w: 1000,
            h: 1000,
        };
        let g = to_region_relative((3, 3, 5, 5), region, 1.5);
        assert_eq!(g.x, 5); // 3 * 1.5 = 4.5 -> 5
        assert_eq!(g.x + g.w as i32, 12); // 8 * 1.5 = 12
        assert_eq!(g.w, 7);
    }

    // -- row serialization (wire contract) -----------------------------------

    #[test]
    fn header_line_matches_the_contract() {
        let h = HeaderLine {
            ty: "header",
            version: 1,
            region: (-100, 50, 2560, 1440),
            fps_num: 30,
            fps_den: 1,
            platform: "windows",
        };
        assert_eq!(
            serde_json::to_string(&h).unwrap(),
            r#"{"type":"header","version":1,"region":[-100,50,2560,1440],"fps_num":30,"fps_den":1,"platform":"windows"}"#
        );
    }

    #[test]
    fn window_row_matches_the_contract() {
        let row = Geometry {
            x: -20,
            y: 40,
            w: 800,
            h: 600,
            z: 2,
        }
        .row(1234.567, 7);
        assert_eq!(
            serde_json::to_string(&row).unwrap(),
            r#"{"type":"window","t":1234.567,"id":7,"x":-20,"y":40,"w":800,"h":600,"z":2}"#
        );
    }

    #[test]
    fn info_row_matches_the_contract() {
        let mut lines = Vec::new();
        write_info(3, "README.md - Code", "Code.exe", 4212, &mut |l: &str| {
            lines.push(l.to_string())
        });
        assert_eq!(
            lines,
            vec![
                r#"{"type":"window_info","id":3,"title":"README.md - Code","app":"Code.exe","pid":4212}"#
            ]
        );
    }

    // -- change detection ----------------------------------------------------

    fn geometry(x: i32, y: i32) -> Geometry {
        Geometry {
            x,
            y,
            w: 100,
            h: 100,
            z: 0,
        }
    }

    fn sample(
        tracker: &mut Tracker,
        t: f64,
        windows: &[(u64, u32, Geometry, &str, &str)],
    ) -> Vec<String> {
        let mut lines = Vec::new();
        let owned = windows
            .iter()
            .map(|(id, pid, g, title, app)| (*id, *pid, *g, title.to_string(), app.to_string()))
            .collect();
        tracker.apply(t, owned, &mut |l: &str| lines.push(l.to_string()));
        lines
    }

    #[test]
    fn a_new_window_emits_its_identity_before_its_position() {
        let mut tracker = Tracker::new();
        let lines = sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        assert_eq!(lines.len(), 2);
        assert!(lines[0].starts_with(r#"{"type":"window_info","id":1,"title":"A""#));
        assert_eq!(
            lines[1],
            r#"{"type":"window","t":0.0,"id":1,"x":10,"y":20,"w":100,"h":100,"z":0}"#
        );
    }

    #[test]
    fn an_unchanged_window_emits_nothing() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        let lines = sample(
            &mut tracker,
            33.3,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        assert!(lines.is_empty(), "{lines:?}");
    }

    #[test]
    fn a_moved_window_emits_only_its_new_position() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        let lines = sample(
            &mut tracker,
            33.3,
            &[(0xA, 42, geometry(11, 20), "A", "a.exe")],
        );
        assert_eq!(
            lines,
            vec![r#"{"type":"window","t":33.3,"id":1,"x":11,"y":20,"w":100,"h":100,"z":0}"#]
        );
    }

    #[test]
    fn a_retitled_window_re_emits_its_identity_under_the_same_id() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        let lines = sample(
            &mut tracker,
            33.3,
            &[(0xA, 42, geometry(10, 20), "B", "a.exe")],
        );
        assert_eq!(lines.len(), 1);
        assert!(lines[0].starts_with(r#"{"type":"window_info","id":1,"title":"B""#));
    }

    #[test]
    fn a_window_leaving_the_region_emits_the_zero_rect() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        let lines = sample(&mut tracker, 99.0, &[]);
        assert_eq!(
            lines,
            vec![r#"{"type":"window","t":99.0,"id":1,"x":0,"y":0,"w":0,"h":0,"z":0}"#]
        );
        // ...exactly once: it is no longer tracked.
        assert!(sample(&mut tracker, 132.0, &[]).is_empty());
    }

    #[test]
    fn a_window_re_entering_the_region_keeps_its_id() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        sample(&mut tracker, 33.0, &[]);
        let lines = sample(
            &mut tracker,
            66.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains(r#""id":1"#));
        assert!(lines[1].contains(r#""id":1"#));
    }

    #[test]
    fn a_recycled_handle_in_another_process_gets_a_fresh_id() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        let lines = sample(
            &mut tracker,
            33.0,
            &[(0xA, 43, geometry(10, 20), "B", "b.exe")],
        );
        // The old window left and a different one arrived.
        assert!(lines
            .iter()
            .any(|l| l.contains(r#""id":1,"x":0,"y":0,"w":0,"h":0"#)));
        assert!(lines
            .iter()
            .any(|l| l.contains(r#""type":"window_info","id":2"#)));
    }

    #[test]
    fn z_order_is_the_index_among_tracked_windows() {
        let mut tracker = Tracker::new();
        let lines = sample(
            &mut tracker,
            0.0,
            &[
                (0xA, 42, geometry(0, 0), "A", "a.exe"),
                (0xB, 43, geometry(0, 0), "B", "b.exe"),
            ],
        );
        assert!(lines[1].contains(r#""z":0"#));
        assert!(lines[3].contains(r#""z":1"#));

        // Raising B to the front re-emits both rows with swapped z.
        let lines = sample(
            &mut tracker,
            33.0,
            &[
                (0xB, 43, geometry(0, 0), "B", "b.exe"),
                (0xA, 42, geometry(0, 0), "A", "a.exe"),
            ],
        );
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains(r#""id":2"#) && lines[0].contains(r#""z":0"#));
        assert!(lines[1].contains(r#""id":1"#) && lines[1].contains(r#""z":1"#));
    }

    #[test]
    fn forgetting_the_tracked_set_re_announces_without_leave_rows() {
        let mut tracker = Tracker::new();
        sample(
            &mut tracker,
            0.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );

        // The write-failure recovery path: no leave row, and the next sample
        // re-emits identity + position under the SAME wire id.
        tracker.forget_live();
        let lines = sample(
            &mut tracker,
            33.0,
            &[(0xA, 42, geometry(10, 20), "A", "a.exe")],
        );
        assert_eq!(lines.len(), 2);
        assert!(lines[0].starts_with(r#"{"type":"window_info","id":1,"title":"A""#));
        assert_eq!(
            lines[1],
            r#"{"type":"window","t":33.0,"id":1,"x":10,"y":20,"w":100,"h":100,"z":0}"#
        );
    }

    #[test]
    fn the_identity_map_is_capped() {
        let mut tracker = Tracker::new();
        for i in 0..MAX_WINDOW_IDS as u64 {
            assert!(tracker.wire_id((i, 1)).is_some());
        }
        // A brand new window past the cap is dropped...
        assert_eq!(tracker.wire_id((999_999, 1)), None);
        // ...but an already-known one keeps working.
        assert_eq!(tracker.wire_id((0, 1)), Some(1));
    }

    // -- poll cadence --------------------------------------------------------

    #[test]
    fn poll_period_follows_the_fps_up_to_the_cap() {
        assert_eq!(poll_period_ms(30), 33);
        assert_eq!(poll_period_ms(60), 16);
        // Capped: a 120/240 fps recording still polls at 60 Hz.
        assert_eq!(poll_period_ms(120), 16);
        assert_eq!(poll_period_ms(240), 16);
        // A degenerate fps must not produce a zero-length sleep.
        assert_eq!(poll_period_ms(0), 1000);
    }
}
