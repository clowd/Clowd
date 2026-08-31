//! GPU-side frame timing via D3D11 timestamp queries. Always on: every
//! render worker constructs one.
//!
//! Per render thread. Maintains a small ring of query triples - one
//! `D3D11_QUERY_TIMESTAMP_DISJOINT` bracketing a start and an end
//! `D3D11_QUERY_TIMESTAMP` - so up to [`RING_SIZE`] frames can be in
//! flight on the GPU before a measurement has to be skipped (never
//! stalled for: an unmeasured frame beats a pipeline bubble).
//!
//! Use:
//!   1. [`GpuTimings::new`] - returns `None` when query creation fails.
//!      Callers treat `None` as "no GPU timing available" (the debug
//!      panel renders `n/a`).
//!   2. `Surface::acquire` calls [`GpuTimings::begin_frame`] under the
//!      context lock (begin disjoint + start timestamp) and threads the
//!      returned slot id through `Frame`; `Frame::present` calls
//!      [`GpuTimings::end_frame`] (end timestamp + end disjoint) right
//!      before `Present`.
//!   3. [`GpuTimings::poll_completed`] - called at the start of each
//!      frame to drain the slots whose query data has landed
//!      (non-blocking `GetData`). Each returned duration is handed to
//!      `PerfTracker::backfill_next_gpu`.

use std::sync::atomic::{AtomicU8, Ordering};
use std::time::Duration;

use windows::Win32::Graphics::Direct3D11::{
    ID3D11Query, D3D11_ASYNC_GETDATA_DONOTFLUSH, D3D11_QUERY, D3D11_QUERY_DATA_TIMESTAMP_DISJOINT, D3D11_QUERY_DESC, D3D11_QUERY_TIMESTAMP,
    D3D11_QUERY_TIMESTAMP_DISJOINT,
};

use super::device::{ContextCell, Device, Queue};

/// Frames allowed in flight before a measurement is skipped. 3 covers the
/// swapchain's queue depth (frame latency 1 plus the frame the GPU is
/// executing and the one being recorded) with room to spare.
const RING_SIZE: usize = 3;

const SLOT_IDLE: u8 = 0;
/// `begin_frame` issued; waiting for `end_frame`.
const SLOT_OPEN: u8 = 1;
/// `end_frame` issued; waiting for `poll_completed` to read the data.
const SLOT_PENDING: u8 = 2;

/// One ring slot, handed out by `begin_frame` and carried inside `Frame`.
#[derive(Clone, Copy)]
pub(super) struct FrameSlotId(usize);

struct Slot {
    state: AtomicU8,
    disjoint: ID3D11Query,
    start: ID3D11Query,
    end: ID3D11Query,
}

pub struct GpuTimings {
    slots: Vec<Slot>,
}

// SAFETY: the `ID3D11Query` objects are device children with no thread
// affinity of their own; the operations that drive them (Begin/End/
// GetData) are immediate-context methods, and every one of those calls
// goes through the context mutex (`Queue::lock` / `Device::lock_ctx`)
// like all other context touches in this backend. COM refcounting is
// atomic. The slot states are atomics. `Sync` is claimed for parity with
// the metal backend's `GpuTimings` (whose `Arc<Mutex<..>>` derives it).
unsafe impl Send for GpuTimings {}
unsafe impl Sync for GpuTimings {}

impl GpuTimings {
    /// `None` when the ring's queries cannot be created (logged;
    /// timestamp queries exist on every FL >= 10_0 device, so this is
    /// defensive).
    pub fn new(device: &Device, queue: &Queue) -> Option<Self> {
        let _ = queue;
        let mut slots = Vec::with_capacity(RING_SIZE);
        for _ in 0..RING_SIZE {
            slots.push(Slot {
                state: AtomicU8::new(SLOT_IDLE),
                disjoint: create_query(device, D3D11_QUERY_TIMESTAMP_DISJOINT)?,
                start: create_query(device, D3D11_QUERY_TIMESTAMP)?,
                end: create_query(device, D3D11_QUERY_TIMESTAMP)?,
            });
        }
        Some(Self {
            slots,
        })
    }

