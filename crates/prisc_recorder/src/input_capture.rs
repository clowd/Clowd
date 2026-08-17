//! `--input-capture`: a JSONL sidecar recording cursor position/shape, mouse
//! buttons and keys per rendered frame, plus sub-frame-precise input edges
//! (DESIGN §1 — the file format is a wire contract consumed by the Clowd
//! editor).
//!
//! Threading: a libobs tick callback (graphics thread, `tracker.rs` pattern)
//! samples the frame time, cursor and hook-state snapshot once per rendered
//! frame; the hook thread delivers edge events. Both only *send* on a channel
//! — all serialization and file I/O happen on a dedicated writer thread (no
//! blocking I/O on the graphics thread, DESIGN §2). The tick's cursor sample
//! is also handed to an optional [`CursorObserver`] (the cursor track's
//! per-frame recentering), so the recorded frame row and the 512 box always
//! agree on where the cursor was.
//!
//! Timebase: `t` is milliseconds relative to the first frame time sampled
//! after `OutputStarted` (`t0`), minus the accumulated pause offset — the
//! same clock and pause adjustment the output applies to its PTS. The offset
//! is read from the track-0 *video encoder* (`obs_encoder_get_pause_offset`),
//! NOT `obs_output_get_pause_offset`: this recorder's outputs (ffmpeg_muxer /
//! mp4_output) are OBS_OUTPUT_ENCODED, so pause bookkeeping lives on the
//! encoders (`obs_encoded_output_pause`) and the output-level offset stays 0
//! forever. Frame rows and events are dropped while the output is paused
//! (encoder-level pause: ticks and hooks keep firing, so the gate is
//! explicit).
//!
//! Lifetime: exit paths skip Drop (`platform::exit_process`), so the recorder
//! calls [`InputCapture::close`] explicitly before *every*
//! `emit_stopped_recording` — finish, start-failure and pre-start quit. Close
//! disarms the pipeline (the graphics thread keeps ticking and the hooks keep
//! firing after `output.stop()`, so without disarming rows would keep landing
//! after the final flush) and then flushes.

use std::ffi::c_void;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Mutex};
use std::time::Duration;

use serde::Serialize;

use crate::input_hook::{InputHook, RawEvent, RawEventKind};
use crate::platform::{self, CursorState, MonitorInfo};
use crate::region::Rect;

/// Per-frame consumer of the tick's cursor sample — the cursor track's
/// recentering hook. Receiving the very same sample the frame row records is
/// the DESIGN §1 consistency contract.
pub type CursorObserver = Box<dyn FnMut(&CursorState) + Send>;

/// Bound on waiting for the writer thread to acknowledge a flush — a wedged
/// disk must not stall the recorder's exit path indefinitely.
const FLUSH_ACK_TIMEOUT: Duration = Duration::from_secs(5);

// ---------------------------------------------------------------------------
// Wire rows (DESIGN §1 — field names and order are the contract)
// ---------------------------------------------------------------------------

#[derive(Serialize)]
struct HeaderLine {
    #[serde(rename = "type")]
    ty: &'static str,
    version: u32,
    /// Recording region `[x,y,w,h]`, physical px, virtual-desktop coords.
    region: (i32, i32, u32, u32),
    fps_num: u32,
    fps_den: u32,
    platform: &'static str,
    monitors: Vec<MonitorEntry>,
}

#[derive(Serialize, Clone)]
struct MonitorEntry {
    x: i32,
    y: i32,
    w: u32,
    h: u32,
    /// DPI zoom (Windows: dpi/96; macOS: Retina backing scale) — the editor's
    /// base factor for themed cursor sizing.
    scale: f64,
}

#[derive(Serialize)]
struct FrameRow {
    #[serde(rename = "type")]
    ty: &'static str,
    t: f64,
    x: i32,
    y: i32,
    b: u32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    k: Vec<u32>,
    c: &'static str,
}

#[derive(Serialize)]
struct EventRow {
    #[serde(rename = "type")]
    ty: &'static str,
    t: f64,
    kind: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    vk: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    ch: Option<char>,
    #[serde(skip_serializing_if = "Option::is_none")]
    btn: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    x: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    y: Option<i32>,
}

