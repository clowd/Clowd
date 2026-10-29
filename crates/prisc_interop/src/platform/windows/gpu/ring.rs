//! The Direct3D 12 copy-queue readback ring: N slots, each a BGRA render target Skia draws into,
//! a readback-heap buffer kept mapped for its life, and one copy command list recorded once and
//! re-executed every frame.
//!
//! The protocol is the one `D3D12ReadbackRing.cs` ran in C# before this move, instruction for
//! instruction, because the shipped render depends on it:
//!
//! * slot textures carry `ALLOW_SIMULTANEOUS_ACCESS`, which makes the direct-queue render and the
//!   copy-queue read legal without a single `ResourceBarrier`: such textures decay to `COMMON` at
//!   the end of every `ExecuteCommandLists` and are implicitly promoted on first use in the next.
//!   The corollary lives in C#, which rebuilds its `GRD3DTextureResourceInfo` with
//!   `ResourceState = COMMON` at every `Begin`;
//! * readback buffers are created in `COPY_DEST`, never transitioned, mapped once with a null read
//!   range and unmapped at teardown with an **empty** written range;
//! * the copy list is closed at creation and never `Reset`: the slot state machine guarantees the
//!   previous execution completed before the next one is queued;
//! * one monotonic counter feeds both submit protocols and every slot, and only advances past a
//!   `Signal` the runtime accepted, so a refused signal cannot park teardown on a value the fence
//!   will never reach. Taking the next value and queuing its signals is one critical section
//!   (`RingInner::timeline`), so two slots submitted from two threads at once never share a value
//!   and the fences are signalled in value order;
//! * every CPU wait on the consumed fence runs under one mutex on one auto-reset event and
//!   re-checks the fence after each wake. `abandon_shared`'s read-then-signal takes that mutex too.
//!
//! What is new: the fence timeout is a per-call `timeout_ms` (an expired wait answers
//! `PRISC_S_TIMEOUT` and leaves the handle usable), and creation ends with a self-check (critique
//! F22): slot 0 is cleared to a known colour on the direct queue, read back through the copy path
//! and compared, and a mismatch is `PRISC_E_CAPABILITY`, so the caller falls back to the
//! synchronous ring. The clear list is the converter's self-check's too.

use std::ffi::c_void;
use std::mem::ManuallyDrop;
use std::ptr;
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use windows::Win32::Graphics::Direct3D12::{
    ID3D12CommandAllocator, ID3D12CommandList, ID3D12CommandQueue, ID3D12DescriptorHeap, ID3D12Device, ID3D12Fence,
    ID3D12GraphicsCommandList, ID3D12Resource, D3D12_COMMAND_LIST_TYPE_COPY, D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_COMMAND_QUEUE_DESC,
    D3D12_COMMAND_QUEUE_FLAG_NONE, D3D12_COMMAND_QUEUE_PRIORITY_NORMAL, D3D12_CPU_PAGE_PROPERTY_UNKNOWN, D3D12_DESCRIPTOR_HEAP_DESC,
    D3D12_DESCRIPTOR_HEAP_FLAG_NONE, D3D12_DESCRIPTOR_HEAP_TYPE_RTV, D3D12_FENCE_FLAGS, D3D12_FENCE_FLAG_NONE, D3D12_FENCE_FLAG_SHARED,
    D3D12_HEAP_FLAGS, D3D12_HEAP_FLAG_NONE, D3D12_HEAP_FLAG_SHARED, D3D12_HEAP_PROPERTIES, D3D12_HEAP_TYPE, D3D12_HEAP_TYPE_DEFAULT,
    D3D12_HEAP_TYPE_READBACK, D3D12_MEMORY_POOL_UNKNOWN, D3D12_PLACED_SUBRESOURCE_FOOTPRINT, D3D12_RANGE, D3D12_RESOURCE_BARRIER,
    D3D12_RESOURCE_BARRIER_0, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_BARRIER_FLAG_NONE,
    D3D12_RESOURCE_BARRIER_TYPE_TRANSITION, D3D12_RESOURCE_DESC, D3D12_RESOURCE_DIMENSION_BUFFER, D3D12_RESOURCE_DIMENSION_TEXTURE2D,
    D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET, D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS, D3D12_RESOURCE_STATES,
    D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_TRANSITION_BARRIER,
    D3D12_TEXTURE_COPY_LOCATION, D3D12_TEXTURE_COPY_LOCATION_0, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
    D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_TEXTURE_LAYOUT_UNKNOWN,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC};
use windows_core::{Interface, HRESULT};

use crate::error::{Error, Result};
use crate::ffi::gpu::PriscGpuRingInfo;
use crate::ffi::status::{PRISC_OK, PRISC_S_TIMEOUT};
use crate::platform::windows::gpu::device::DeviceInner;
use crate::platform::windows::gpu::{wait_fence, Event, FenceWait, FreeThreaded};
use crate::platform::windows::{hresult_error, os_error};
use crate::util;

/// The slot state machine: `Idle -> Composing -> InFlight -> Idle`, every transition a
/// compare-and-swap, every violation a caller bug reported as `PRISC_E_STATE`.
const SLOT_IDLE: u32 = 0;
const SLOT_COMPOSING: u32 = 1;
const SLOT_IN_FLIGHT: u32 = 2;

/// An EMPTY written range, not a null one: the CPU only reads a readback heap.
const EMPTY_WRITTEN_RANGE: D3D12_RANGE = D3D12_RANGE {
    Begin: 0,
    End: 0,
};

/// What the ring's destroy allows its own drain: the C# ring's 30 seconds. A GPU that takes that
/// long to finish a queued copy is hung, and device removal signals fences to `u64::MAX`.
const DESTROY_DRAIN_TIMEOUT_MS: u32 = 30_000;

/// The bound on each fence wait of the self-checks (ring create, converter self-check).
pub(crate) const SELF_CHECK_TIMEOUT_MS: u32 = 30_000;

/// The ring self-check's colour, `R, G, B` = 11 22 33, so B8G8R8A8 memory reads 33 22 11 FF: the
/// value the C# smoke test used, so a byte-order mistake shows up the same way.
const SELF_CHECK_RGB: [u8; 3] = [0x11, 0x22, 0x33];

/// How a shared slot whose consumer failed was handed back (`RingInner::recover_shared_slot`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum SlotRecovery {
    /// The consumer's signal was queued and landed in time: the slot was released normally.
    Released,
    /// Nothing will signal the slot (or not in time): the CPU signalled the consumed fence.
    Abandoned,
}

