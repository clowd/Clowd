//! GPU-side frame timing via wgpu `TIMESTAMP_QUERY`.
//!
//! Per render thread. Maintains a ring of independent slots so up to
//! `RING_SIZE` frames can be in flight on the GPU before we need to stall
//! for a readback. Each slot owns 4 timestamp indices (desktop begin/end,
//! ui begin/end) inside a shared `QuerySet`, a region of a shared resolve
//! buffer, and a dedicated `MAP_READ`-usable readback buffer.
//!
//! Use:
//!   1. `GpuTimings::new` — returns `None` when the device didn't grant
//!      `TIMESTAMP_QUERY`. Callers treat `None` as "no GPU timing
//!      available" and render an `n/a` row in the panel.
//!   2. `poll_completed` — called at the start of each frame to drain any
//!      readback buffers whose mapping has landed. Each returned duration
//!      is handed to `PerfTracker::backfill_next_gpu`.
//!   3. `begin_frame` — picks a free slot and returns
//!      `(desktop_writes, ui_writes, FrameSlotId)` to plug into the two
//!      `RenderPassDescriptor`s. Returns `None` when every slot is
//!      still awaiting GPU work (a stall would be worse than skipping
//!      one frame of GPU timing).
//!   4. `finish_frame` — appends a `resolve_query_set` + a
//!      `copy_buffer_to_buffer` onto the encoder, then issues the
//!      `map_async` for the slot (called after `queue.submit`).

use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::Arc;
use std::time::Duration;

use wgpu::RenderPassTimestampWrites;

/// Master switch. When `false`:
///   * `GpuTimings::new` returns `None` (so nothing is constructed, no
///     `QuerySet`, no resolve buffer, no readback buffers, no per-frame
///     `resolve_query_set` / `copy_buffer_to_buffer` in the encoder,
///     and no `map_async` or `device.poll(Poll)` path).
///   * `gpu.rs` skips requesting `Features::TIMESTAMP_QUERY` on the
///     device — some backends instrument the queue differently when
///     the feature is enabled even if unused.
pub const GPU_TIMING_ENABLED: bool = true;

const SLOTS_PER_FRAME: u32 = 4;
/// Number of frames we're willing to have in flight before skipping a
/// measurement. 3 comfortably covers the present queue depth on DX12.
const RING_SIZE: usize = 3;
const QUERIES_TOTAL: u32 = SLOTS_PER_FRAME * RING_SIZE as u32;
const QUERY_SIZE_BYTES: u64 = 8;
/// Bytes each slot actually uses (4 × u64 = 32).
const SLOT_USEFUL_BYTES: u64 = QUERY_SIZE_BYTES * SLOTS_PER_FRAME as u64;
/// Stride between slots in the resolve buffer. `resolve_query_set`
/// requires destination offsets to be multiples of
/// `QUERY_RESOLVE_BUFFER_ALIGNMENT` (256 on every wgpu backend as of
/// today), so we waste a little space to keep the slot layout aligned.
const SLOT_STRIDE_BYTES: u64 = 256;

#[derive(Clone, Copy)]
pub struct FrameSlotId(usize);

const SLOT_IDLE: u8 = 0;
const SLOT_IN_FLIGHT: u8 = 1;
const SLOT_MAP_PENDING: u8 = 2;
const SLOT_READY: u8 = 3;

struct Slot {
    state: Arc<AtomicU8>,
    readback: wgpu::Buffer,
}

pub struct GpuTimings {
    query_set: wgpu::QuerySet,
    resolve: wgpu::Buffer,
    slots: Vec<Slot>,
    /// Nanoseconds per GPU tick, captured once at construction.
    period_ns: f64,
}

