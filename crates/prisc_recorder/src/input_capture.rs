//! `--input-capture`: a JSONL sidecar recording cursor position/shape, mouse
//! buttons and keys per rendered frame, plus sub-frame-precise input edges
//! (DESIGN §1 — the file format is a wire contract consumed by the Clowd
//! editor).
//!
//! Threading: a libobs tick callback (graphics thread, `tracker.rs` pattern)
//! samples the frame time, cursor and hook-state snapshot once per rendered
//! frame; the hook thread delivers edge events. Both only *send* on a channel
//! — all serialization and file I/O happen on a dedicated writer thread (no
//! blocking I/O on the graphics thread, DESIGN §2). The tick also rasterizes
//! the live cursor (`platform::take_cursor_sprite`, cheap steady state) and
//! sends the resulting [`SpriteEvent`] alongside the frame row; PNG/base64
//! encoding, content-hash dedupe and `cursor_image` row emission all live on
//! the writer thread, which guarantees a sprite's row always precedes the
//! first frame row referencing it. A cursor_image row may precede the first
//! frame row: a skipped pre-t0 frame can supply the sprite that a surviving
//! frame's Unchanged event relies on.
//!
//! Timebase: `t` is milliseconds relative to the shared packet-anchored
//! recording origin (`t0`), minus the pause offset accumulated since t0. Startup
//! observations wait on the writer until the origin is known. The offset
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

use std::collections::{HashMap, VecDeque};
use std::ffi::c_void;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::time::Duration;

use serde::Serialize;

use crate::cursor_sprite::{self, SpriteEvent};
use crate::frame_clock::FrameClock;
use crate::input_hook::{InputHook, RawEvent, RawEventKind};
use crate::platform::{self, MonitorInfo};
use crate::region::Rect;

/// Bound on waiting for the writer thread to acknowledge a flush — a wedged
/// disk must not stall the recorder's exit path indefinitely.
const FLUSH_ACK_TIMEOUT: Duration = Duration::from_secs(5);
const STARTUP_BUFFER_CAP: usize = 1024;

// ---------------------------------------------------------------------------
// Wire rows (DESIGN §1 — field names and order are the contract)
// ---------------------------------------------------------------------------

#[derive(Serialize)]
struct HeaderLine {
    #[serde(rename = "type")]
    ty: &'static str,
    version: u32,
    /// Recording region `[x,y,w,h]` in canvas pixels — `w`/`h` are the encoded
    /// video's exact dimensions (see [`to_canvas`]).
    region: (i32, i32, u32, u32),
    fps_num: u32,
    fps_den: u32,
    platform: &'static str,
    monitors: Vec<MonitorEntry>,
}

#[derive(Serialize, Clone)]
struct MonitorEntry {
    /// Bounds in canvas pixels, the same space as `region` and the frame rows.
    x: i32,
    y: i32,
    w: u32,
    h: u32,
    /// DPI zoom (Windows: dpi/96; macOS: Retina backing scale) — the editor's
    /// base factor for themed cursor sizing. Deliberately *not* a coordinate:
    /// it stays a density factor while `x/y/w/h` above are canvas pixels.
    scale: f64,
}