/// Faults the tests inject into the creation self-check, per thread. Outside `#[cfg(test)]` there
/// is no way to set one, and `injected` is a constant false.
pub(crate) mod fault {
    /// Compare the readback against a colour other than the one cleared.
    pub(crate) const MISMATCH: u32 = 1;
    /// Answer the self-check's wait as if the copy never landed.
    pub(crate) const TIMEOUT: u32 = 2;

    #[cfg(test)]
    thread_local! {
        pub(crate) static SELF_CHECK: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
    }
}

/// Whether the test running on this thread injected `kind` into the creation self-check.
#[cfg(test)]
fn injected(kind: u32) -> bool {
    fault::SELF_CHECK.with(std::cell::Cell::get) == kind
}

#[cfg(not(test))]
const fn injected(_kind: u32) -> bool {
    false
}

/// One slot: the render target, the readback buffer its pixels land in, and its one copy.
struct Slot {
    /// The BGRA render target, `DEFAULT` heap, created in `COMMON`. Lent to C# by `begin` as a raw
    /// `ID3D12Resource*` so Skia can wrap it; the same pointer every frame.
    texture: FreeThreaded<ID3D12Resource>,
    /// The `READBACK` heap buffer, created in `COPY_DEST` and left there.
    readback: FreeThreaded<ID3D12Resource>,
    /// Never reset; held so the recorded list keeps its backing store.
    _allocator: FreeThreaded<ID3D12CommandAllocator>,
    copy_list: FreeThreaded<ID3D12CommandList>,
    /// The mapped readback memory, or null once unmapped (exactly once, by teardown or drop).
    mapped: AtomicPtr<u8>,
    state: AtomicU32,
    /// Whether the in-flight submission was `submit_shared`. Written before the state transition
    /// publishes the slot, so a waiter that sees `InFlight` never reads a stale flag.
    shared: AtomicBool,
    /// The consumed-fence value that means "the pixels landed" or "the consumer is done".
    fence_value: AtomicU64,
}

/// The direct-queue clear of slot 0, the stand-in for Skia both self-checks draw with.
struct ClearList {
    /// Slot 0's texture, for the transition barrier the clear records.
    target: FreeThreaded<ID3D12Resource>,
    heap: FreeThreaded<ID3D12DescriptorHeap>,
    allocator: FreeThreaded<ID3D12CommandAllocator>,
    list: FreeThreaded<ID3D12GraphicsCommandList>,
    /// The render-fence value at which the last recorded clear is known complete (the value of the
    /// submit that must follow it); 0 when none was recorded. Reset is only legal after that.
    pending: u64,
    event: Event,
}

/// Everything a readback ring owns, behind an `Arc` so a converter built on it keeps the fences
/// and slot textures alive after the ring handle is destroyed.
pub(crate) struct RingInner {
    /// The `ring -> device` edge: the `ID3D12Device` and the direct queue stay alive with the ring.
    device: Arc<DeviceInner>,
    copy_queue: FreeThreaded<ID3D12CommandQueue>,
    render_fence: FreeThreaded<ID3D12Fence>,
    consumed_fence: FreeThreaded<ID3D12Fence>,
    slots: Vec<Slot>,
    width: u32,
    height: u32,
    row_pitch: u32,
    shareable: bool,
    /// One monotonic counter across both protocols and every slot.
    last_signaled: AtomicU64,
    /// Held from reading `last_signaled` to storing the value it gave out, across every `Signal`,
    /// `Wait` and `ExecuteCommandLists` a submit queues (and around the self-check clear's
    /// execution and its `pending` value), so the submit exports are safe from any thread.
    timeline: Mutex<()>,
    /// The event every consumed-fence wait uses; `None` once teardown closed it.
    wait: Mutex<Option<Event>>,
    clear: Mutex<ClearList>,
}

impl RingInner {
    pub(crate) fn info(&self) -> PriscGpuRingInfo {
        PriscGpuRingInfo {
            width: self.width,
            height: self.height,
            slots: self.slots.len() as u32,
            row_pitch: self.row_pitch,
            shareable: util::to_flag(self.shareable),
            _reserved: [0; 7],
        }
    }

    pub(crate) fn device(&self) -> &Arc<DeviceInner> {
        &self.device
    }

    pub(crate) fn render_fence(&self) -> &ID3D12Fence {
        self.render_fence.get()
    }

    pub(crate) fn consumed_fence(&self) -> &ID3D12Fence {
        self.consumed_fence.get()
    }

    /// One slot's BGRA render target, for the converter's `CreateSharedHandle`.
    pub(crate) fn slot_texture(&self, index: u32) -> Result<&ID3D12Resource> {
        Ok(self.slot(index)?.texture.get())
    }

    #[cfg(test)]
    pub(crate) fn last_signaled(&self) -> u64 {
        self.last_signaled.load(Ordering::Acquire)
    }

    fn slot(&self, index: u32) -> Result<&Slot> {
        self.slots
            .get(index as usize)
            .ok_or_else(|| {
                Error::invalid_arg(format!(
                    "readback slot {index} is out of range for a ring with {} slots",
                    self.slots.len()
                ))
            })
    }

    /// `Idle -> Composing`, lending the slot's `ID3D12Resource*`.
    pub(crate) fn begin(&self, index: u32) -> Result<*mut c_void> {
        let slot = self.slot(index)?;
        transition(slot, index, SLOT_IDLE, SLOT_COMPOSING)?;
        Ok(slot.texture.as_raw())
    }

    /// `Composing -> Idle`, for the path where wrapping the texture for Skia failed.
    pub(crate) fn cancel_begin(&self, index: u32) -> Result<()> {
        transition(self.slot(index)?, index, SLOT_COMPOSING, SLOT_IDLE)
    }

    /// The readback submit: signal the render fence on the direct queue, make the copy queue wait
    /// for it on the GPU, execute the recorded copy, signal the consumed fence.
    pub(crate) fn submit(&self, index: u32) -> Result<u64> {
        let slot = self.slot(index)?;
        begin_submission(slot, index, false)?;

        let _timeline = self
            .timeline
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let value = self.last_signaled.load(Ordering::Acquire) + 1;
        unsafe {
            self.device
                .direct_queue()
                .Signal(self.render_fence.get(), value)
                .map_err(|e| os_error(&e, "ID3D12CommandQueue::Signal"))?;
            self.copy_queue
                .Wait(self.render_fence.get(), value)
                .map_err(|e| os_error(&e, "ID3D12CommandQueue::Wait"))?;
            self.copy_queue
                .ExecuteCommandLists(&[Some(slot.copy_list.clone_interface())]);
            self.copy_queue
                .Signal(self.consumed_fence.get(), value)
                .map_err(|e| os_error(&e, "ID3D12CommandQueue::Signal"))?;
        }
        self.last_signaled
            .store(value, Ordering::Release);
        slot.fence_value
            .store(value, Ordering::Release);
        Ok(value)
    }

