//! GPU-side frame timing via wgpu `TIMESTAMP_QUERY`. Always on: every
//! render worker constructs one.
//!
//! Per render thread. Maintains a ring of independent slots so up to
//! [`RING_SIZE`] frames can be in flight on the GPU before a measurement
//! has to be skipped (never stalled for: an unmeasured frame beats a
//! pipeline bubble). Each slot owns 2 timestamp indices (pass begin/end)
//! inside a shared `QuerySet`, a region of a shared resolve buffer, and a
//! dedicated `MAP_READ` readback buffer.
//!
//! Use:
//!   1. [`GpuTimings::new`] — returns `None` when the device was not
//!      granted `TIMESTAMP_QUERY` (a GL adapter without the extension,
//!      say). Callers treat `None` as "no GPU timing available" (the
//!      debug panel renders `n/a`).
//!   2. [`GpuTimings::poll_completed`] — called at the start of each frame
//!      to drain any readback buffers whose mapping has landed. Each
//!      returned duration is handed to `PerfTracker::backfill_next_gpu`.
//!   3. The per-frame begin/resolve/map choreography is backend-internal:
//!      `Surface::acquire` reserves a slot ([`GpuTimings::begin_frame`])
//!      and threads the id through `Frame`; `Frame::present` resolves
//!      the queries into the slot's readback buffer and starts the map.

use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::Arc;
use std::time::Duration;

use wgpu::RenderPassTimestampWrites;

use super::device::{Device, Queue};

const SLOTS_PER_FRAME: u32 = 2;
/// Frames allowed in flight before a measurement is skipped. 3 covers the
/// swapchain's queue depth (frame latency 1 plus the frame the GPU is
/// executing and the one being recorded) with room to spare.
const RING_SIZE: usize = 3;
const QUERIES_TOTAL: u32 = SLOTS_PER_FRAME * RING_SIZE as u32;
const QUERY_SIZE_BYTES: u64 = 8;
/// Bytes each slot actually uses (2 × u64 = 16).
const SLOT_USEFUL_BYTES: u64 = QUERY_SIZE_BYTES * SLOTS_PER_FRAME as u64;
/// Stride between slots in the resolve buffer. `resolve_query_set`
/// requires destination offsets to be multiples of
/// `QUERY_RESOLVE_BUFFER_ALIGNMENT` (256 on every wgpu backend), so a
/// little space is wasted to keep the slot layout aligned.
const SLOT_STRIDE_BYTES: u64 = wgpu::QUERY_RESOLVE_BUFFER_ALIGNMENT;

/// One ring slot, handed out by `begin_frame` and carried inside `Frame`.
#[derive(Clone, Copy)]
pub(super) struct FrameSlotId(usize);

const SLOT_IDLE: u8 = 0;
/// `resolve` issued; the frame is submitted or about to be.
const SLOT_IN_FLIGHT: u8 = 1;
/// `after_submit` issued; waiting for the map callback.
const SLOT_MAP_PENDING: u8 = 2;
/// Mapped; waiting for `poll_completed` to read the data.
const SLOT_READY: u8 = 3;