fn event_row(t: f64, kind: RawEventKind) -> EventRow {
    let mut row = EventRow {
        ty: "event",
        t,
        kind: "",
        vk: None,
        ch: None,
        btn: None,
        x: None,
        y: None,
    };
    match kind {
        RawEventKind::KeyDown { vk, ch } => {
            row.kind = "kd";
            row.vk = Some(vk);
            row.ch = ch;
        }
        RawEventKind::KeyUp { vk } => {
            row.kind = "ku";
            row.vk = Some(vk);
        }
        RawEventKind::MouseDown { btn, x, y } => {
            row.kind = "md";
            row.btn = Some(btn);
            row.x = Some(x);
            row.y = Some(y);
        }
        RawEventKind::MouseUp { btn, x, y } => {
            row.kind = "mu";
            row.btn = Some(btn);
            row.x = Some(x);
            row.y = Some(y);
        }
    }
    row
}

// ---------------------------------------------------------------------------
// Timestamp mapping (pure)
// ---------------------------------------------------------------------------

/// Maps a monotonic timestamp onto the file timebase: milliseconds since
/// `t0_ns`, minus the accumulated pause offset, rounded to microsecond
/// precision (sub-frame accuracy with compact serialization). `None` when the
/// timestamp precedes the recording start or falls inside time the pause
/// offset has swallowed (an edge raced the pause boundary) — such rows are
/// dropped, not clamped.
pub fn map_t(event_ns: u64, t0_ns: u64, pause_offset_ns: u64) -> Option<f64> {
    if t0_ns == 0 {
        return None; // recording has not started
    }
    let since_start = event_ns.checked_sub(t0_ns)?;
    let adjusted = since_start.checked_sub(pause_offset_ns)?;
    Some((adjusted as f64 / 1_000.0).round() / 1_000.0)
}

/// Accumulated pause time (ns) for this recorder's output. Read off the
/// track-0 video encoder: the outputs used here are OBS_OUTPUT_ENCODED, whose
/// pause path (`obs_encoded_output_pause`) updates only the per-encoder pause
/// structs — `obs_output_get_pause_offset` would always return 0. All of the
/// output's encoders pause at the same `closest_v_ts`, so track 0 speaks for
/// the recording. NULL-safe (`obs_encoder_get_pause_offset(NULL)` is 0).
///
/// # Safety
/// `output` must be a live output pointer. Callers must not race a pre-start
/// encoder swap (`handle_configure`): only call once the recording has
/// started (armed tick / nonzero `t0`) — encoder swaps are pre-start only.
unsafe fn pause_offset_ns(output: *mut obs_sys::obs_output_t) -> u64 {
    let encoder = obs_sys::obs_output_get_video_encoder(output);
    obs_sys::obs_encoder_get_pause_offset(encoder)
}

// ---------------------------------------------------------------------------
// Writer thread
// ---------------------------------------------------------------------------

enum WriterMsg {
    Header(HeaderLine),
    Frame(FrameRow),
    Event(RawEvent),
    Flush(mpsc::Sender<()>),
}