    /// The zero-copy submit: the render-fence signal and nothing else. The consumer waits that
    /// value on its own timeline and signals the consumed fence with exactly it, in order.
    pub(crate) fn submit_shared(&self, index: u32) -> Result<u64> {
        let slot = self.slot(index)?;
        begin_submission(slot, index, true)?;

        let _timeline = self
            .timeline
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let value = self.last_signaled.load(Ordering::Acquire) + 1;
        unsafe {
            self.device
                .direct_queue()
                .Signal(self.render_fence.get(), value)
                .map_err(|e| os_error(&e, "ID3D12CommandQueue::Signal"))?;
        }
        self.last_signaled
            .store(value, Ordering::Release);
        slot.fence_value
            .store(value, Ordering::Release);
        Ok(value)
    }

    /// Blocks for the copy, then lends the slot's mapped readback memory and row pitch. A timeout
    /// leaves the slot in flight so the caller may wait again.
    pub(crate) fn wait(&self, index: u32, timeout_ms: u32) -> Result<Option<(*const u8, u32)>> {
        let slot = self.slot(index)?;
        check_in_flight(slot, index, false)?;
        if self.wait_consumed(slot.fence_value.load(Ordering::Acquire), timeout_ms)? == PRISC_S_TIMEOUT {
            return Ok(None);
        }
        slot.state
            .store(SLOT_IDLE, Ordering::Release);
        Ok(Some((
            slot.mapped
                .load(Ordering::Acquire)
                .cast_const(),
            self.row_pitch,
        )))
    }

    /// Blocks until the external consumer signalled the slot's value, then idles the slot.
    /// `PRISC_S_TIMEOUT` leaves it in flight.
    pub(crate) fn release_shared(&self, index: u32, timeout_ms: u32) -> Result<i32> {
        let slot = self.slot(index)?;
        check_in_flight(slot, index, true)?;
        let status = self.wait_consumed(slot.fence_value.load(Ordering::Acquire), timeout_ms)?;
        if status == PRISC_OK {
            slot.state
                .store(SLOT_IDLE, Ordering::Release);
        }
        Ok(status)
    }

    /// Idles a shared slot whose consumer failed before it could signal. The read of the consumed
    /// fence and the CPU signal that may follow are one critical section under the waits' mutex,
    /// and a fence already past the slot's value is never moved backwards.
    pub(crate) fn abandon_shared(&self, index: u32) -> Result<()> {
        let slot = self.slot(index)?;
        check_in_flight(slot, index, true)?;
        let value = slot.fence_value.load(Ordering::Acquire);
        {
            let _guard = self
                .wait
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            unsafe {
                if self.consumed_fence.GetCompletedValue() < value {
                    self.consumed_fence
                        .Signal(value)
                        .map_err(|e| os_error(&e, "ID3D12Fence::Signal"))?;
                }
            }
        }
        slot.state
            .store(SLOT_IDLE, Ordering::Release);
        Ok(())
    }

    /// Hands back a shared slot whose consumer failed: when the consumer's signal was queued
    /// (`signal_queued`), it is waited for like any frame's (the consumer may still be reading the
    /// slot) and the slot released; when nothing was queued, or the signal did not land within
    /// `timeout_ms`, the slot is abandoned. Either way the slot is idle on `Ok`.
    pub(crate) fn recover_shared_slot(&self, index: u32, signal_queued: bool, timeout_ms: u32) -> Result<SlotRecovery> {
        if signal_queued && matches!(self.release_shared(index, timeout_ms), Ok(PRISC_OK)) {
            return Ok(SlotRecovery::Released);
        }
        self.abandon_shared(index)?;
        Ok(SlotRecovery::Abandoned)
    }

    /// Blocks until the consumed fence reaches `value`: `PRISC_OK` or `PRISC_S_TIMEOUT`.
    pub(crate) fn wait_consumed(&self, value: u64, timeout_ms: u32) -> Result<i32> {
        let guard = self
            .wait
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let Some(event) = guard.as_ref() else {
            return Err(Error::state("the readback ring's wait event was closed by its destroy"));
        };
        match wait_fence(self.consumed_fence.get(), value, timeout_ms, event, || self.device_removed())? {
            FenceWait::Reached => Ok(PRISC_OK),
            FenceWait::TimedOut => Ok(PRISC_S_TIMEOUT),
        }
    }

    /// The flat "the device was removed" of the C# ring plus the reason the runtime holds.
    pub(crate) fn device_removed(&self) -> Error {
        let reason = unsafe { self.device.d3d12().GetDeviceRemovedReason() }
            .err()
            .map(|e| e.code())
            .unwrap_or(HRESULT(0));
        Error::os(
            i64::from(reason.0),
            format!("The Direct3D 12 device was removed during the render (0x{:08X}).", reason.0 as u32),
        )
    }

