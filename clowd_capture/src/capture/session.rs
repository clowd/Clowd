use std::sync::atomic::AtomicUsize;
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use crate::app::App;
use crate::gxi;
use crate::image_extract;
use crate::render::protocol::{BlurredDesktopImage, CycleParams, RenderMsg, WorkerInput};
use crate::render::worker::{self, RenderWorkerParams, WorkerSetup};
use crate::settings::CapturerSettings;
use crate::sync::{Latch, VisibleLatch};
use crate::system::{CapturedCursor, CapturedDesktop, MonitorInfo, SystemInterop, WindowPeekImage, WindowWalker};
use crate::telemetry::startup::StartupTimings;
use clowd_rust_core::geometry::ScreenPointF;

type WalkerLatch = Arc<Latch<Arc<WindowWalker>>>;
type PeekImagesLatch = Arc<Latch<Vec<Arc<WindowPeekImage>>>>;

pub struct CaptureSession {
    app: App,
    timings: Arc<StartupTimings>,
}

impl CaptureSession {
    /// `t_start` is taken by `main` when this capture leaves standby. Process
    /// loading, the logger and Sentry stay warm and are intentionally excluded;
    /// every display/GPU/window resource constructed below remains measured.
    pub fn new(settings: Arc<CapturerSettings>, t_start: Instant) -> anyhow::Result<Self> {
        let monitors = SystemInterop::all_monitors();
        if monitors.is_empty() {
            anyhow::bail!("no monitors detected; nothing to render to");
        }
        // Sampled before the timings exist — the worker count sizes them, and
        // enumerating the monitors is what produces that count.
        let monitors_enumerated = t_start.elapsed();
        let timings = Arc::new(StartupTimings::new(t_start, monitors.len()));
        timings.mark_monitors_enumerated(monitors_enumerated);

        // Everything the desktop capture needs, gathered before anything else:
        // the capture is the longest pole in startup and the main thread blocks
        // on it below, so it is spawned FIRST and the GPU instance and the
        // render workers are built beside it instead of in front of it. Only the
        // cursor and the monitor list are genuine prerequisites; the worker
        // channels it also wants arrive later over `worker_channels_latch`.
        let initial_mouse = SystemInterop::get_mouse_position(&monitors);
        let initial_mouse_f = ScreenPointF::new(initial_mouse.x as f32, initial_mouse.y as f32);
        let captured_cursor = SystemInterop::capture_cursor(&monitors);
        let ready_count = Arc::new(AtomicUsize::new(0));
        let visible_latch = Arc::new(VisibleLatch::new());

        // Peek is a cosmetic nicety with the biggest resource bill in the
        // whole capturer: a second full-desktop texture per worker (the
        // blur), a wide all-core blur burst at show, and a PrintWindow pass
        // per obstructed window. On an integrated GPU (≤ 1 GB dedicated
        // VRAM, see `MonitorInfo::low_vram_adapter` — never trips on Apple
        // unified memory) that bill is the difference between fitting the
        // carve-out and thrashing it, so the feature turns itself off there
        // regardless of the setting.
        let peek_enabled = settings.obscured_window_peek_enabled && {
            let low_vram = monitors.iter().any(|m| m.low_vram_adapter);
            if low_vram {
                log::info!("peek disabled: integrated/low-VRAM adapter drives at least one monitor");
            }
            !low_vram
        };

        let (screenshot_latch, worker_channels_latch) = spawn_screenshot_job(ScreenshotJobParams {
            monitors: monitors.clone(),
            cursor: captured_cursor,
            peek_enabled,
            accent_color: settings.accent_color,
            initial_mouse: initial_mouse_f,
            ready_count: ready_count.clone(),
            visible_latch: visible_latch.clone(),
            timings: timings.clone(),
        });

        let instance = gxi::Instance::new();
        timings.mark_instance_created();
        timings.mark_initialize();

        let worker_failed = Arc::new(AtomicUsize::new(0));
        let worker_setups = spawn_render_workers(&monitors, &instance, &timings, &worker_failed);
        timings.mark_workers_spawned();

        let input_txs: Vec<_> = worker_setups
            .iter()
            .map(|s| s.input_tx.clone())
            .collect();
        let render_msg_txs: Vec<_> = worker_setups
            .iter()
            .map(|s| s.render_msg_tx.clone())
            .collect();
        // Releases the screenshot thread's second stage. It may already be
        // parked here waiting, or it may still be compositing displays — either
        // way this hand-off is what lets the capture start before the workers do.
        worker_channels_latch.set(WorkerChannels {
            input_txs,
            render_msg_txs: render_msg_txs.clone(),
        });

        let (walker_latch, peek_images_latch) = spawn_walker_job(
            monitors.clone(),
            render_msg_txs,
            peek_enabled,
            settings.obscured_window_detection_threshold,
            settings.rounded_window_corners,
            timings.clone(),
        );

        // Deliberately NOT waited on here. The main thread used to block on this
        // latch (~50 ms — the single largest delta on the startup critical path),
        // serializing the event loop, every window/surface creation and every
        // surface configure behind the capture. `App` now takes the latch itself
        // and picks the buffer up inside the event loop
        // (`app.rs::try_pick_up_screenshot`), so all of that setup runs BESIDE
        // the capture instead of after it. What still waits for the bitmap waits
        // in the right place: the macOS frozen-desktop backdrop is installed —
        // and the windows ordered front — only once the buffer lands, and the
        // workers cannot reach frame 0 earlier anyway (`BeginCycle` carries the
        // snapshot and is broadcast by the capture thread).
        //
        // The old 30 s bound survives as `App`'s screenshot deadline, enforced in
        // the pickup: a wedged CG capture call must still end as a clean non-zero
        // exit (`App::fatal_result`), never a process idling forever with nothing
        // on screen and no way to close it from the shell.
        let app = App::new(
            settings,
            timings.clone(),
            instance,
            monitors,
            initial_mouse_f,
            worker_setups,
            screenshot_latch,
            walker_latch,
            peek_images_latch,
            ready_count,
            visible_latch,
            worker_failed,
        );

        Ok(Self {
            app,
            timings,
        })
    }