/// Serializes rows to the JSONL file. Event `t` mapping happens here (the
/// hook thread must not query the output): `t0` via the shared atomic, pause
/// state/offset read straight off the output pointer — sound for the same
/// reason as the status thread's counter reads (the process always exits via
/// `exit_process` while the output is alive). Events are gated on `armed`
/// (set on `OutputStarted`, cleared by `close`), so nothing lands after the
/// final flush and the writer never touches the output's encoder while a
/// pre-start configure may still be swapping it.
fn writer_loop(
    file: std::fs::File,
    rx: mpsc::Receiver<WriterMsg>,
    armed: std::sync::Arc<AtomicBool>,
    t0_ns: std::sync::Arc<AtomicU64>,
    output_addr: usize,
) {
    let output = output_addr as *mut obs_sys::obs_output_t;
    let mut w = BufWriter::new(file);
    loop {
        // Block for the next message, then drain the backlog and flush once
        // the channel runs dry: at most one small flush per frame, and the
        // on-disk file stays current — a crash loses at most the in-flight
        // frame (the mp4 side is crash-resilient, the sidecar should be too).
        let msg = match rx.recv() {
            Ok(m) => m,
            Err(_) => break,
        };
        handle_msg(&mut w, msg, &armed, &t0_ns, output);
        loop {
            match rx.try_recv() {
                Ok(m) => handle_msg(&mut w, m, &armed, &t0_ns, output),
                Err(mpsc::TryRecvError::Empty) => {
                    let _ = w.flush();
                    break;
                }
                Err(mpsc::TryRecvError::Disconnected) => {
                    let _ = w.flush();
                    return;
                }
            }
        }
    }
    // All senders gone (never in a normal session — exit paths flush and then
    // exit the process); make the tail durable anyway.
    let _ = w.flush();
}

fn handle_msg(
    w: &mut BufWriter<std::fs::File>,
    msg: WriterMsg,
    armed: &AtomicBool,
    t0_ns: &AtomicU64,
    output: *mut obs_sys::obs_output_t,
) {
    let mut write_line = |line: &str| {
        if let Err(e) = writeln!(w, "{line}") {
            eprintln!("Warning: input-capture write failed: {e}");
        }
    };
    match msg {
        WriterMsg::Header(h) => {
            if let Ok(line) = serde_json::to_string(&h) {
                write_line(&line);
            }
        }
        WriterMsg::Frame(row) => {
            if let Ok(line) = serde_json::to_string(&row) {
                write_line(&line);
            }
        }
        WriterMsg::Event(ev) => {
            // Dropped before start and after close (the hooks fire the whole
            // time), and while paused (frame rows are gated on the graphics
            // thread; events are gated here). The armed gate also keeps the
            // encoder-pointer read below away from pre-start configure swaps.
            if !armed.load(Ordering::Acquire) || unsafe { obs_sys::obs_output_paused(output) } {
                return;
            }
            let offset = unsafe { pause_offset_ns(output) };
            if let Some(t) = map_t(ev.t_ns, t0_ns.load(Ordering::Acquire), offset) {
                if let Ok(line) = serde_json::to_string(&event_row(t, ev.kind)) {
                    write_line(&line);
                }
            }
        }
        WriterMsg::Flush(ack) => {
            if let Err(e) = w.flush() {
                eprintln!("Warning: input-capture flush failed: {e}");
            }
            let _ = ack.send(());
        }
    }
}

// ---------------------------------------------------------------------------
// Tick callback (graphics thread)
// ---------------------------------------------------------------------------

/// Everything the tick callback touches. Boxed and handed to libobs as the
/// callback's `param` (`tracker.rs` pattern); the atomics are shared with the
/// run loop (`armed`) and the writer thread (`t0_ns`).
struct TickState {
    /// Set by `on_output_started`, cleared by `close`: rows only flow while
    /// armed. Shared with the writer thread, which gates event rows on it.
    armed: std::sync::Arc<AtomicBool>,
    /// First frame time sampled after arming; 0 = not started (sentinel —
    /// `obs_get_video_frame_time` is a boot-relative monotonic clock, never 0
    /// mid-session).
    t0_ns: std::sync::Arc<AtomicU64>,
    /// `*mut obs_output_t` as usize (pause state/offset reads).
    output: usize,
    tx: mpsc::Sender<WriterMsg>,
    /// The hooks live exactly as long as the tick callback that samples them.
    hook: InputHook,
    /// Cursor-track recentering hook, fed the tick's cursor sample. Swapped
    /// by the recorder around `obs_reset_video` teardown/rebuild (contention
    /// is pre-start only, so the graphics thread never meaningfully blocks).
    observer: Mutex<Option<CursorObserver>>,
}