    /// Clears slot 0, which must be composing, to `rgb` on the direct queue: the stand-in for a
    /// Skia draw the two self-checks use. The submit that follows orders it before the copy or
    /// the consumer, exactly as a Skia flush is ordered.
    pub(crate) fn clear_slot0(&self, rgb: [u8; 3]) -> Result<()> {
        let slot = self.slot(0)?;
        let state = slot.state.load(Ordering::Acquire);
        if state != SLOT_COMPOSING {
            return Err(wrong_state(0, state, SLOT_COMPOSING));
        }
        let mut clear = self
            .clear
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        // The allocator may only be reset once the GPU finished the last list recorded from it.
        if clear.pending > 0
            && wait_fence(self.render_fence.get(), clear.pending, SELF_CHECK_TIMEOUT_MS, &clear.event, || {
                self.device_removed()
            })? == FenceWait::TimedOut
        {
            return Err(Error::os(
                0,
                "the readback ring's previous self-check clear did not complete within 30 s",
            ));
        }
        let colour = [f32::from(rgb[0]) / 255.0, f32::from(rgb[1]) / 255.0, f32::from(rgb[2]) / 255.0, 1.0];
        unsafe {
            clear
                .allocator
                .Reset()
                .map_err(|e| os_error(&e, "ID3D12CommandAllocator::Reset"))?;
            clear
                .list
                .Reset(clear.allocator.get(), None)
                .map_err(|e| os_error(&e, "ID3D12GraphicsCommandList::Reset"))?;
            // The same barrier Skia records: the simultaneous-access texture has decayed to
            // COMMON, and it decays back at the end of the execution, so nothing transitions it
            // back afterwards.
            let mut barrier = D3D12_RESOURCE_BARRIER {
                Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
                Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
                Anonymous: D3D12_RESOURCE_BARRIER_0 {
                    Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                        pResource: ManuallyDrop::new(Some(clear.target.clone_interface())),
                        Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                        StateBefore: D3D12_RESOURCE_STATE_COMMON,
                        StateAfter: D3D12_RESOURCE_STATE_RENDER_TARGET,
                    }),
                },
            };
            clear
                .list
                .ResourceBarrier(std::slice::from_ref(&barrier));
            // The union members are ManuallyDrop: release the reference the barrier carried.
            let mut transition = ManuallyDrop::take(&mut barrier.Anonymous.Transition);
            ManuallyDrop::drop(&mut transition.pResource);
            let target = clear
                .heap
                .GetCPUDescriptorHandleForHeapStart();
            clear
                .list
                .ClearRenderTargetView(target, &colour, None);
            clear
                .list
                .Close()
                .map_err(|e| os_error(&e, "ID3D12GraphicsCommandList::Close"))?;
            let list = clear
                .list
                .cast::<ID3D12CommandList>()
                .map_err(|e| os_error(&e, "QueryInterface(ID3D12CommandList)"))?;
            // Under the timeline lock, every render-fence value from `pending` on is signalled on
            // the direct queue behind this execution, so reaching it means the clear completed.
            let _timeline = self
                .timeline
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            self.device
                .direct_queue()
                .ExecuteCommandLists(&[Some(list)]);
            clear.pending = self.last_signaled.load(Ordering::Acquire) + 1;
        }
        Ok(())
    }

    /// Creation's self-check: slot 0 cleared, read back through the copy path, first and last
    /// pixels compared. Leaves slot 0 idle on success.
    fn self_check(&self) -> Result<()> {
        self.begin(0)?;
        if let Err(e) = self.clear_slot0(SELF_CHECK_RGB) {
            let _ = self.cancel_begin(0);
            return Err(e);
        }
        self.submit(0)?;
        let waited = if injected(fault::TIMEOUT) {
            None
        } else {
            self.wait(0, SELF_CHECK_TIMEOUT_MS)?
        };
        let Some((address, row_bytes)) = waited else {
            return Err(Error::capability(
                "the readback ring's self-check copy did not complete within 30 s",
            ));
        };
        let mut expected = [SELF_CHECK_RGB[2], SELF_CHECK_RGB[1], SELF_CHECK_RGB[0], 0xFF];
        if injected(fault::MISMATCH) {
            expected[0] ^= 0xFF;
        }
        let corners = [(0u32, 0u32), (self.width - 1, self.height - 1)];
        for (x, y) in corners {
            let offset = y as usize * row_bytes as usize + x as usize * 4;
            let pixel = unsafe { std::slice::from_raw_parts(address.add(offset), 4) };
            if pixel != expected {
                return Err(Error::capability(format!(
                    "the readback ring's self-check read back {:02X}{:02X}{:02X}{:02X} at ({x},{y}), expected {:02X}{:02X}{:02X}FF",
                    pixel[0], pixel[1], pixel[2], pixel[3], expected[0], expected[1], expected[2]
                )));
            }
        }
        Ok(())
    }

    /// Waits out every copy this ring queued. Failures are swallowed: a removed device or a hung
    /// GPU leaves nothing to wait for, and teardown must still release what it holds.
    fn drain(&self, timeout_ms: u32) {
        let last = self.last_signaled.load(Ordering::Acquire);
        // A fresh signal behind everything on the copy queue covers a copy that executed but
        // whose own Signal was refused.
        let queued = unsafe {
            self.copy_queue
                .Signal(self.consumed_fence.get(), last + 1)
        };
        if queued.is_ok() {
            let _ = self.wait_consumed(last + 1, timeout_ms);
        } else if last > 0 {
            let _ = self.wait_consumed(last, timeout_ms);
        }
    }

    /// What `_destroy` owes the caller at the call rather than at the last `Arc`: the drain, the
    /// unmaps and the wait event.
    pub(crate) fn teardown(&self) {
        self.drain(DESTROY_DRAIN_TIMEOUT_MS);
        for slot in &self.slots {
            slot.unmap();
        }
        self.wait
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
    }
}

impl Slot {
    fn unmap(&self) {
        if !self
            .mapped
            .swap(ptr::null_mut(), Ordering::AcqRel)
            .is_null()
        {
            unsafe {
                self.readback
                    .Unmap(0, Some(&EMPTY_WRITTEN_RANGE))
            };
        }
    }
}

impl Drop for Slot {
    /// A slot unmaps its own buffer, so a ring whose construction failed half way leaves nothing
    /// mapped; after a teardown this is a no-op.
    fn drop(&mut self) {
        self.unmap();
    }
}

fn transition(slot: &Slot, index: u32, from: u32, to: u32) -> Result<()> {
    slot.state
        .compare_exchange(from, to, Ordering::AcqRel, Ordering::Acquire)
        .map(|_| ())
        .map_err(|actual| wrong_state(index, actual, from))
}

/// The C# `Transition`'s sentence with the slot index: this text reaches the caller as the
/// `InvalidOperationException` message.
fn wrong_state(index: u32, actual: u32, wanted: u32) -> Error {
    Error::state(format!(
        "Readback slot {index} is in state {actual}, not {wanted} (Begin, Submit, Wait order violated)."
    ))
}

/// Publishes which protocol this submission is, then takes the slot. The state is read first so a
/// submit on a slot that is not composing cannot rewrite the flag of a submission another thread
/// waits on; the compare-and-swap is still the authority.
fn begin_submission(slot: &Slot, index: u32, shared: bool) -> Result<()> {
    let state = slot.state.load(Ordering::Acquire);
    if state != SLOT_COMPOSING {
        return Err(wrong_state(index, state, SLOT_COMPOSING));
    }
    slot.shared.store(shared, Ordering::Release);
    transition(slot, index, SLOT_COMPOSING, SLOT_IN_FLIGHT)
}

/// The precondition of `wait`, `release_shared` and `abandon_shared`: in flight, and submitted for
/// this caller's protocol.
fn check_in_flight(slot: &Slot, index: u32, want_shared: bool) -> Result<()> {
    if slot.state.load(Ordering::Acquire) != SLOT_IN_FLIGHT {
        return Err(Error::state(format!("Readback slot {index} has not been submitted.")));
    }
    if slot.shared.load(Ordering::Acquire) != want_shared {
        let detail = if want_shared {
            "was submitted for readback; use Wait."
        } else {
            "was submitted for an external consumer; use ReleaseShared."
        };
        return Err(Error::state(format!("Readback slot {index} {detail}")));
    }
    Ok(())
}