    /// Shared with `main` so the stages that happen after the session is
    /// built — the event loop, entering `run_app` — land on the same clock.
    pub fn timings(&self) -> &Arc<StartupTimings> {
        &self.timings
    }

    pub fn into_app(self) -> App {
        self.app
    }
}

fn spawn_render_workers(
    monitors: &[MonitorInfo],
    instance: &gxi::Instance,
    startup: &Arc<StartupTimings>,
    failed_count: &Arc<AtomicUsize>,
) -> Vec<WorkerSetup> {
    monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            worker::spawn_render_worker(RenderWorkerParams {
                monitor: m.clone(),
                monitor_index: i,
                instance: instance.clone(),
                startup: startup.clone(),
                failed_count: failed_count.clone(),
            })
        })
        .collect()
}

/// Inputs for the screenshot job, grouped into a struct to keep the
/// spawner's argument list manageable.
struct ScreenshotJobParams {
    pub monitors: Vec<MonitorInfo>,
    pub cursor: Option<CapturedCursor>,
    pub peek_enabled: bool,
    pub accent_color: [f32; 4],
    pub initial_mouse: ScreenPointF,
    pub ready_count: Arc<AtomicUsize>,
    pub visible_latch: Arc<VisibleLatch>,
    /// This cycle's debug timings; the screenshot offsets are recorded here
    /// and the `Arc` rides to every worker on `CycleParams`.
    pub timings: Arc<StartupTimings>,
}

/// The half of the screenshot job's inputs that cannot exist until the render
/// workers have been spawned. Handed over out-of-band so the capture itself
/// does not have to wait for them.
#[derive(Clone)]
struct WorkerChannels {
    input_txs: Vec<mpsc::Sender<WorkerInput>>,
    render_msg_txs: Vec<mpsc::Sender<RenderMsg>>,
}

/// Both waits inside the screenshot thread are bounded on the same principle as
/// the main thread's: a background thread must never be the reason a capture that
/// has already gone wrong cannot be observed to have gone wrong. Neither bound is
/// ever expected to elapse — the channels are handed over microseconds later, and
/// the visible latch is signaled by the show gate or by `finish_cycle` on cancel.
const SCREENSHOT_STAGE_TIMEOUT: Duration = Duration::from_secs(30);