/// libobs tick callback — once per rendered frame on the graphics thread.
/// Only sampling, item repositioning (via the observer) and a channel send;
/// never file I/O.
unsafe extern "C" fn tick(param: *mut c_void, _seconds: f32) {
    let state = &*(param as *const TickState);

    // ONE cursor sample serves both the cursor-track recentering and the
    // frame row below — the consistency contract in DESIGN §1. The observer
    // runs unconditionally (before arming and while paused too): frames keep
    // rendering then, and the box should track the cursor from frame one.
    let cursor = platform::get_cursor_state();
    if let Ok(mut observer) = state.observer.lock() {
        if let Some(f) = observer.as_mut() {
            f(&cursor);
        }
    }

    if !state.armed.load(Ordering::Acquire) {
        return;
    }
    let frame_ns = obs_sys::obs_get_video_frame_time();
    if state.t0_ns.load(Ordering::Acquire) == 0 {
        // First tick after OutputStarted: this frame is t = 0.
        state.t0_ns.store(frame_ns, Ordering::Release);
    }

    let output = state.output as *mut obs_sys::obs_output_t;
    if obs_sys::obs_output_paused(output) {
        return;
    }
    let offset = pause_offset_ns(output);
    let Some(t) = map_t(frame_ns, state.t0_ns.load(Ordering::Acquire), offset) else {
        return;
    };

    let (buttons, keys) = state.hook.snapshot();
    let _ = state.tx.send(WriterMsg::Frame(FrameRow {
        ty: "frame",
        t,
        x: cursor.x,
        y: cursor.y,
        b: buttons,
        k: keys,
        c: cursor.kind.as_str(),
    }));
}

// ---------------------------------------------------------------------------
// Public handle
// ---------------------------------------------------------------------------

/// Owns the input-capture pipeline: hooks, tick callback and writer thread.
/// Dropping deregisters the tick callback before the state it points at goes
/// away; final durability is [`flush`](Self::flush), not Drop.
pub struct InputCapture {
    tx: mpsc::Sender<WriterMsg>,
    /// The sidecar path, echoed in the started/stopped protocol payloads.
    path: PathBuf,
    /// Header data captured at construction; the fps joins at
    /// `on_output_started` (a pre-start `configure` may still change it).
    region: Rect,
    monitors: Vec<MonitorEntry>,
    /// Boxed so the address handed to libobs stays valid when `self` moves.
    state: Box<TickState>,
}

impl InputCapture {
    /// Creates the JSONL file, installs the input hooks and registers the
    /// tick callback. Rows only start flowing after
    /// [`on_output_started`](Self::on_output_started).
    pub fn new(
        path: &Path,
        region: Rect,
        monitors: &[MonitorInfo],
        output: *mut obs_sys::obs_output_t,
    ) -> Result<InputCapture, String> {
        // Prime the cursor classifier from the calling (main) thread. The first
        // sample is the expensive one — on macOS it bootstraps AppKit and hashes
        // the stock cursor set (~30 ms) — and the tick callback would otherwise
        // pay it on the graphics thread during the first rendered frame.
        let _ = platform::get_cursor_state();

        let file = std::fs::File::create(path)
            .map_err(|e| format!("failed to create '{}': {e}", path.display()))?;

        let (tx, rx) = mpsc::channel::<WriterMsg>();
        let armed = std::sync::Arc::new(AtomicBool::new(false));
        let t0_ns = std::sync::Arc::new(AtomicU64::new(0));

        let writer_armed = armed.clone();
        let writer_t0 = t0_ns.clone();
        let output_addr = output as usize;
        std::thread::Builder::new()
            .name("input-capture-writer".to_string())
            .spawn(move || writer_loop(file, rx, writer_armed, writer_t0, output_addr))
            .map_err(|e| format!("failed to spawn the input-capture writer: {e}"))?;

        // The hook thread forwards edges into the writer channel; timestamp
        // mapping and the pause gate run on the writer thread.
        let hook_tx = tx.clone();
        let hook = InputHook::start(Box::new(move |ev| {
            let _ = hook_tx.send(WriterMsg::Event(ev));
        }))
        .map_err(|e| format!("failed to install the input hooks: {e}"))?;

        let monitors = monitors
            .iter()
            .map(|m| MonitorEntry {
                x: m.x,
                y: m.y,
                w: m.width,
                h: m.height,
                scale: platform::monitor_display_scale(m),
            })
            .collect();

        let state = Box::new(TickState {
            armed,
            t0_ns,
            output: output_addr,
            tx: tx.clone(),
            hook,
            observer: Mutex::new(None),
        });
        let param = &*state as *const TickState as *mut c_void;
        unsafe { obs_sys::obs_add_tick_callback(Some(tick), param) };

        Ok(InputCapture {
            tx,
            path: path.to_path_buf(),
            region,
            monitors,
            state,
        })
    }