/// Builds the ring and self-checks it. Shared first, non-shared on refusal.
pub(crate) fn create(device: &Arc<DeviceInner>, width: u32, height: u32, slots: u32) -> Result<Arc<RingInner>> {
    create_inner(device, width, height, slots, true)
}

/// The whole of ring creation. `allow_shared` is false only from the crate's own tests, which need
/// a ring the converter must refuse.
pub(crate) fn create_inner(device: &Arc<DeviceInner>, width: u32, height: u32, slots: u32, allow_shared: bool) -> Result<Arc<RingInner>> {
    if width == 0 || height == 0 || slots == 0 {
        return Err(Error::invalid_arg(format!(
            "a readback ring needs a positive size and slot count, not {width}x{height} with {slots} slots"
        )));
    }
    let d3d12 = device.d3d12();

    let tex_desc = D3D12_RESOURCE_DESC {
        Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Alignment: 0,
        Width: u64::from(width),
        Height: height,
        DepthOrArraySize: 1,
        MipLevels: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_UNKNOWN,
        Flags: D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS,
    };

    let mut footprint = D3D12_PLACED_SUBRESOURCE_FOOTPRINT::default();
    let mut num_rows = 0u32;
    let mut row_size = 0u64;
    let mut total_bytes = 0u64;
    unsafe {
        d3d12.GetCopyableFootprints(
            &tex_desc,
            0,
            1,
            0,
            Some(&mut footprint),
            Some(&mut num_rows),
            Some(&mut row_size),
            Some(&mut total_bytes),
        );
    }
    let row_pitch = footprint.Footprint.RowPitch;
    let tight_row = u64::from(width) * 4;
    if num_rows != height
        || row_size != tight_row
        || footprint.Offset != 0
        || u64::from(row_pitch) < tight_row
        || total_bytes > i32::MAX as u64
    {
        return Err(Error::capability(format!(
            "Unexpected copyable footprint for {width}x{height} BGRA (rows {num_rows}, row {row_size} B, pitch {row_pitch}, total {total_bytes} B)."
        )));
    }

    // All or nothing: both fences and every slot texture carry the shared flags, or none does, so
    // `shareable` describes every object the zero-copy path would open.
    let mut shareable = false;
    let mut created = None;
    if allow_shared {
        if let Ok(set) = create_shared_set(d3d12, &tex_desc, slots, true) {
            shareable = true;
            created = Some(set);
        }
    }
    let (render_fence, consumed_fence, textures) = match created {
        Some(set) => set,
        None => create_shared_set(d3d12, &tex_desc, slots, false).map_err(|(hr, step)| hresult_error(hr, &step))?,
    };

    let queue_desc = D3D12_COMMAND_QUEUE_DESC {
        Type: D3D12_COMMAND_LIST_TYPE_COPY,
        Priority: D3D12_COMMAND_QUEUE_PRIORITY_NORMAL.0,
        Flags: D3D12_COMMAND_QUEUE_FLAG_NONE,
        NodeMask: 0,
    };
    let copy_queue: ID3D12CommandQueue =
        unsafe { d3d12.CreateCommandQueue(&queue_desc) }.map_err(|e| os_error(&e, "CreateCommandQueue(COPY)"))?;

    let buffer_desc = D3D12_RESOURCE_DESC {
        Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
        Alignment: 0,
        Width: total_bytes,
        Height: 1,
        DepthOrArraySize: 1,
        MipLevels: 1,
        // A BUFFER desc must carry DXGI_FORMAT_UNKNOWN; anything else is E_INVALIDARG.
        Format: DXGI_FORMAT_UNKNOWN,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
        Flags: Default::default(),
    };

    let clear = create_clear_list(d3d12, &textures[0])?;

    let mut built = Vec::with_capacity(slots as usize);
    for texture in textures {
        built.push(create_slot(d3d12, texture, &buffer_desc, &footprint, total_bytes)?);
    }

    let ring = Arc::new(RingInner {
        device: device.clone(),
        copy_queue: FreeThreaded::new(copy_queue),
        render_fence: FreeThreaded::new(render_fence),
        consumed_fence: FreeThreaded::new(consumed_fence),
        slots: built,
        width,
        height,
        row_pitch,
        shareable,
        last_signaled: AtomicU64::new(0),
        timeline: Mutex::new(()),
        wait: Mutex::new(Some(Event::new()?)),
        clear: Mutex::new(clear),
    });
    if let Err(e) = ring.self_check() {
        ring.teardown();
        return Err(e);
    }
    Ok(ring)
}

/// Both fences and every slot texture, with or without the shared-handle flags, as one unit. On
/// any failure everything this call created is dropped; the error carries the HRESULT and the
/// step name.
#[allow(clippy::type_complexity)]
fn create_shared_set(
    device: &ID3D12Device,
    tex_desc: &D3D12_RESOURCE_DESC,
    slots: u32,
    shared: bool,
) -> std::result::Result<(ID3D12Fence, ID3D12Fence, Vec<ID3D12Resource>), (HRESULT, String)> {
    let fence_flags: D3D12_FENCE_FLAGS = if shared { D3D12_FENCE_FLAG_SHARED } else { D3D12_FENCE_FLAG_NONE };
    let heap_flags: D3D12_HEAP_FLAGS = if shared { D3D12_HEAP_FLAG_SHARED } else { D3D12_HEAP_FLAG_NONE };
    let note = if shared { " (shared)" } else { "" };
    let fence_step = format!("CreateFence{note}");

    let render: ID3D12Fence = unsafe { device.CreateFence(0, fence_flags) }.map_err(|e| (e.code(), fence_step.clone()))?;
    let consumed: ID3D12Fence = unsafe { device.CreateFence(0, fence_flags) }.map_err(|e| (e.code(), fence_step))?;

    let texture_step = format!(
        "CreateCommittedResource({}x{} render target{note})",
        tex_desc.Width, tex_desc.Height
    );
    let mut textures = Vec::with_capacity(slots as usize);
    for _ in 0..slots {
        let texture = committed(device, D3D12_HEAP_TYPE_DEFAULT, heap_flags, tex_desc, D3D12_RESOURCE_STATE_COMMON)
            .map_err(|hr| (hr, texture_step.clone()))?;
        textures.push(texture);
    }
    Ok((render, consumed, textures))
}

