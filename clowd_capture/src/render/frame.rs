use std::time::{Duration, Instant};

use crate::gpu::overlay::{CROSSHAIR_VERTICES, SELECTION_VERTICES};
use crate::gpu::WindowGpu;
use crate::gxi::{self, AcquireResult};
use crate::render::desktop::{OverlayVisibility, SnapshotState};
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::ui::gpu::UiRenderer;

/// What [`draw_once`] wants the render loop to do next.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum DrawStatus {
    /// Frame drawn, or a transient miss (timeout / occluded); the next
    /// iteration retries.
    Continue,
    /// The GPU device itself is gone ([`AcquireResult::DeviceLost`], the
    /// d3d11 backend's `DXGI_ERROR_DEVICE_REMOVED/RESET` map). Terminal:
    /// a dead device fails every subsequent acquire instantly, so
    /// retrying would hot-spin — the worker must exit instead. Dead on
    /// the metal backend, which never constructs that variant.
    DeviceLost,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn draw_once(
    surface: &mut gxi::Surface,
    gpu: &WindowGpu,
    surface_size: (u32, u32),
    snapshot_state: Option<&SnapshotState>,
    // Which overlay feature passes draw this frame (decided CPU-side in
    // `update_uniforms`): a hidden feature costs no GPU time at all.
    overlay: OverlayVisibility,
    peek_bind_group: Option<&gxi::BindGroup>,
    // Peek-aware crosshair bind group for frames where a peek quad draws
    // (the thin cross's contrast tracks the peek composite); `None` uses
    // the snapshot-only fallback in `SnapshotState`.
    crosshair_peek_bg: Option<&gxi::BindGroup>,
    // `None` while the deferred UI build is still in flight (see
    // `WindowGpu::peek`): the frame is then the desktop pass alone.
    mut ui_renderer: Option<&mut UiRenderer>,
    perf: &PerfTracker,
    gpu_timing: Option<&gxi::GpuTimings>,
    out_sample: &mut Option<PerfSample>,
) -> DrawStatus {
    let t_start = Instant::now();
    let mut frame = match surface.acquire(gpu_timing) {
        AcquireResult::Frame(f) => f,
        // Transient miss; the next iteration retries. The backend's
        // degraded paths carry their own pacing sleeps (d3d11) or a
        // bounded internal wait (metal's nextDrawable), so no extra
        // sleep here.
        AcquireResult::Skip => return DrawStatus::Continue,
        // Occluded is different: the metal backend's occlusion guard
        // returns WITHOUT any wait (it must — frame 0's show-gate
        // choreography retries it on a 1 ms cadence), so retrying
        // straight away from the steady-state loop would spin this
        // worker's core at 100 % for as long as the overlay window
        // stays occluded. Sleep the same 10 ms the d3d11 backend uses
        // on its own degraded paths; visibility transitions are
        // compositor events, so 10 ms costs at most a frame.
        AcquireResult::Occluded => {
            std::thread::sleep(Duration::from_millis(10));
            return DrawStatus::Continue;
        }
        AcquireResult::DeviceLost => return DrawStatus::DeviceLost,
    };
    // The sample's wait bucket is the swapchain acquire alone (the vsync
    // block); the encoder/render-pass setup `acquire` also did lands in
    // the draw bucket below, matching the pre-gxi bucketing.
    let wait = frame.acquire_wait();

    if let Some(ui) = ui_renderer.as_mut() {
        ui.prepare(&gpu.device, &gpu.queue, surface_size, perf);
    }

    // Pass order is the painter's stack, background to front: desktop
    // (snapshot + cursor + region fade) → peek (the hovered window's
    // contents inside the selection) → selection border + handles →
    // crosshair → UI chrome. Peek draws before the border/crosshair so
    // it needs no knowledge of either — they simply paint over it.
    frame.set_pipeline(&gpu.pipeline);
    if let Some(state) = snapshot_state {
        frame.set_bind_group(0, &state.bind_group);
        frame.draw(0..3, 0..1);
    }
    if let (Some(peek_bg), Some(peek)) = (peek_bind_group, gpu.peek.as_ref()) {
        frame.set_pipeline(&peek.pipeline);
        frame.set_bind_group(0, peek_bg);
        frame.draw(0..6, 0..1);
    }
    if let Some(state) = snapshot_state {
        // `selection` is None while the deferred build is in flight —
        // the border appears when it lands, same policy as peek.
        if let (true, Some(selection)) = (overlay.selection, gpu.selection.as_ref()) {
            frame.set_pipeline(selection);
            frame.set_bind_group(0, &state.selection_bind_group);
            frame.draw(0..SELECTION_VERTICES, 0..1);
        }
        if overlay.crosshair {
            frame.set_pipeline(&gpu.crosshair);
            frame.set_bind_group(0, crosshair_peek_bg.unwrap_or(&state.crosshair_bind_group));
            frame.draw(0..CROSSHAIR_VERTICES, 0..1);
        }
    }
    if let Some(ui) = ui_renderer.as_deref() {
        ui.draw(&mut frame);
    }

    // `present` submits and presents; it hands back the time spent in the
    // final compositor handoff so the sample's draw/present split matches
    // the pre-gxi bucketing (submit counts as draw work).
    let present = frame.present(gpu_timing);
    let draw = t_start
        .elapsed()
        .saturating_sub(wait)
        .saturating_sub(present);

    *out_sample = Some(PerfSample {
        wait,
        draw,
        present,
        overall: Duration::ZERO,
        gpu: None,
    });
    DrawStatus::Continue
}