#[derive(Serialize)]
struct FrameRow {
    #[serde(rename = "type")]
    ty: &'static str,
    t: f64,
    /// Cursor hotspot in canvas pixels (see [`to_canvas`]).
    x: i32,
    y: i32,
    b: u32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    k: Vec<u32>,
    c: &'static str,
    /// The `cursor_image` sprite id this frame's cursor renders as; omitted
    /// when hidden or unavailable (mirroring the `k` omission convention).
    /// Assigned on the writer thread, which owns the dedupe state — the tick
    /// always sends `None`.
    #[serde(skip_serializing_if = "Option::is_none")]
    ci: Option<u32>,
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

/// Converts a capture-space coordinate to canvas pixels — the space the encoded
/// video, and therefore the editor, works in.
///
/// Windows capture coordinates are already physical pixels and `canvas_scale`
/// is 1.0, so this is a no-op there. macOS reports CG points while the canvas is
/// `region * backing scale` (`region::plan_region`), so without this the editor
/// maps a point offset onto a pixel grid and draws every overlay at
/// `1 / backing` of its correct distance from the region's top-left — half way
/// in, on a Retina display. `tracker.rs` already applies the same factor for the
/// click highlight; this is the sidecar catching up.
pub fn to_canvas(v: i32, canvas_scale: f64) -> i32 {
    (v as f64 * canvas_scale).round() as i32
}

fn event_row(t: f64, kind: RawEventKind, canvas_scale: f64) -> EventRow {
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
            row.x = Some(to_canvas(x, canvas_scale));
            row.y = Some(to_canvas(y, canvas_scale));
        }
        RawEventKind::MouseUp { btn, x, y } => {
            row.kind = "mu";
            row.btn = Some(btn);
            row.x = Some(to_canvas(x, canvas_scale));
            row.y = Some(to_canvas(y, canvas_scale));
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
    /// A frame row plus the same tick's sprite-capture outcome — travelling
    /// together so the writer can resolve the row's `ci` against sprite state
    /// that is exactly as old as the row itself.
    Frame {
        frame_ns: u64,
        pause_offset: u64,
        row: FrameRow,
        sprite: SpriteEvent,
    },
    Event(RawEvent),
    Flush(mpsc::Sender<()>),
}

/// Events snapshot their pause eligibility and offset on initial writer
/// receipt, as before. Draining startup rows must not query a later pause state.
enum PendingRow {
    Frame {
        frame_ns: u64,
        pause_offset: u64,
        row: FrameRow,
        sprite: SpriteEvent,
    },
    Event {
        event: RawEvent,
        pause_offset: u64,
    },
}

impl PendingRow {
    fn frame_time(&self) -> Option<(u64, u64)> {
        match self {
            Self::Frame { frame_ns, pause_offset, .. } => Some((*frame_ns, *pause_offset)),
            Self::Event { .. } => None,
        }
    }
}

/// Cap on distinct sprites recorded per session — a pathological cursor
/// stream (an animated custom cursor generating unique frames, say) must not
/// grow the sidecar without bound.
const MAX_SPRITE_COUNT: usize = 4096;

/// Cap on the cumulative raw payload bytes of recorded sprites, the second
/// half of the cache bound (a few huge sprites can cost as much as thousands
/// of small ones).
const MAX_SPRITE_BYTES: usize = 8 * 1024 * 1024;

/// Sprites wider or taller than this are never recorded. The frame row keeps
/// its `c` kind regardless, so the editor still has something to draw.
const MAX_SPRITE_DIM: u32 = 512;

/// The writer thread's sprite state (single-threaded by construction). Maps
/// sprite content to the small sequential id the file carries — the dedupe
/// key includes the FNV-1a 64 hash plus dimensions and byte length, but the
/// hash itself never reaches the wire (a u64 in JSON would hit the C#
/// parser's f64 precision trap) — and tracks which id the current frame rows
/// should reference.
struct SpriteCache {
    ids: HashMap<(u64, u32, u32, usize), u32>,
    /// The next id to assign; sequential from 1.
    next_id: u32,
    /// What the next frame row's `ci` should carry; `None` while the cursor
    /// is hidden or sprite capture has degraded.
    last_id: Option<u32>,
    /// Cumulative raw payload bytes of cached sprites ([`MAX_SPRITE_BYTES`]'s
    /// counter).
    total_bytes: usize,
    /// One-shot warning latches, so a capped or oversized cursor logs once
    /// instead of once per frame.
    warned_cap: bool,
    warned_oversize: bool,
}

impl SpriteCache {
    fn new() -> SpriteCache {
        SpriteCache {
            ids: HashMap::new(),
            next_id: 1,
            last_id: None,
            total_bytes: 0,
            warned_cap: false,
            warned_oversize: false,
        }
    }