/// The RTV over slot 0 and the direct list the self-checks clear it with.
fn create_clear_list(device: &ID3D12Device, slot0: &ID3D12Resource) -> Result<ClearList> {
    let heap_desc = D3D12_DESCRIPTOR_HEAP_DESC {
        Type: D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
        NumDescriptors: 1,
        Flags: D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
        NodeMask: 0,
    };
    let heap: ID3D12DescriptorHeap =
        unsafe { device.CreateDescriptorHeap(&heap_desc) }.map_err(|e| os_error(&e, "CreateDescriptorHeap(RTV)"))?;
    unsafe { device.CreateRenderTargetView(slot0, None, heap.GetCPUDescriptorHandleForHeapStart()) };
    let allocator: ID3D12CommandAllocator = unsafe { device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT) }
        .map_err(|e| os_error(&e, "CreateCommandAllocator(DIRECT)"))?;
    let list: ID3D12GraphicsCommandList = unsafe { device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None) }
        .map_err(|e| os_error(&e, "CreateCommandList(DIRECT)"))?;
    unsafe { list.Close() }.map_err(|e| os_error(&e, "CommandList.Close"))?;
    Ok(ClearList {
        target: FreeThreaded::new(slot0.clone()),
        heap: FreeThreaded::new(heap),
        allocator: FreeThreaded::new(allocator),
        list: FreeThreaded::new(list),
        pending: 0,
        event: Event::new()?,
    })
}

/// One slot's readback buffer, its mapping, its allocator and the one copy list it re-executes.
fn create_slot(
    device: &ID3D12Device,
    texture: ID3D12Resource,
    buffer_desc: &D3D12_RESOURCE_DESC,
    footprint: &D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
    total_bytes: u64,
) -> Result<Slot> {
    let readback = committed(
        device,
        D3D12_HEAP_TYPE_READBACK,
        D3D12_HEAP_FLAG_NONE,
        buffer_desc,
        D3D12_RESOURCE_STATE_COPY_DEST,
    )
    .map_err(|hr| hresult_error(hr, &format!("CreateCommittedResource({total_bytes} B readback)")))?;

    // Mapped once for the resource's life: READBACK heaps are write-back cached system memory and
    // the convert stage reads them in place.
    let mut mapped: *mut c_void = ptr::null_mut();
    unsafe { readback.Map(0, None, Some(&mut mapped)) }.map_err(|e| os_error(&e, "Map(readback)"))?;
    // From here the slot owns the mapping, so an early return below still unmaps it.
    let allocator: ID3D12CommandAllocator = match unsafe { device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COPY) } {
        Ok(allocator) => allocator,
        Err(e) => {
            unsafe { readback.Unmap(0, Some(&EMPTY_WRITTEN_RANGE)) };
            return Err(os_error(&e, "CreateCommandAllocator(COPY)"));
        }
    };
    let list: ID3D12GraphicsCommandList = match unsafe { device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COPY, &allocator, None) } {
        Ok(list) => list,
        Err(e) => {
            unsafe { readback.Unmap(0, Some(&EMPTY_WRITTEN_RANGE)) };
            return Err(os_error(&e, "CreateCommandList(COPY)"));
        }
    };

    // The one copy this slot does: texture subresource 0 to the readback buffer at the placed
    // footprint, no box.
    let mut destination = D3D12_TEXTURE_COPY_LOCATION {
        pResource: ManuallyDrop::new(Some(readback.clone())),
        Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            PlacedFootprint: *footprint,
        },
    };
    let mut source = D3D12_TEXTURE_COPY_LOCATION {
        pResource: ManuallyDrop::new(Some(texture.clone())),
        Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            SubresourceIndex: 0,
        },
    };
    unsafe { list.CopyTextureRegion(&destination, 0, 0, 0, &source, None) };
    // The union member is a ManuallyDrop, so the two references above are released by hand; the
    // command list keeps its own.
    unsafe {
        ManuallyDrop::drop(&mut destination.pResource);
        ManuallyDrop::drop(&mut source.pResource);
    }

    let slot = Slot {
        texture: FreeThreaded::new(texture),
        readback: FreeThreaded::new(readback),
        _allocator: FreeThreaded::new(allocator),
        copy_list: FreeThreaded::new(
            list.cast::<ID3D12CommandList>()
                .map_err(|e| os_error(&e, "QueryInterface(ID3D12CommandList)"))?,
        ),
        mapped: AtomicPtr::new(mapped.cast::<u8>()),
        state: AtomicU32::new(SLOT_IDLE),
        shared: AtomicBool::new(false),
        fence_value: AtomicU64::new(0),
    };
    unsafe { list.Close() }.map_err(|e| os_error(&e, "CommandList.Close"))?;
    Ok(slot)
}

/// `CreateCommittedResource` with no optimised clear value, on a heap whose only non-default field
/// is its type.
fn committed(
    device: &ID3D12Device,
    heap_type: D3D12_HEAP_TYPE,
    heap_flags: D3D12_HEAP_FLAGS,
    desc: &D3D12_RESOURCE_DESC,
    state: D3D12_RESOURCE_STATES,
) -> std::result::Result<ID3D12Resource, HRESULT> {
    let heap = D3D12_HEAP_PROPERTIES {
        Type: heap_type,
        CPUPageProperty: D3D12_CPU_PAGE_PROPERTY_UNKNOWN,
        MemoryPoolPreference: D3D12_MEMORY_POOL_UNKNOWN,
        CreationNodeMask: 0,
        VisibleNodeMask: 0,
    };
    let mut resource: Option<ID3D12Resource> = None;
    unsafe { device.CreateCommittedResource(&heap, heap_flags, desc, state, None, &mut resource) }.map_err(|e| e.code())?;
    resource.ok_or(HRESULT(0))
}

#[cfg(test)]
pub(crate) mod tests {
    use std::thread;

    use super::*;
    use crate::ffi::status::PriscInteropStatus;
    use crate::platform::windows::gpu::device::tests::device_or_skip;

    /// A device and a ring over it, or `None` when the machine has no device at all. A machine
    /// that has a device and still cannot build a ring is a bug, so that panics.
    pub(crate) fn ring_or_skip(test: &str, width: u32, height: u32, slots: u32) -> Option<(Arc<DeviceInner>, Arc<RingInner>)> {
        let device = device_or_skip(test)?;
        let ring = create(&device, width, height, slots).unwrap_or_else(|e| {
            panic!(
                "{test}: a device must build a {width}x{height} ring with {slots} slots ({})",
                e.message
            )
        });
        Some((device, ring))
    }

    fn status(e: Error) -> PriscInteropStatus {
        e.status
    }

    /// Begin, clear, submit, wait: the readback of one slot-0 frame of `rgb`, as BGRA bytes.
    fn round_trip(ring: &RingInner, rgb: [u8; 3]) -> [u8; 4] {
        ring.begin(0).expect("slot 0 is idle");
        ring.clear_slot0(rgb)
            .expect("the clear is recorded");
        ring.submit(0)
            .expect("the submit is accepted");
        let (address, _) = ring
            .wait(0, 30_000)
            .expect("the wait is accepted")
            .expect("the copy completes");
        let mut pixel = [0u8; 4];
        pixel.copy_from_slice(unsafe { std::slice::from_raw_parts(address, 4) });
        pixel
    }