/// Capture the desktop bitmap on a background thread and publish it through the
/// returned latch; then, once `worker_channels_latch` delivers the render
/// workers' senders, broadcast `BeginCycle` (snapshot + per-cycle params) to
/// every worker and, when peek is enabled, follow up with the blurred desktop.
///
/// The two stages are split so the capture can be kicked off before the workers
/// exist — it is the longest single step in startup and the main thread blocks
/// on its first stage, so it starts as early as the cursor and monitor list
/// allow. See the ordering note in `CaptureSession::new`.
fn spawn_screenshot_job(params: ScreenshotJobParams) -> (Arc<Latch<Arc<CapturedDesktop>>>, Arc<Latch<WorkerChannels>>) {
    let ScreenshotJobParams {
        monitors,
        cursor,
        peek_enabled,
        accent_color,
        initial_mouse,
        ready_count,
        visible_latch,
        timings,
    } = params;

    let screenshot_latch = Arc::new(Latch::new());
    let worker_channels_latch: Arc<Latch<WorkerChannels>> = Arc::new(Latch::new());
    let latch = screenshot_latch.clone();
    let channels_latch = worker_channels_latch.clone();
    std::thread::Builder::new()
        .name("screenshot".into())
        .spawn(move || {
            timings
                .background
                .screenshot_start
                .set_once(timings.t_start.elapsed());
            let captured = Arc::new(SystemInterop::capture_desktop_bitmap(monitors, cursor));
            latch.set(captured.clone());
            timings
                .background
                .screenshot
                .set_once(timings.t_start.elapsed());

            let Some(channels) = channels_latch.wait_timeout(SCREENSHOT_STAGE_TIMEOUT) else {
                error!("screenshot: worker channels never arrived; no cycle broadcast");
                return;
            };
            let cycle = Arc::new(CycleParams {
                snapshot: captured.clone(),
                accent_color,
                initial_mouse,
                ready_count,
                visible_latch: visible_latch.clone(),
            });
            for tx in &channels.input_txs {
                let _ = tx.send(WorkerInput::BeginCycle(cycle.clone()));
            }

            if peek_enabled {
                // Gated on the overlay actually being on screen. `stack_blur` fans out
                // wide (`AdaptiveReserve(2)` — all but two cores; see
                // `blur_desktop_bgra` for why cores, not priority, are the lever) and
                // nothing reads `BlurredDesktop` until the user hovers a peek-eligible
                // window, which cannot happen before the overlay is visible. Ungated it
                // competed with window creation, the snapshot upload and frame 0 for
                // exactly those cores, on the one path where they are the critical
                // path. It still runs as soon as the gate opens — not later — so the
                // blurred backdrop stays temporally close to the desktop capture and
                // the peek images.
                //
                // Bounded rather than an unconditional wait: on the exit paths that
                // never signal (every window failing to create) this thread would
                // otherwise stay parked holding the sender clones, and a worker whose
                // handoff failed only exits on channel disconnect.
                if !visible_latch.wait_timeout(SCREENSHOT_STAGE_TIMEOUT) {
                    warn!("screenshot: overlay never became visible; blurring anyway");
                }
                // stack_blur radius 4 visually approximates the old sigma-2.0 gaussian.
                let t_blur = std::time::Instant::now();
                let (bgra, w, h) = image_extract::blur_desktop_bgra(&captured.bgra, captured.width, captured.height, 4);
                let blurred = Arc::new(BlurredDesktopImage {
                    bgra,
                    width: w,
                    height: h,
                });
                for tx in &channels.render_msg_txs {
                    let _ = tx.send(RenderMsg::BlurredDesktop(blurred.clone()));
                }
                info!("screenshot: desktop blur complete in {:?}", t_blur.elapsed());
            }
        })
        .expect("spawn screenshot thread");
    (screenshot_latch, worker_channels_latch)
}

/// Snapshot the window z-order on a background thread and, when peek is
/// enabled, capture obstructed-window images.
fn spawn_walker_job(
    monitors: Vec<MonitorInfo>,
    render_msg_txs: Vec<mpsc::Sender<RenderMsg>>,
    peek_enabled: bool,
    visibility_threshold: f32,
    rounded_corners: bool,
    timings: Arc<StartupTimings>,
) -> (WalkerLatch, PeekImagesLatch) {
    let walker_latch = Arc::new(Latch::new());
    let peek_images_latch = Arc::new(Latch::new());
    let peek_txs = render_msg_txs;
    let latch = walker_latch.clone();
    let peek_latch = peek_images_latch.clone();

    std::thread::Builder::new()
        .name("walker".into())
        .spawn(move || {
            timings
                .background
                .walker_start
                .set_once(timings.t_start.elapsed());
            let walker = Arc::new(SystemInterop::snapshot_windows(&monitors, visibility_threshold, rounded_corners));
            let obstructed = if peek_enabled { walker.obstructed_windows() } else { Vec::new() };
            latch.set(walker.clone());
            timings
                .background
                .walker
                .set_once(timings.t_start.elapsed());

            // Only now, with hover hit-testing already live off the
            // published snapshot: ask the OS for each window's real corner
            // radius where that is a per-window round-trip (macOS; a no-op
            // on Windows, which resolved it during the snapshot). Ahead of
            // the peek captures because it touches every hover, not only
            // the obstructed windows.
            walker.probe_corner_radii(&monitors);

            if !peek_enabled {
                return;
            }

            capture_peek_images(&obstructed, &peek_txs, &peek_latch);
        })
        .expect("spawn walker thread");

    (walker_latch, peek_images_latch)
}