    /// The sidecar file path, for the protocol payloads.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Installs (or with `None` clears) the per-frame cursor observer — the
    /// cursor track's recentering hook. The recorder MUST clear it before
    /// tearing the cursor chain down and re-install a fresh one after any
    /// rebuild (`obs_reset_video` paths).
    pub fn set_cursor_observer(&self, observer: Option<CursorObserver>) {
        *self.state.observer.lock().unwrap() = observer;
    }

    /// Called from the run loop on `OutputStarted`: writes the header line
    /// (with the *final* fps — a pre-start configure may have changed the one
    /// construction saw) and arms the tick callback, whose next tick defines
    /// `t0`.
    pub fn on_output_started(&self, fps_num: u32) {
        let _ = self.tx.send(WriterMsg::Header(HeaderLine {
            ty: "header",
            version: 1,
            region: (self.region.x, self.region.y, self.region.w, self.region.h),
            fps_num,
            fps_den: 1,
            platform: platform::PLATFORM_NAME,
            monitors: self.monitors.clone(),
        }));
        // Header queued before arming, so it is always line 1.
        self.state.armed.store(true, Ordering::Release);
    }

    /// Disarms the pipeline and makes the file durable. MUST run before every
    /// `emit_stopped_recording` — exit paths skip Drop, and losing the tail
    /// of the sidecar silently desyncs the editor overlays.
    ///
    /// Disarm-then-flush ordering matters: the graphics thread keeps ticking
    /// and the hooks keep firing after `output.stop()`, so without the
    /// disarm, rows would keep flowing while the parent (told the file is
    /// final by `stopped_recording`) reads it — and `exit_process` could kill
    /// the writer mid-write, leaving a torn trailing line. After the disarm,
    /// every row already in the channel is drained and flushed by the ack'd
    /// flush (channel FIFO), and no new row can follow it onto disk.
    pub fn close(&self) {
        self.state.armed.store(false, Ordering::Release);
        self.flush();
    }

    /// Drains the writer channel and flushes the file, bounded by
    /// [`FLUSH_ACK_TIMEOUT`].
    fn flush(&self) {
        let (ack_tx, ack_rx) = mpsc::channel();
        if self.tx.send(WriterMsg::Flush(ack_tx)).is_err() {
            return; // writer already gone; nothing more to make durable
        }
        if ack_rx.recv_timeout(FLUSH_ACK_TIMEOUT).is_err() {
            eprintln!(
                "Warning: the input-capture file did not flush within {FLUSH_ACK_TIMEOUT:?}; \
                 it may be incomplete"
            );
        }
    }
}