    /// Drain the slots whose query data has landed. Non-blocking: a slot
    /// whose GPU work has not retired yet (`GetData` returns `S_FALSE`)
    /// stays pending for the next poll. Frames the driver flags as
    /// disjoint (clock rate changed mid-frame) recycle their slot without
    /// emitting a sample. Callers feed each duration to
    /// `PerfTracker::backfill_next_gpu`.
    pub fn poll_completed(&mut self, device: &Device) -> Vec<Duration> {
        let mut out = Vec::new();
        for slot in &self.slots {
            if slot.state.load(Ordering::Acquire) != SLOT_PENDING {
                continue;
            }
            // The disjoint query is polled first: its End was issued last,
            // so its data landing means the whole triple has retired.
            // windows-rs folds S_FALSE into `Ok` with the output left
            // untouched, so - like `wait_idle` - the written value, not
            // the HRESULT, is the completion signal (a real frequency is
            // never 0).
            let mut dis = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
            let hr = {
                let ctx = device.lock_ctx();
                unsafe {
                    ctx.0.GetData(
                        &slot.disjoint,
                        Some(&mut dis as *mut _ as *mut core::ffi::c_void),
                        std::mem::size_of::<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>() as u32,
                        D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
                    )
                }
            };
            match hr {
                Err(e) => {
                    // Recycle rather than wedge the slot as permanently
                    // pending; a removed device is reported by acquire.
                    warn!("gpu timing: disjoint GetData failed: {e}");
                    slot.state
                        .store(SLOT_IDLE, Ordering::Release);
                    continue;
                }
                Ok(()) if dis.Frequency == 0 => continue, // not ready yet
                Ok(()) => {}
            }

            let start = read_timestamp(device, &slot.start);
            let end = read_timestamp(device, &slot.end);
            slot.state
                .store(SLOT_IDLE, Ordering::Release);
            if dis.Disjoint.as_bool() {
                continue;
            }
            // 0 means the timestamp never landed even though the disjoint
            // bracket retired (or its read failed) - drop the sample.
            let (Some(start), Some(end)) = (start, end) else {
                continue;
            };
            let ticks = end.saturating_sub(start);
            let ns = ticks as f64 * 1e9 / dis.Frequency as f64;
            out.push(Duration::from_nanos(ns as u64));
        }
        out
    }

    /// Reserve a slot and issue its begin-of-frame queries: begin the
    /// disjoint bracket, then the start timestamp (timestamp queries are
    /// End-only). Returns `None` when every slot is busy; the frame then
    /// simply goes unmeasured. `ctx` is the already-held context lock -
    /// `Surface::acquire` calls this inside its per-frame setup block.
    pub(super) fn begin_frame(&self, ctx: &ContextCell) -> Option<FrameSlotId> {
        let idx = self
            .slots
            .iter()
            .position(|s| s.state.load(Ordering::Acquire) == SLOT_IDLE)?;
        let slot = &self.slots[idx];
        // SAFETY: the queries are alive for `self`'s lifetime and the
        // caller holds the context mutex.
        unsafe {
            ctx.0.Begin(&slot.disjoint);
            ctx.0.End(&slot.start);
        }
        slot.state
            .store(SLOT_OPEN, Ordering::Release);
        Some(FrameSlotId(idx))
    }

    /// Issue the end-of-frame queries: end timestamp, then close the
    /// disjoint bracket. `Frame::present` calls this under the context
    /// lock right before `Present`.
    pub(super) fn end_frame(&self, ctx: &ContextCell, id: FrameSlotId) {
        let slot = &self.slots[id.0];
        // SAFETY: as in `begin_frame`.
        unsafe {
            ctx.0.End(&slot.end);
            ctx.0.End(&slot.disjoint);
        }
        slot.state
            .store(SLOT_PENDING, Ordering::Release);
    }
}

fn create_query(device: &Device, kind: D3D11_QUERY) -> Option<ID3D11Query> {
    let desc = D3D11_QUERY_DESC {
        Query: kind,
        MiscFlags: 0,
    };
    let mut query: Option<ID3D11Query> = None;
    if let Err(e) = unsafe {
        device
            .raw()
            .CreateQuery(&desc, Some(&mut query))
    } {
        warn!("gpu timing: CreateQuery({kind:?}) failed ({e}); GPU column will read n/a");
        return None;
    }
    query
}

/// Read one retired timestamp. `None` on failure or if the value never
/// landed (0 is not a plausible GPU tick count).
fn read_timestamp(device: &Device, query: &ID3D11Query) -> Option<u64> {
    let mut ticks: u64 = 0;
    let hr = {
        let ctx = device.lock_ctx();
        unsafe {
            ctx.0.GetData(
                query,
                Some(&mut ticks as *mut u64 as *mut core::ffi::c_void),
                8,
                D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
            )
        }
    };
    if let Err(e) = hr {
        warn!("gpu timing: timestamp GetData failed: {e}");
        return None;
    }
    (ticks != 0).then_some(ticks)
}