#[cfg(windows)]
fn capture_peek_images(
    obstructed: &[crate::system::ObstructedWindow],
    peek_txs: &[std::sync::mpsc::Sender<RenderMsg>],
    peek_latch: &Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
) {
    info!("walker: capturing {} obstructed window images", obstructed.len());
    // A bounded low-priority pool, not a thread per window: these land in
    // the seconds right after the overlay shows and must not gang up on the
    // render loop. Four workers keeps the captures temporally close to each
    // other and to the desktop snapshot on realistic window counts while
    // capping the burst. Each capture still broadcasts the moment it lands,
    // so a hovered window's peek is not held behind the batch.
    let next = std::sync::atomic::AtomicUsize::new(0);
    let all_peeks: Vec<Arc<WindowPeekImage>> = {
        let workers = obstructed.len().min(4);
        let peeks: std::sync::Mutex<Vec<Arc<WindowPeekImage>>> = std::sync::Mutex::new(Vec::new());
        std::thread::scope(|s| {
            for _ in 0..workers {
                s.spawn(|| {
                    // Background tier: PrintWindow-style captures churn DWM
                    // and disk; nothing visible waits on the batch.
                    crate::system::background_thread_priority();
                    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
                    let _ = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
                    loop {
                        let i = next.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                        let Some(ow) = obstructed.get(i) else {
                            return;
                        };
                        let Some((bgra, w, h)) = SystemInterop::capture_peek_image(ow) else {
                            continue;
                        };
                        let crop_x = ow.rect.min_x() - ow.raw_rect.min_x();
                        let crop_y = ow.rect.min_y() - ow.raw_rect.min_y();
                        let peek = Arc::new(WindowPeekImage {
                            window_index: ow.window_index,
                            window_rect: ow.rect,
                            bgra,
                            width: w,
                            height: h,
                            crop_x,
                            crop_y,
                            obstruction_rects: ow.obstruction_rects.clone(),
                        });
                        for tx in peek_txs {
                            let _ = tx.send(RenderMsg::PeekImage(peek.clone()));
                        }
                        peeks.lock().unwrap().push(peek);
                    }
                });
            }
        });
        peeks.into_inner().unwrap()
    };
    peek_latch.set(all_peeks);
    info!("walker: obstructed window capture complete");
}

#[cfg(target_os = "macos")]
fn capture_peek_images(
    obstructed: &[crate::system::ObstructedWindow],
    peek_txs: &[std::sync::mpsc::Sender<RenderMsg>],
    peek_latch: &Arc<Latch<Vec<Arc<WindowPeekImage>>>>,
) {
    info!("walker: capturing {} obstructed window images", obstructed.len());
    let all_peeks: Vec<Arc<WindowPeekImage>> = std::thread::scope(|s| {
        let handles: Vec<_> = obstructed
            .iter()
            .map(|ow| {
                s.spawn(|| {
                    let (bgra, w, h) = SystemInterop::capture_peek_image(ow)?;
                    let peek = Arc::new(WindowPeekImage {
                        window_index: ow.window_index,
                        window_rect: ow.rect,
                        bgra,
                        width: w,
                        height: h,
                        crop_x: 0,
                        crop_y: 0,
                        obstruction_rects: ow.obstruction_rects.clone(),
                    });
                    for tx in peek_txs {
                        let _ = tx.send(RenderMsg::PeekImage(peek.clone()));
                    }
                    Some(peek)
                })
            })
            .collect();
        handles
            .into_iter()
            .filter_map(|h| h.join().ok().flatten())
            .collect()
    });
    peek_latch.set(all_peeks);
    info!("walker: obstructed window capture complete");
}