    /// Folds one tick's sprite event into the cache, emitting a
    /// `cursor_image` row for a never-before-seen sprite. Runs immediately
    /// before the frame row is serialized, so a sprite's row always precedes
    /// the first frame row referencing it. Every degraded path (oversized,
    /// cache full, encode failure) keeps the previous `last_id` — the frame
    /// rows go on referencing the last recorded sprite, and `c` still records
    /// the kind.
    fn apply(&mut self, event: SpriteEvent, write_line: &mut impl FnMut(&str)) {
        let sprite = match event {
            SpriteEvent::Unchanged => return,
            SpriteEvent::Hidden => {
                self.last_id = None;
                return;
            }
            SpriteEvent::Candidate(s) => s,
        };
        if sprite.w > MAX_SPRITE_DIM || sprite.h > MAX_SPRITE_DIM {
            if !self.warned_oversize {
                self.warned_oversize = true;
                eprintln!(
                    "Warning: skipping an oversized {}x{} cursor sprite (limit {MAX_SPRITE_DIM} px)",
                    sprite.w, sprite.h
                );
            }
            return;
        }
        let key = (sprite.content_hash(), sprite.w, sprite.h, sprite.byte_len());
        if let Some(&id) = self.ids.get(&key) {
            self.last_id = Some(id);
            return;
        }
        if self.ids.len() >= MAX_SPRITE_COUNT
            || self.total_bytes + sprite.byte_len() > MAX_SPRITE_BYTES
        {
            if !self.warned_cap {
                self.warned_cap = true;
                eprintln!(
                    "Warning: the cursor sprite cache is full; new cursor shapes will not be \
                     recorded for the rest of this session"
                );
            }
            return;
        }
        // Encode failure is left uncached deliberately: the same malformed
        // sprite would fail identically, and caching it would burn an id on
        // a row that never existed.
        if let Some(line) = cursor_sprite::encode_row(self.next_id, &sprite) {
            write_line(&line);
            self.ids.insert(key, self.next_id);
            self.total_bytes += sprite.byte_len();
            self.last_id = Some(self.next_id);
            self.next_id += 1;
        }
    }
}

/// Resolves a frame's `ci` against the sprite cache and writes the row —
/// preceded, when the tick delivered a new sprite, by its `cursor_image` row.
fn write_frame(
    mut row: FrameRow,
    sprite: SpriteEvent,
    sprites: &mut SpriteCache,
    write_line: &mut impl FnMut(&str),
) {
    sprites.apply(sprite, write_line);
    row.ci = sprites.last_id;
    if let Ok(line) = serde_json::to_string(&row) {
        write_line(&line);
    }
}

/// Maps buffered observations only after the shared origin is known. Sprite
/// transitions still run for pre-origin frames: a surviving Unchanged event
/// may depend on pixels captured by a frame which the encoder skipped.
fn drain_pending(
    pending: &mut VecDeque<PendingRow>,
    clock: &FrameClock,
    sprites: &mut SpriteCache,
    canvas_scale: f64,
    closing: bool,
    write_line: &mut impl FnMut(&str),
) {
    if clock.t0() == 0 {
        let ticks: Vec<(u64, u64)> = pending.iter().filter_map(PendingRow::frame_time).collect();
        // Closing permits the bounded fallback even without a preceding tick.
        let _ = clock.try_resolve(&ticks, closing || pending.len() >= STARTUP_BUFFER_CAP);
        if closing {
            let _ = clock.resolve_for_close(ticks.first().copied());
        }
    }
    let t0 = clock.t0();
    if t0 == 0 {
        return;
    }
    let base = clock.base_offset();
    while let Some(row) = pending.pop_front() {
        match row {
            PendingRow::Frame {
                frame_ns,
                pause_offset,
                mut row,
                sprite,
            } => {
                if let Some(t) = map_t(frame_ns, t0, pause_offset.saturating_sub(base)) {
                    row.t = t;
                    write_frame(row, sprite, sprites, write_line);
                } else {
                    sprites.apply(sprite, write_line);
                }
            }
            PendingRow::Event { event, pause_offset } => {
                if let Some(t) = map_t(event.t_ns, t0, pause_offset.saturating_sub(base)) {
                    if let Ok(line) =
                        serde_json::to_string(&event_row(t, event.kind, canvas_scale))
                    {
                        write_line(&line);
                    }
                }
            }
        }
    }
}

/// The hook thread only sends. Its existing pause gate and encoder-offset
/// read happen on initial writer receipt, never when a buffered event drains.
/// Frames already carry the pause offset sampled on their graphics tick.
fn writer_loop(
    file: std::fs::File,
    rx: mpsc::Receiver<WriterMsg>,
    armed: std::sync::Arc<AtomicBool>,
    clock: std::sync::Arc<FrameClock>,
    output_addr: usize,
    canvas_scale: f64,
) {
    let output = output_addr as *mut obs_sys::obs_output_t;
    let mut w = BufWriter::new(file);
    let mut sprites = SpriteCache::new();
    let mut pending = VecDeque::new();
    loop {
        let (msg, disconnected) = match rx.recv() {
            Ok(msg) => (msg, false),
            Err(_) => {
                let (ack, _) = mpsc::channel();
                (WriterMsg::Flush(ack), true)
            }
        };
        handle_msg(
            &mut w,
            msg,
            &mut sprites,
            &armed,
            &clock,
            output,
            canvas_scale,
            &mut pending,
        );
        if disconnected {
            break;
        }
        // Preserve the existing batching: one flush after the channel's
        // current backlog, rather than one flush per hook event.
        while let Ok(msg) = rx.try_recv() {
            handle_msg(
                &mut w,
                msg,
                &mut sprites,
                &armed,
                &clock,
                output,
                canvas_scale,
                &mut pending,
            );
        }
        let _ = w.flush();
    }
    let _ = w.flush();
}

#[allow(clippy::too_many_arguments)]
fn handle_msg(
    w: &mut BufWriter<std::fs::File>,
    msg: WriterMsg,
    sprites: &mut SpriteCache,
    armed: &AtomicBool,
    clock: &FrameClock,
    output: *mut obs_sys::obs_output_t,
    canvas_scale: f64,
    pending: &mut VecDeque<PendingRow>,
) {
    let mut write_line = |line: &str| {
        if let Err(e) = writeln!(w, "{line}") {
            eprintln!("Warning: input-capture write failed: {e}");
        }
    };
    let row = match msg {
        WriterMsg::Header(h) => {
            if let Ok(line) = serde_json::to_string(&h) {
                write_line(&line);
            }
            None
        }
        WriterMsg::Frame { frame_ns, pause_offset, row, sprite } => {
            Some(PendingRow::Frame { frame_ns, pause_offset, row, sprite })
        }
        WriterMsg::Event(event) => {
            // Evaluate once, before startup buffering. Rechecking on drain
            // would drop pre-pause edges or apply a later pause's offset.
            if !armed.load(Ordering::Acquire) || unsafe { obs_sys::obs_output_paused(output) } {
                return;
            }
            Some(PendingRow::Event {
                event,
                pause_offset: unsafe { pause_offset_ns(output) },
            })
        }
        WriterMsg::Flush(ack) => {
            drain_pending(pending, clock, sprites, canvas_scale, true, &mut write_line);
            if let Err(e) = w.flush() {
                eprintln!("Warning: input-capture flush failed: {e}");
            }
            let _ = ack.send(());
            return;
        }
    };
    if let Some(row) = row {
        if pending.len() >= STARTUP_BUFFER_CAP {
            pending.pop_front();
        }
        pending.push_back(row);
    }
    drain_pending(pending, clock, sprites, canvas_scale, false, &mut write_line);
}

// ---------------------------------------------------------------------------
// Tick callback (graphics thread)
// ---------------------------------------------------------------------------

/// Everything the tick callback touches. The stable Box remains alive until
/// close has removed the callback and the writer has acknowledged its flush.
struct TickState {
    /// Set immediately before output.start(), cleared by close.
    armed: std::sync::Arc<AtomicBool>,
    /// close and Drop may both run; remove the callback only once.
    registered: AtomicBool,
    /// `*mut obs_output_t` as usize (pause state/offset reads).
    output: usize,
    /// Capture-space units per canvas pixel, applied to the row coordinates
    /// (see [`to_canvas`]).
    canvas_scale: f64,
    tx: mpsc::Sender<WriterMsg>,
    /// The hooks live exactly as long as the tick callback that samples them.
    hook: InputHook,
}

/// libobs tick callback — once per rendered frame on the graphics thread.
/// Only sampling, cursor-sprite rasterization and a channel send; never file
/// I/O or image encoding.
unsafe extern "C" fn tick(param: *mut c_void, _seconds: f32) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        sample_tick(param);
    }));
}