impl GpuTimings {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Option<Self> {
        if !GPU_TIMING_ENABLED {
            return None;
        }
        if !device.features().contains(wgpu::Features::TIMESTAMP_QUERY) {
            return None;
        }
        let query_set = device.create_query_set(&wgpu::QuerySetDescriptor {
            label: Some("gpu_timing query_set"),
            ty: wgpu::QueryType::Timestamp,
            count: QUERIES_TOTAL,
        });
        let resolve = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("gpu_timing resolve"),
            size: SLOT_STRIDE_BYTES * RING_SIZE as u64,
            usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let slots = (0..RING_SIZE)
            .map(|i| Slot {
                state: Arc::new(AtomicU8::new(SLOT_IDLE)),
                readback: device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some(&format!("gpu_timing readback {}", i)),
                    size: SLOT_USEFUL_BYTES,
                    usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                    mapped_at_creation: false,
                }),
            })
            .collect();
        Some(Self {
            query_set,
            resolve,
            slots,
            period_ns: queue.get_timestamp_period() as f64,
        })
    }

    /// Drain any slots whose map callbacks have fired. Returns durations
    /// in the same order the slots originally used by `begin_frame` (FIFO
    /// because we hand out and complete slots in ring order). Callers
    /// feed each duration to `PerfTracker::backfill_next_gpu`.
    pub fn poll_completed(&mut self) -> Vec<Duration> {
        let mut out = Vec::new();
        for slot in self.slots.iter_mut() {
            if slot.state.load(Ordering::Acquire) == SLOT_READY {
                let data = slot.readback.slice(..).get_mapped_range();
                let raw: &[u64] = bytemuck::cast_slice(&data);
                if raw.len() >= 4 {
                    let desktop_ticks = raw[1].saturating_sub(raw[0]);
                    let ui_ticks = raw[3].saturating_sub(raw[2]);
                    let total_ticks = desktop_ticks + ui_ticks;
                    let ns = total_ticks as f64 * self.period_ns;
                    out.push(Duration::from_nanos(ns as u64));
                }
                drop(data);
                slot.readback.unmap();
                slot.state.store(SLOT_IDLE, Ordering::Release);
            }
        }
        out
    }

    /// Reserve a slot for this frame. Returns writes for the two render
    /// passes and a slot id to thread through to `finish_frame`. Returns
    /// `None` when every slot is busy; the caller then omits timestamp
    /// writes for this frame and the GPU duration simply won't be
    /// recorded.
    pub fn begin_frame(&self) -> Option<BeginFrame<'_>> {
        let slot_idx = self
            .slots
            .iter()
            .position(|s| s.state.load(Ordering::Acquire) == SLOT_IDLE)?;
        let base = slot_idx as u32 * SLOTS_PER_FRAME;
        Some(BeginFrame {
            id: FrameSlotId(slot_idx),
            desktop: RenderPassTimestampWrites {
                query_set: &self.query_set,
                beginning_of_pass_write_index: Some(base),
                end_of_pass_write_index: Some(base + 1),
            },
            ui: RenderPassTimestampWrites {
                query_set: &self.query_set,
                beginning_of_pass_write_index: Some(base + 2),
                end_of_pass_write_index: Some(base + 3),
            },
        })
    }

    /// After the caller has finished encoding both passes but before
    /// `queue.submit`: resolve the slot's queries into the shared resolve
    /// buffer and copy them into the slot's readback buffer. Marks the
    /// slot in flight. `map_async` runs after submit; caller must then
    /// call `after_submit` with the same id.
    pub fn resolve(&self, encoder: &mut wgpu::CommandEncoder, id: FrameSlotId) {
        let slot_idx = id.0;
        let base = slot_idx as u32 * SLOTS_PER_FRAME;
        let byte_offset = slot_idx as u64 * SLOT_STRIDE_BYTES;
        encoder.resolve_query_set(
            &self.query_set,
            base..base + SLOTS_PER_FRAME,
            &self.resolve,
            byte_offset,
        );
        encoder.copy_buffer_to_buffer(
            &self.resolve,
            byte_offset,
            &self.slots[slot_idx].readback,
            0,
            SLOT_USEFUL_BYTES,
        );
        self.slots[slot_idx]
            .state
            .store(SLOT_IN_FLIGHT, Ordering::Release);
    }

    /// After `queue.submit`: kick off the async mapping so `poll_completed`
    /// can pick up the result once the GPU work finishes.
    pub fn after_submit(&self, id: FrameSlotId) {
        let slot = &self.slots[id.0];
        slot.state.store(SLOT_MAP_PENDING, Ordering::Release);
        let state = slot.state.clone();
        slot.readback
            .slice(..)
            .map_async(wgpu::MapMode::Read, move |r| {
                if r.is_ok() {
                    state.store(SLOT_READY, Ordering::Release);
                } else {
                    state.store(SLOT_IDLE, Ordering::Release);
                }
            });
    }
}

pub struct BeginFrame<'a> {
    pub id: FrameSlotId,
    pub desktop: RenderPassTimestampWrites<'a>,
    pub ui: RenderPassTimestampWrites<'a>,
}