    #[test]
    fn ring_create_reports_a_256_aligned_pitch_and_passes_its_self_check() {
        let Some((_device, ring)) = ring_or_skip("ring_create_reports_a_256_aligned_pitch_and_passes_its_self_check", 333, 97, 2) else {
            return;
        };
        let info = ring.info();
        assert_eq!((info.width, info.height, info.slots), (333, 97, 2));
        assert!(info.row_pitch >= 333 * 4, "{}", info.row_pitch);
        assert_eq!(info.row_pitch % 256, 0, "D3D12_TEXTURE_DATA_PITCH_ALIGNMENT is 256");
        // The self-check spent value 1 on slot 0 and left it idle.
        assert_eq!(ring.last_signaled(), 1);
        ring.begin(0)
            .expect("the self-check leaves slot 0 idle");
    }

    #[test]
    fn ring_readback_round_trip_returns_the_cleared_colour() {
        let Some((_device, ring)) = ring_or_skip("ring_readback_round_trip_returns_the_cleared_colour", 64, 48, 2) else {
            return;
        };
        assert_eq!(round_trip(&ring, [0x44, 0x55, 0x66]), [0x66, 0x55, 0x44, 0xFF]);
        assert_eq!(round_trip(&ring, [0x10, 0x20, 0x30]), [0x30, 0x20, 0x10, 0xFF]);
        ring.teardown();
    }

    #[test]
    fn ring_rejects_out_of_order_use() {
        let Some((_device, ring)) = ring_or_skip("ring_rejects_out_of_order_use", 32, 32, 2) else {
            return;
        };
        assert_eq!(status(ring.submit(0).unwrap_err()), PriscInteropStatus::State);
        assert_eq!(status(ring.wait(0, 0).unwrap_err()), PriscInteropStatus::State);

        ring.begin(0).expect("slot 0 is idle");
        assert_eq!(status(ring.begin(0).unwrap_err()), PriscInteropStatus::State);
        ring.submit_shared(0)
            .expect("the shared submit is accepted");
        assert_eq!(status(ring.wait(0, 0).unwrap_err()), PriscInteropStatus::State);
        ring.abandon_shared(0)
            .expect("a shared slot can be abandoned");

        ring.begin(1).expect("slot 1 is idle");
        ring.submit(1)
            .expect("the submit is accepted");
        assert_eq!(status(ring.release_shared(1, 0).unwrap_err()), PriscInteropStatus::State);
        assert!(ring
            .wait(1, 30_000)
            .expect("the wait")
            .is_some());

        assert_eq!(status(ring.abandon_shared(1).unwrap_err()), PriscInteropStatus::State);
        assert_eq!(status(ring.begin(2).unwrap_err()), PriscInteropStatus::InvalidArg);
        assert_eq!(status(ring.clear_slot0([0, 0, 0]).unwrap_err()), PriscInteropStatus::State);
    }

    #[test]
    fn ring_cancel_begin_returns_the_slot_to_idle() {
        let Some((_device, ring)) = ring_or_skip("ring_cancel_begin_returns_the_slot_to_idle", 32, 32, 1) else {
            return;
        };
        ring.begin(0).expect("slot 0 is idle");
        ring.cancel_begin(0)
            .expect("a begun slot can be cancelled");
        ring.begin(0)
            .expect("a cancelled slot is idle again");
        ring.cancel_begin(0)
            .expect("and cancelled again");
        assert_eq!(status(ring.cancel_begin(0).unwrap_err()), PriscInteropStatus::State);
    }

    #[test]
    fn ring_abandon_shared_moves_the_consumed_fence() {
        let Some((_device, ring)) = ring_or_skip("ring_abandon_shared_moves_the_consumed_fence", 32, 32, 1) else {
            return;
        };
        ring.begin(0).expect("slot 0 is idle");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        ring.abandon_shared(0).expect("abandon");
        assert_eq!(ring.wait_consumed(value, 0).expect("wait"), PRISC_OK);
        assert_eq!(round_trip(&ring, [0x10, 0x20, 0x30]), [0x30, 0x20, 0x10, 0xFF]);
        assert_eq!(ring.last_signaled(), value + 1);
    }

    /// Both orders of the race are correct; what must hold either way is asserted.
    #[test]
    fn ring_abandon_shared_races_a_waiter() {
        let Some((_device, ring)) = ring_or_skip("ring_abandon_shared_races_a_waiter", 32, 32, 1) else {
            return;
        };
        ring.begin(0).expect("slot 0 is idle");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        let abandoning = ring.clone();
        let abandon = thread::spawn(move || abandoning.abandon_shared(0));
        let waited = ring
            .wait_consumed(value, 2_000)
            .expect("wait");
        assert!(waited == PRISC_OK || waited == PRISC_S_TIMEOUT, "{waited}");
        abandon
            .join()
            .expect("the abandon thread")
            .expect("abandon");
        assert_eq!(ring.wait_consumed(value, 0).expect("wait"), PRISC_OK);
    }

    #[test]
    fn ring_wait_times_out_without_a_submit_signal() {
        let Some((_device, ring)) = ring_or_skip("ring_wait_times_out_without_a_submit_signal", 32, 32, 1) else {
            return;
        };
        let unreachable = ring.last_signaled() + 5;
        assert_eq!(
            ring.wait_consumed(unreachable, 50)
                .expect("wait"),
            PRISC_S_TIMEOUT
        );
        assert_eq!(ring.wait_consumed(0, 0).expect("wait"), PRISC_OK);
    }

    #[test]
    fn ring_survives_two_hundred_frames_and_tears_down_clean() {
        let Some((_device, ring)) = ring_or_skip("ring_survives_two_hundred_frames_and_tears_down_clean", 64, 64, 3) else {
            return;
        };
        for frame in 0..200u32 {
            let slot = frame % 3;
            ring.begin(slot).expect("the slot is idle");
            ring.submit(slot)
                .expect("the submit is accepted");
            ring.wait(slot, 30_000)
                .expect("wait")
                .expect("the copy completes");
        }
        ring.teardown();
        assert_eq!(status(ring.wait_consumed(1, 0).unwrap_err()), PriscInteropStatus::State);
    }

    #[test]
    fn ring_outlives_its_device_handle() {
        let Some((device, ring)) = ring_or_skip("ring_outlives_its_device_handle", 32, 32, 1) else {
            return;
        };
        drop(device);
        assert_eq!(round_trip(&ring, [0x44, 0x55, 0x66]), [0x66, 0x55, 0x44, 0xFF]);
    }

