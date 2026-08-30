use std::time::{Duration, Instant};

use crate::gpu::WindowGpu;
use crate::gxi::{self, AcquireResult};
use crate::render::desktop::SnapshotState;
use crate::telemetry::perf::{PerfSample, PerfTracker};
use crate::ui::gpu::UiRenderer;

/// What [`draw_once`] wants the render loop to do next.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum DrawStatus {
    /// Frame drawn, or a transient miss (timeout / occluded / swapchain
    /// reconfigured) — the next iteration retries.
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
    peek_bind_group: Option<&gxi::BindGroup>,
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
        // A lost/outdated surface has already been reconfigured by the
        // backend; like the transient skips, the next iteration retries.
        AcquireResult::Skip | AcquireResult::Occluded | AcquireResult::Reconfigured => return DrawStatus::Continue,
        AcquireResult::DeviceLost => return DrawStatus::DeviceLost,
    };
    // The sample's wait bucket is the swapchain acquire alone (the vsync
    // block); the encoder/render-pass setup `acquire` also did lands in
    // the draw bucket below, matching the pre-gxi bucketing.
    let wait = frame.acquire_wait();

    if let Some(ui) = ui_renderer.as_mut() {
        ui.prepare(&gpu.device, &gpu.queue, surface_size, perf);
    }

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