unsafe fn sample_tick(param: *mut c_void) {
    let state = &*(param as *const TickState);

    if !state.armed.load(Ordering::Acquire) {
        return;
    }
    let frame_ns = obs_sys::obs_get_video_frame_time();
    let output = state.output as *mut obs_sys::obs_output_t;
    if obs_sys::obs_output_paused(output) {
        return;
    }
    let offset = pause_offset_ns(output);

    // ONE `get_cursor_state` snapshot serves the frame row's position/kind
    // AND the sprite rasterization — the DESIGN §1 consistency contract: the
    // recorded pixels can never disagree with where the row says the cursor
    // was. Deliberately below the arm/pause gates: no sprite work runs while
    // the recording is not accumulating frames.
    let cursor = platform::get_cursor_state();
    let sprite = platform::take_cursor_sprite(&cursor);
    let (buttons, keys) = state.hook.snapshot();
    let _ = state.tx.send(WriterMsg::Frame {
        frame_ns,
        pause_offset: offset,
        row: FrameRow {
            ty: "frame",
            // Resolved on the writer after the packet clock is known.
            t: 0.0,
            x: to_canvas(cursor.x, state.canvas_scale),
            y: to_canvas(cursor.y, state.canvas_scale),
            b: buttons,
            k: keys,
            c: cursor.kind.as_str(),
            ci: None,
        },
        sprite,
    });
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
    /// The region's origin in canvas pixels — every row coordinate is measured
    /// from here. Stored as a bare origin rather than a `Rect` so there is no
    /// half-converted rectangle lying around: the extent comes from `canvas`.
    region_origin: (i32, i32),
    /// The encoded video's exact dimensions, reported as the header region's
    /// `w`/`h`. Taken from the region plan rather than recomputed: the planner
    /// rounds the canvas down to an even size, so multiplying here could differ
    /// by a pixel and desync the editor's source rect.
    canvas: (u32, u32),
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
        canvas_scale: f64,
        canvas: (u32, u32),
        output: *mut obs_sys::obs_output_t,
        clock: std::sync::Arc<FrameClock>,
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

        let writer_armed = armed.clone();
        let writer_clock = clock;
        let output_addr = output as usize;
        std::thread::Builder::new()
            .name("input-capture-writer".to_string())
            .spawn(move || {
                writer_loop(file, rx, writer_armed, writer_clock, output_addr, canvas_scale)
            })
            .map_err(|e| format!("failed to spawn the input-capture writer: {e}"))?;

        // The hook thread forwards edges into the writer channel; timestamp
        // mapping and the pause gate run on the writer thread.
        let hook_tx = tx.clone();
        let hook = InputHook::start(Box::new(move |ev| {
            let _ = hook_tx.send(WriterMsg::Event(ev));
        }))
        .map_err(|e| format!("failed to install the input hooks: {e}"))?;

        // Bounds into canvas pixels; `scale` stays a density factor.
        let monitors = monitors
            .iter()
            .map(|m| MonitorEntry {
                x: to_canvas(m.x, canvas_scale),
                y: to_canvas(m.y, canvas_scale),
                w: (m.width as f64 * canvas_scale).round() as u32,
                h: (m.height as f64 * canvas_scale).round() as u32,
                scale: platform::monitor_display_scale(m),
            })
            .collect();

        let state = Box::new(TickState {
            armed,
            registered: AtomicBool::new(true),
            output: output_addr,
            canvas_scale,
            tx: tx.clone(),
            hook,
        });
        let param = &*state as *const TickState as *mut c_void;
        unsafe { obs_sys::obs_add_tick_callback(Some(tick), param) };

        Ok(InputCapture {
            tx,
            path: path.to_path_buf(),
            region_origin: (
                to_canvas(region.x, canvas_scale),
                to_canvas(region.y, canvas_scale),
            ),
            canvas,
            monitors,
            state,
        })
    }

    /// The sidecar file path, for the protocol payloads.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Called immediately before output.start(): the final fps is now fixed,
    /// and sampling must precede the first encoded frame, not its start signal.
    pub fn on_output_started(&self, fps_num: u32) {
        let _ = self.tx.send(WriterMsg::Header(HeaderLine {
            ty: "header",
            version: 2,
            region: (
                self.region_origin.0,
                self.region_origin.1,
                self.canvas.0,
                self.canvas.1,
            ),
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
    /// Remove the tick callback before the flush barrier: an in-flight tick
    /// may already have passed the arm gate. Hook events arriving afterwards
    /// are rejected by the writer's arm gate.
    pub fn close(&self) {
        self.state.armed.store(false, Ordering::Release);
        if self.state.registered.swap(false, Ordering::AcqRel) {
            let param = &*self.state as *const TickState as *mut c_void;
            unsafe { obs_sys::obs_remove_tick_callback(Some(tick), param) };
        }
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
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input_hook::{BTN_LEFT, BTN_RIGHT};

    #[test]
    fn startup_drain_preserves_sprite_transitions_from_skipped_frames() {
        let clock = FrameClock::new();
        let mut pending = VecDeque::new();
        pending.push_back(PendingRow::Frame {
            frame_ns: 100,
            pause_offset: 0,
            row: test_frame(0.0),
            sprite: SpriteEvent::Candidate(test_sprite(9)),
        });
        pending.push_back(PendingRow::Event {
            event: RawEvent {
                t_ns: 1_000_200,
                kind: RawEventKind::KeyUp { vk: 17 },
            },
            pause_offset: 500_000,
        });
        pending.push_back(PendingRow::Frame {
            frame_ns: 2_000_200,
            pause_offset: 500_000,
            row: test_frame(0.0),
            sprite: SpriteEvent::Unchanged,
        });
        let mut sprites = SpriteCache::new();
        let mut lines = Vec::new();
        drain_pending(&mut pending, &clock, &mut sprites, 1.0, false, &mut sink(&mut lines));
        assert_eq!(pending.len(), 3);
        assert!(lines.is_empty());

        clock.resolve_for_close(Some((200, 0)));
        drain_pending(&mut pending, &clock, &mut sprites, 1.0, false, &mut sink(&mut lines));
        assert!(pending.is_empty());
        assert_eq!(lines.len(), 3);
        assert!(lines[0].starts_with(r#"{"type":"cursor_image","id":1,"#));
        assert_eq!(lines[1], r#"{"type":"event","t":0.5,"kind":"ku","vk":17}"#);
        assert_eq!(
            lines[2],
            r#"{"type":"frame","t":1.5,"x":0,"y":0,"b":0,"c":"arrow","ci":1}"#
        );
    }

    #[test]
    fn startup_drain_subtracts_only_pauses_after_the_origin() {
        let clock = FrameClock::new();
        let t0 = 3_000_000_000;
        let base = 2_000_000_000;
        let mut pending = VecDeque::new();
        pending.push_back(PendingRow::Frame {
            frame_ns: t0,
            pause_offset: base,
            row: test_frame(0.0),
            sprite: SpriteEvent::Hidden,
        });
        pending.push_back(PendingRow::Event {
            event: RawEvent {
                t_ns: t0 + 250_000_000,
                kind: RawEventKind::KeyUp { vk: 17 },
            },
            pause_offset: base,
        });
        pending.push_back(PendingRow::Frame {
            frame_ns: t0 + 3_000_000_000,
            pause_offset: base + 2_000_000_000,
            row: test_frame(0.0),
            sprite: SpriteEvent::Unchanged,
        });
        clock.resolve_for_close(pending.front().and_then(PendingRow::frame_time));
        let mut sprites = SpriteCache::new();
        let mut lines = Vec::new();
        drain_pending(&mut pending, &clock, &mut sprites, 1.0, false, &mut sink(&mut lines));
        assert!(pending.is_empty());
        assert_eq!(
            lines,
            vec![
                r#"{"type":"frame","t":0.0,"x":0,"y":0,"b":0,"c":"arrow"}"#,
                r#"{"type":"event","t":250.0,"kind":"ku","vk":17}"#,
                r#"{"type":"frame","t":1000.0,"x":0,"y":0,"b":0,"c":"arrow"}"#,
            ]
        );
    }

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
            version: 2,
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
            r#"{"type":"header","version":2,"region":[-100,50,2560,1440],"fps_num":30,"fps_den":1,"platform":"windows","monitors":[{"x":0,"y":0,"w":2560,"h":1440,"scale":1.5}]}"#
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
            ci: Some(3),
        };
        assert_eq!(
            serde_json::to_string(&row).unwrap(),
            r#"{"type":"frame","t":1234.567,"x":100,"y":200,"b":1,"k":[17,75],"c":"arrow","ci":3}"#
        );
    }

    #[test]
    fn frame_row_omits_an_empty_key_list_and_an_absent_sprite() {
        let row = FrameRow {
            ty: "frame",
            t: 0.0,
            x: -5,
            y: 7,
            b: 0,
            k: vec![],
            c: "hidden",
            ci: None,
        };
        assert_eq!(
            serde_json::to_string(&row).unwrap(),
            r#"{"type":"frame","t":0.0,"x":-5,"y":7,"b":0,"c":"hidden"}"#
        );
    }

    #[test]
    fn to_canvas_is_identity_at_unit_scale() {
        // Windows always lands here: capture coords are already physical px.
        for v in [-1920, -1, 0, 1, 1080, 3840] {
            assert_eq!(to_canvas(v, 1.0), v);
        }
    }

    #[test]
    fn to_canvas_scales_points_to_retina_pixels() {
        // macOS: CG points -> canvas px on a 2x display.
        assert_eq!(to_canvas(0, 2.0), 0);
        assert_eq!(to_canvas(100, 2.0), 200);
        // Displays left of / above the primary have negative origins; the
        // conversion must stay symmetric or the region offset skews.
        assert_eq!(to_canvas(-100, 2.0), -200);
        assert_eq!(to_canvas(-1, 2.0), -2);
    }

    #[test]
    fn to_canvas_rounds_half_away_from_zero() {
        // Fractional backing scales exist (1.5x "More Space" modes).
        assert_eq!(to_canvas(3, 1.5), 5); // 4.5
        assert_eq!(to_canvas(-3, 1.5), -5);
        assert_eq!(to_canvas(1, 1.5), 2); // 1.5
    }

    /// The bug this conversion exists for: a cursor at the region's
    /// bottom-right must land at the bottom-right of the encoded frame. Before
    /// the fix the editor received point offsets and mapped them onto a pixel
    /// grid, drawing every overlay half way in on a Retina display.
    #[test]
    fn region_relative_offsets_span_the_full_canvas() {
        // 800x600 points of region on a 2x display -> a 1600x1200 canvas.
        let (region_x, region_y) = (100, 50);
        let (cursor_x, cursor_y) = (900, 650); // the region's far corner
        let scale = 2.0;

        let offset_x = to_canvas(cursor_x, scale) - to_canvas(region_x, scale);
        let offset_y = to_canvas(cursor_y, scale) - to_canvas(region_y, scale);
        assert_eq!((offset_x, offset_y), (1600, 1200));

        // Unscaled, the same corner would have reported the canvas midpoint.
        assert_eq!((cursor_x - region_x, cursor_y - region_y), (800, 600));
    }

    #[test]
    fn mouse_event_coordinates_are_converted() {
        let md = event_row(
            1.0,
            RawEventKind::MouseDown {
                btn: BTN_LEFT,
                x: 100,
                y: 200,
            },
            2.0,
        );
        assert_eq!(
            serde_json::to_string(&md).unwrap(),
            r#"{"type":"event","t":1.0,"kind":"md","btn":1,"x":200,"y":400}"#
        );

        // Key rows carry no coordinates, so the scale must not perturb them.
        let kd = event_row(1.0, RawEventKind::KeyDown { vk: 17, ch: None }, 2.0);
        assert_eq!(
            serde_json::to_string(&kd).unwrap(),
            r#"{"type":"event","t":1.0,"kind":"kd","vk":17}"#
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
            1.0,
        );
        assert_eq!(
            serde_json::to_string(&kd).unwrap(),
            r#"{"type":"event","t":1234.111,"kind":"kd","vk":75,"ch":"k"}"#
        );

        // No translated char -> `ch` omitted, not null.
        let kd_none = event_row(1.0, RawEventKind::KeyDown { vk: 17, ch: None }, 1.0);
        assert_eq!(
            serde_json::to_string(&kd_none).unwrap(),
            r#"{"type":"event","t":1.0,"kind":"kd","vk":17}"#
        );

        let ku = event_row(1234.222, RawEventKind::KeyUp { vk: 75 }, 1.0);
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
            1.0,
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
            1.0,
        );
        assert_eq!(
            serde_json::to_string(&mu).unwrap(),
            r#"{"type":"event","t":1305.5,"kind":"mu","btn":2,"x":-10,"y":0}"#
        );
    }

    // -- writer sprite cache -------------------------------------------------

    use crate::cursor_sprite::{RawSprite, SpritePixels};

    /// A 1x1 sprite whose content is controlled by `seed`, so distinct seeds
    /// dedupe apart and equal seeds dedupe together.
    fn test_sprite(seed: u8) -> RawSprite {
        RawSprite {
            kind: "arrow",
            w: 1,
            h: 1,
            hotx: 0,
            hoty: 0,
            bmp: SpritePixels::Bgra(vec![seed, 0, 0, 255]),
            mask: None,
        }
    }

    fn test_frame(t: f64) -> FrameRow {
        FrameRow {
            ty: "frame",
            t,
            x: 0,
            y: 0,
            b: 0,
            k: vec![],
            c: "arrow",
            ci: None,
        }
    }

    /// A fresh per-call write sink, so the tests can assert on `lines`
    /// between writes without fighting a long-lived closure borrow.
    fn sink(lines: &mut Vec<String>) -> impl FnMut(&str) + '_ {
        move |l| lines.push(l.to_string())
    }

    #[test]
    fn sprite_cache_dedupes_and_assigns_sequential_ids() {
        let mut cache = SpriteCache::new();
        let mut lines = Vec::new();
        // First sighting: a cursor_image row with id 1.
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );
        assert_eq!(cache.last_id, Some(1));
        assert_eq!(lines.len(), 1);

        // Same content again: no new row, same id.
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );
        assert_eq!(cache.last_id, Some(1));
        assert_eq!(lines.len(), 1);

        // New content: id 2, second row.
        cache.apply(
            SpriteEvent::Candidate(test_sprite(2)),
            &mut sink(&mut lines),
        );
        assert_eq!(cache.last_id, Some(2));
        assert_eq!(lines.len(), 2);
        assert!(lines[0].starts_with(r#"{"type":"cursor_image","id":1,"#));
        assert!(lines[1].starts_with(r#"{"type":"cursor_image","id":2,"#));

        // Unchanged keeps the reference; hidden clears it.
        cache.apply(SpriteEvent::Unchanged, &mut sink(&mut lines));
        assert_eq!(cache.last_id, Some(2));
        cache.apply(SpriteEvent::Hidden, &mut sink(&mut lines));
        assert_eq!(cache.last_id, None);

        // A previously seen sprite reappearing resolves by dedupe, no new row.
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );
        assert_eq!(cache.last_id, Some(1));
        assert_eq!(lines.len(), 2);
    }

    #[test]
    fn sprite_cache_skips_oversized_sprites_and_keeps_the_reference() {
        let mut cache = SpriteCache::new();
        let mut lines = Vec::new();
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );

        let big = RawSprite {
            w: MAX_SPRITE_DIM + 1,
            bmp: SpritePixels::Bgra(vec![0; (MAX_SPRITE_DIM as usize + 1) * 4]),
            ..test_sprite(2)
        };
        cache.apply(SpriteEvent::Candidate(big), &mut sink(&mut lines));
        // Skipped: no row, the previous reference survives, warned once.
        assert_eq!(lines.len(), 1);
        assert_eq!(cache.last_id, Some(1));
        assert!(cache.warned_oversize);
    }

    #[test]
    fn sprite_cache_stops_emitting_past_the_byte_cap() {
        let mut cache = SpriteCache::new();
        let mut lines = Vec::new();
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );

        // Pretend earlier sprites already spent the byte budget.
        cache.total_bytes = MAX_SPRITE_BYTES;
        cache.apply(
            SpriteEvent::Candidate(test_sprite(2)),
            &mut sink(&mut lines),
        );
        assert_eq!(lines.len(), 1);
        assert_eq!(cache.last_id, Some(1));
        assert!(cache.warned_cap);

        // Already-cached sprites still resolve while capped.
        cache.apply(
            SpriteEvent::Candidate(test_sprite(1)),
            &mut sink(&mut lines),
        );
        assert_eq!(cache.last_id, Some(1));
        assert_eq!(lines.len(), 1);
    }

    /// The ordering half of the wire contract: a `cursor_image` row must land
    /// before the first frame row referencing it, and the frame row's `ci`
    /// must carry the id that row introduced.
    #[test]
    fn cursor_image_rows_precede_their_first_reference() {
        let mut cache = SpriteCache::new();
        let mut lines = Vec::new();
        write_frame(
            test_frame(0.0),
            SpriteEvent::Candidate(test_sprite(9)),
            &mut cache,
            &mut sink(&mut lines),
        );
        assert_eq!(lines.len(), 2);
        // The sprite row first, with the exact-JSON field prefix (order is
        // part of the contract; the payload tail is PNG/base64).
        assert!(lines[0].starts_with(
            r#"{"type":"cursor_image","id":1,"kind":"arrow","w":1,"h":1,"hotx":0,"hoty":0,"bmp":""#
        ));
        assert_eq!(
            lines[1],
            r#"{"type":"frame","t":0.0,"x":0,"y":0,"b":0,"c":"arrow","ci":1}"#
        );

        // Steady state: unchanged sprite, frame rows keep the reference with
        // no further sprite rows.
        write_frame(
            test_frame(16.6),
            SpriteEvent::Unchanged,
            &mut cache,
            &mut sink(&mut lines),
        );
        assert_eq!(lines.len(), 3);
        assert_eq!(
            lines[2],
            r#"{"type":"frame","t":16.6,"x":0,"y":0,"b":0,"c":"arrow","ci":1}"#
        );

        // Hidden: the reference drops off the row entirely.
        write_frame(
            test_frame(33.3),
            SpriteEvent::Hidden,
            &mut cache,
            &mut sink(&mut lines),
        );
        assert_eq!(
            lines[3],
            r#"{"type":"frame","t":33.3,"x":0,"y":0,"b":0,"c":"arrow"}"#
        );
    }
}