struct Slot {
    /// `Arc` because the map callback captures its own clone and may fire
    /// after a dropped `GpuTimings`.
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
    /// `None` when the device was not granted `TIMESTAMP_QUERY`
    /// (`Device::create` requests it whenever the adapter offers it).
    pub fn new(device: &Device, queue: &Queue) -> Option<Self> {
        if !device
            .raw()
            .features()
            .contains(wgpu::Features::TIMESTAMP_QUERY)
        {
            warn!("gpu timing: adapter has no TIMESTAMP_QUERY; GPU column will read n/a");
            return None;
        }
        let query_set = device
            .raw()
            .create_query_set(&wgpu::QuerySetDescriptor {
                label: Some("gpu_timing query_set"),
                ty: wgpu::QueryType::Timestamp,
                count: QUERIES_TOTAL,
            });
        let resolve = device
            .raw()
            .create_buffer(&wgpu::BufferDescriptor {
                label: Some("gpu_timing resolve"),
                size: SLOT_STRIDE_BYTES * RING_SIZE as u64,
                usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: false,
            });
        let slots = (0..RING_SIZE)
            .map(|i| Slot {
                state: Arc::new(AtomicU8::new(SLOT_IDLE)),
                readback: device
                    .raw()
                    .create_buffer(&wgpu::BufferDescriptor {
                        label: Some(&format!("gpu_timing readback {i}")),
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
            period_ns: queue.raw().get_timestamp_period() as f64,
        })
    }

    /// Drive the device's callback processing and drain any slots whose
    /// map callbacks have fired. Returns durations in the order the slots
    /// were handed out by `begin_frame` (FIFO, because slots are handed
    /// out and completed in ring order). Callers feed each duration to
    /// `PerfTracker::backfill_next_gpu`.
    pub fn poll_completed(&mut self, device: &Device) -> Vec<Duration> {
        // Non-blocking poll so `map_async` callbacks get a chance to fire.
        if let Err(e) = device.raw().poll(wgpu::PollType::Poll) {
            warn!("gpu timing: device poll failed: {e}");
        }

        let mut out = Vec::new();
        for slot in &self.slots {
            if slot.state.load(Ordering::Acquire) != SLOT_READY {
                continue;
            }
            // The map callback has fired, so the range is mapped; if it
            // somehow isn't, drop this sample and recycle the slot anyway
            // rather than wedging it as permanently busy.
            match slot.readback.slice(..).get_mapped_range() {
                Ok(data) => {
                    let ticks = |i: usize| {
                        let bytes: [u8; 8] = data[i * 8..(i + 1) * 8]
                            .try_into()
                            .expect("8-byte timestamp slice");
                        u64::from_le_bytes(bytes)
                    };
                    let pass_ticks = ticks(1).saturating_sub(ticks(0));
                    let ns = pass_ticks as f64 * self.period_ns;
                    out.push(Duration::from_nanos(ns as u64));
                }
                Err(e) => warn!("gpu timing: readback not mapped: {e}"),
            }
            slot.readback.unmap();
            slot.state
                .store(SLOT_IDLE, Ordering::Release);
        }
        out
    }

    /// Reserve a slot for this frame. Returns the timestamp writes for
    /// the frame's render pass and a slot id to thread through to the
    /// resolve step. Returns `None` when every slot is busy; the frame
    /// then simply goes unmeasured (a stall would be worse than skipping
    /// one sample).
    pub(super) fn begin_frame(&self) -> Option<BeginFrame<'_>> {
        let slot_idx = self
            .slots
            .iter()
            .position(|s| s.state.load(Ordering::Acquire) == SLOT_IDLE)?;
        let base = slot_idx as u32 * SLOTS_PER_FRAME;
        Some(BeginFrame {
            id: FrameSlotId(slot_idx),
            pass: RenderPassTimestampWrites {
                query_set: &self.query_set,
                beginning_of_pass_write_index: Some(base),
                end_of_pass_write_index: Some(base + 1),
            },
        })
    }

    /// After the frame's pass has ended but before `queue.submit`: resolve
    /// the slot's queries into the shared resolve buffer and copy them
    /// into the slot's readback buffer. Marks the slot in flight.
    pub(super) fn resolve(&self, encoder: &mut wgpu::CommandEncoder, id: FrameSlotId) {
        let slot_idx = id.0;
        let base = slot_idx as u32 * SLOTS_PER_FRAME;
        let byte_offset = slot_idx as u64 * SLOT_STRIDE_BYTES;
        encoder.resolve_query_set(&self.query_set, base..base + SLOTS_PER_FRAME, &self.resolve, byte_offset);
        encoder.copy_buffer_to_buffer(&self.resolve, byte_offset, &self.slots[slot_idx].readback, 0, SLOT_USEFUL_BYTES);
        self.slots[slot_idx]
            .state
            .store(SLOT_IN_FLIGHT, Ordering::Release);
    }

    /// After `queue.submit`: kick off the async mapping so
    /// [`GpuTimings::poll_completed`] can pick up the result once the GPU
    /// work finishes.
    pub(super) fn after_submit(&self, id: FrameSlotId) {
        let slot = &self.slots[id.0];
        slot.state
            .store(SLOT_MAP_PENDING, Ordering::Release);
        let state = Arc::clone(&slot.state);
        slot.readback
            .slice(..)
            .map_async(wgpu::MapMode::Read, move |r| {
                let next = if r.is_ok() { SLOT_READY } else { SLOT_IDLE };
                state.store(next, Ordering::Release);
            });
    }
}

pub(super) struct BeginFrame<'a> {
    pub id: FrameSlotId,
    pub pass: RenderPassTimestampWrites<'a>,
}