    /// Two threads submitting different slots at once never share a fence value: the values
    /// handed out are exactly the next ones, each once, for both protocols. (The protocols are
    /// not mixed on one ring at once: a consumer's signal of the consumed fence is not ordered
    /// against the copy queue's.)
    #[test]
    fn ring_submits_from_two_threads_take_distinct_values() {
        let Some((_device, ring)) = ring_or_skip("ring_submits_from_two_threads_take_distinct_values", 32, 32, 2) else {
            return;
        };
        const FRAMES: usize = 100;

        fn on_two_threads(ring: &Arc<RingInner>, frame: fn(&RingInner, u32) -> u64) -> Vec<u64> {
            let threads: Vec<_> = (0..2u32)
                .map(|slot| {
                    let ring = ring.clone();
                    thread::spawn(move || {
                        (0..FRAMES)
                            .map(|_| frame(&ring, slot))
                            .collect::<Vec<_>>()
                    })
                })
                .collect();
            let mut values: Vec<u64> = threads
                .into_iter()
                .flat_map(|t| t.join().expect("a submitting thread"))
                .collect();
            values.sort_unstable();
            values
        }

        let start = ring.last_signaled();
        let copies = on_two_threads(&ring, |ring, slot| {
            ring.begin(slot).expect("the slot is idle");
            let value = ring
                .submit(slot)
                .expect("the submit is accepted");
            ring.wait(slot, 30_000)
                .expect("wait")
                .expect("the copy completes");
            value
        });
        let expected: Vec<u64> = (start + 1..=start + 2 * FRAMES as u64).collect();
        assert_eq!(copies, expected, "every copy value handed out once, none skipped");

        let start = ring.last_signaled();
        let shared = on_two_threads(&ring, |ring, slot| {
            ring.begin(slot).expect("the slot is idle");
            let value = ring
                .submit_shared(slot)
                .expect("the shared submit is accepted");
            ring.abandon_shared(slot).expect("abandon");
            value
        });
        let expected: Vec<u64> = (start + 1..=start + 2 * FRAMES as u64).collect();
        assert_eq!(shared, expected, "every shared value handed out once, none skipped");
        assert_eq!(round_trip(&ring, [0x10, 0x20, 0x30]), [0x30, 0x20, 0x10, 0xFF]);
    }

    /// The release-or-abandon decision the converter's self-check makes on failure, without a
    /// converter (so it runs on WARP): nothing queued abandons, a queued signal that lands
    /// releases, a queued signal that never lands abandons after the wait. The slot is idle and
    /// usable for readback after each.
    #[test]
    fn recover_shared_slot_releases_or_abandons_and_leaves_the_slot_idle() {
        let Some((_device, ring)) = ring_or_skip("recover_shared_slot_releases_or_abandons_and_leaves_the_slot_idle", 32, 32, 1) else {
            return;
        };

        ring.begin(0).expect("slot 0 is idle");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        assert_eq!(
            ring.recover_shared_slot(0, false, 30_000)
                .expect("recover"),
            SlotRecovery::Abandoned
        );
        assert_eq!(ring.wait_consumed(value, 0).expect("wait"), PRISC_OK);
        assert_eq!(round_trip(&ring, [0x10, 0x20, 0x30]), [0x30, 0x20, 0x10, 0xFF]);

        ring.begin(0).expect("slot 0 is idle");
        let value = ring
            .submit_shared(0)
            .expect("the shared submit is accepted");
        unsafe { ring.consumed_fence().Signal(value) }.expect("the consumer's signal");
        assert_eq!(
            ring.recover_shared_slot(0, true, 30_000)
                .expect("recover"),
            SlotRecovery::Released
        );
        assert_eq!(round_trip(&ring, [0x40, 0x50, 0x60]), [0x60, 0x50, 0x40, 0xFF]);

        ring.begin(0).expect("slot 0 is idle");
        ring.submit_shared(0)
            .expect("the shared submit is accepted");
        assert_eq!(
            ring.recover_shared_slot(0, true, 50)
                .expect("recover"),
            SlotRecovery::Abandoned,
            "a queued signal that never lands is abandoned after the wait"
        );
        assert_eq!(round_trip(&ring, [0x70, 0x80, 0x90]), [0x90, 0x80, 0x70, 0xFF]);
    }

    /// Critique F22's failure path: a mismatch and a copy that never lands are both
    /// `PRISC_E_CAPABILITY`, the failed ring is torn down and dropped whole (nothing keeps the
    /// device), and the next ring on the same device comes up.
    #[test]
    fn a_failed_ring_self_check_is_capability_and_releases_everything() {
        let Some(device) = device_or_skip("a_failed_ring_self_check_is_capability_and_releases_everything") else {
            return;
        };
        let holders = Arc::strong_count(&device);
        for (kind, words) in [(fault::MISMATCH, "read back"), (fault::TIMEOUT, "did not complete")] {
            fault::SELF_CHECK.with(|f| f.set(kind));
            let result = create(&device, 32, 32, 2);
            fault::SELF_CHECK.with(|f| f.set(0));
            let Err(e) = result else {
                panic!("an injected self-check fault {kind} must fail the create");
            };
            assert_eq!(e.status, PriscInteropStatus::Capability, "{}", e.message);
            assert!(e.message.contains(words), "{}", e.message);
            assert_eq!(Arc::strong_count(&device), holders, "the failed ring released its device");

            let ring = create(&device, 32, 32, 2).unwrap_or_else(|e| panic!("a healthy ring after a failed one: {}", e.message));
            assert_eq!(round_trip(&ring, [1, 2, 3]), [3, 2, 1, 0xFF]);
        }
    }

    #[test]
    fn ring_rejects_a_zero_size() {
        let Some(device) = device_or_skip("ring_rejects_a_zero_size") else {
            return;
        };
        for (w, h, s) in [(0, 16, 1), (16, 0, 1), (16, 16, 0)] {
            assert_eq!(create(&device, w, h, s).err().map(status), Some(PriscInteropStatus::InvalidArg));
        }
    }

    #[test]
    fn ring_create_inner_can_force_a_non_shareable_ring() {
        let Some(device) = device_or_skip("ring_create_inner_can_force_a_non_shareable_ring") else {
            return;
        };
        let ring = create_inner(&device, 32, 32, 1, false).unwrap_or_else(|e| panic!("a non-shared ring must build: {}", e.message));
        assert_eq!(ring.info().shareable, 0);
        assert_eq!(round_trip(&ring, [1, 2, 3]), [3, 2, 1, 0xFF]);
    }
}