impl Drop for InputCapture {
    fn drop(&mut self) {
        // Deregister before the Box (and the hooks inside it) goes away.
        let param = &*self.state as *const TickState as *mut c_void;
        unsafe { obs_sys::obs_remove_tick_callback(Some(tick), param) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input_hook::{BTN_LEFT, BTN_RIGHT};

    // -- map_t ---------------------------------------------------------------

    #[test]
    fn map_t_is_ms_relative_to_t0() {
        assert_eq!(map_t(1_000_000_000, 1_000_000_000, 0), Some(0.0));
        assert_eq!(map_t(1_500_000_000, 1_000_000_000, 0), Some(500.0));
        // Sub-millisecond precision survives (µs granularity).
        assert_eq!(map_t(1_000_567_000, 1_000_000_000, 0), Some(0.567));
    }

    #[test]
    fn map_t_subtracts_the_pause_offset() {
        // 5 s of wall time, of which 2 s were paused -> t = 3000 ms.
        assert_eq!(
            map_t(6_000_000_000, 1_000_000_000, 2_000_000_000),
            Some(3000.0)
        );
    }

    #[test]
    fn map_t_drops_rows_outside_the_recording() {
        // Before start.
        assert_eq!(map_t(900_000_000, 1_000_000_000, 0), None);
        // Not started at all (t0 sentinel).
        assert_eq!(map_t(900_000_000, 0, 0), None);
        // An edge raced the pause boundary: the offset already exceeds its
        // distance from t0.
        assert_eq!(map_t(1_100_000_000, 1_000_000_000, 200_000_000), None);
    }

    #[test]
    fn map_t_rounds_to_microsecond_precision() {
        // 123456789 ns = 123.456789 ms -> 123.457 (compact, sub-frame exact).
        assert_eq!(map_t(1_123_456_789, 1_000_000_000, 0), Some(123.457));
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
            monitors: vec![MonitorEntry {
                x: 0,
                y: 0,
                w: 2560,
                h: 1440,
                scale: 1.5,
            }],
        };
        assert_eq!(
            serde_json::to_string(&h).unwrap(),
            r#"{"type":"header","version":1,"region":[-100,50,2560,1440],"fps_num":30,"fps_den":1,"platform":"windows","monitors":[{"x":0,"y":0,"w":2560,"h":1440,"scale":1.5}]}"#
        );
    }

    #[test]
    fn frame_row_matches_the_contract() {
        let row = FrameRow {
            ty: "frame",
            t: 1234.567,
            x: 100,
            y: 200,
            b: BTN_LEFT,
            k: vec![17, 75],
            c: "arrow",
        };
        assert_eq!(
            serde_json::to_string(&row).unwrap(),
            r#"{"type":"frame","t":1234.567,"x":100,"y":200,"b":1,"k":[17,75],"c":"arrow"}"#
        );
    }

    #[test]
    fn frame_row_omits_an_empty_key_list() {
        let row = FrameRow {
            ty: "frame",
            t: 0.0,
            x: -5,
            y: 7,
            b: 0,
            k: vec![],
            c: "hidden",
        };
        assert_eq!(
            serde_json::to_string(&row).unwrap(),
            r#"{"type":"frame","t":0.0,"x":-5,"y":7,"b":0,"c":"hidden"}"#
        );
    }

    #[test]
    fn event_rows_match_the_contract() {
        let kd = event_row(
            1234.111,
            RawEventKind::KeyDown {
                vk: 75,
                ch: Some('k'),
            },
        );
        assert_eq!(
            serde_json::to_string(&kd).unwrap(),
            r#"{"type":"event","t":1234.111,"kind":"kd","vk":75,"ch":"k"}"#
        );

        // No translated char -> `ch` omitted, not null.
        let kd_none = event_row(1.0, RawEventKind::KeyDown { vk: 17, ch: None });
        assert_eq!(
            serde_json::to_string(&kd_none).unwrap(),
            r#"{"type":"event","t":1.0,"kind":"kd","vk":17}"#
        );

        let ku = event_row(1234.222, RawEventKind::KeyUp { vk: 75 });
        assert_eq!(
            serde_json::to_string(&ku).unwrap(),
            r#"{"type":"event","t":1234.222,"kind":"ku","vk":75}"#
        );

        let md = event_row(
            1300.0,
            RawEventKind::MouseDown {
                btn: BTN_LEFT,
                x: 100,
                y: 200,
            },
        );
        assert_eq!(
            serde_json::to_string(&md).unwrap(),
            r#"{"type":"event","t":1300.0,"kind":"md","btn":1,"x":100,"y":200}"#
        );

        let mu = event_row(
            1305.5,
            RawEventKind::MouseUp {
                btn: BTN_RIGHT,
                x: -10,
                y: 0,
            },
        );
        assert_eq!(
            serde_json::to_string(&mu).unwrap(),
            r#"{"type":"event","t":1305.5,"kind":"mu","btn":2,"x":-10,"y":0}"#
        );
    }
}
