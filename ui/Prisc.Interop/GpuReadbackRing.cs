using System;

namespace Prisc.Interop
{
    /// <summary>
    /// The Direct3D 12 copy-queue readback ring (<c>prisc_gpu_readback_ring_*_windows</c>): N slots,
    /// each a BGRA render target the compositor draws into, with a readback buffer kept mapped for
    /// its life.
    /// </summary>
    /// <remarks>
    /// <para>Readback protocol: <see cref="Begin"/>, draw, <see cref="Submit"/>, <see cref="Wait"/>.
    /// Zero-copy protocol: <see cref="Begin"/>, draw, <see cref="SubmitShared"/>, the external
    /// consumer waits the returned value and signals the consumed fence with it, then
    /// <see cref="ReleaseShared"/> (or <see cref="AbandonShared"/> when the consumer failed before
    /// signalling). The begin and submit calls belong to the drawing thread; the waits are
    /// free-threaded, so the stage that consumes the pixels pays for the fence.</para>
    /// <para>The crate self-checks the ring at creation (slot 0 cleared, read back, compared), so a
    /// driver whose copy path is broken fails <see cref="TryCreate"/> instead of a render. The ring
    /// holds its device inside the crate; disposing the <see cref="GpuDevice"/> first is untidy,
    /// not unsafe. Dispose drains the copy queue, unmaps the buffers and closes the wait event.</para>
    /// <para>Windows only; elsewhere <see cref="TryCreate"/> answers null with
    /// <see cref="InteropStatus.Unsupported"/>.</para>
    /// </remarks>
    public sealed unsafe class GpuReadbackRing : InteropHandle
    {
        private GpuReadbackRing(PriscGpuReadbackRing* handle, in PriscGpuRingInfo info)
        {
            Adopt((IntPtr)handle);
            Width = (int)info.width;
            Height = (int)info.height;
            SlotCount = (int)info.slots;
            RowPitch = (int)info.row_pitch;
            Shareable = info.shareable != 0;
        }

        /// <summary>Slot texture width in pixels.</summary>
        public int Width { get; }

        /// <summary>Slot texture height in pixels.</summary>
        public int Height { get; }

        /// <summary>How many slots the ring has.</summary>
        public int SlotCount { get; }

        /// <summary>Bytes per row of the memory <see cref="Wait"/> lends: a multiple of 256, never
        /// less than <see cref="Width"/> times four.</summary>
        public int RowPitch { get; }

        /// <summary>True when every slot texture and both fences carry the shared-handle flags, so
        /// the zero-copy encode path can open them on a Direct3D 11 device. Never a partial set.</summary>
        public bool Shareable { get; }

        /// <summary>
        /// Builds and self-checks the ring, or returns null with the reason. Never throws for a
        /// driver that refused: the caller falls back to synchronous readback.
        /// </summary>
        /// <param name="status">The crate's status: <see cref="InteropStatus.Capability"/> when the
        /// self-check or the copy footprint says this driver cannot run the ring.</param>
        public static GpuReadbackRing? TryCreate(GpuDevice device, int width, int height, int slots,
            out string failureReason, out InteropStatus status)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slots, 0);

            PriscGpuReadbackRing* created = null;
            PriscGpuRingInfo info = default;
            int result;
            using (var lease = device.Lease())
            {
                result = NativeMethods.prisc_gpu_readback_ring_create_windows((PriscGpuDevice*)lease.Handle,
                    (uint)width, (uint)height, (uint)slots, &created, &info);
            }

            status = (InteropStatus)result;
            if (result != (int)InteropStatus.Ok || created == null)
            {
                failureReason = GpuStatus.Describe(result, "prisc_gpu_readback_ring_create_windows");
                return null;
            }

            failureReason = String.Empty;
            return new GpuReadbackRing(created, info);
        }

        /// <summary>Claims an idle slot and lends its <c>ID3D12Resource*</c> (the same pointer every
        /// frame). Wrap it for Skia afresh each frame with <c>ResourceState = COMMON</c>: the
        /// texture decays to that state at the end of every submission.</summary>
        public IntPtr Begin(int slot)
        {
            using var lease = Lease();
            void* texture = null;
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_begin_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot, &texture),
                "prisc_gpu_readback_ring_begin_windows");
            return (IntPtr)texture;
        }

        /// <summary>Puts a begun slot back to idle without submitting it (wrapping it failed).</summary>
        public void CancelBegin(int slot)
        {
            using var lease = Lease();
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_cancel_begin_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot),
                "prisc_gpu_readback_ring_cancel_begin_windows");
        }

        /// <summary>Queues a begun slot's readback, after its drawing was flushed and submitted on the
        /// direct queue, and returns the fence value the copy reaches.</summary>
        public ulong Submit(int slot)
        {
            using var lease = Lease();
            ulong value = 0;
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_submit_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot, &value),
                "prisc_gpu_readback_ring_submit_windows");
            return value;
        }

        /// <summary>Ends drawing on a begun slot for an external GPU consumer and returns the render
        /// fence value it must wait for and then signal on the consumed fence.</summary>
        public ulong SubmitShared(int slot)
        {
            using var lease = Lease();
            ulong value = 0;
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_submit_shared_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot, &value),
                "prisc_gpu_readback_ring_submit_shared_windows");
            return value;
        }

        /// <summary>Blocks until a submitted slot's pixels landed, makes it idle, and lends its
        /// mapped memory (valid until the slot's next <see cref="Begin"/>) and row pitch.</summary>
        /// <exception cref="TimeoutException">The copy did not land within
        /// <paramref name="timeout"/>; the slot is still in flight and may be waited for again.</exception>
        public (IntPtr Address, int RowBytes) Wait(int slot, TimeSpan timeout)
        {
            using var lease = Lease();
            byte* address = null;
            uint rowBytes = 0;
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_wait_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot,
                    GpuTimeout.Milliseconds(timeout), &address, &rowBytes),
                "prisc_gpu_readback_ring_wait_windows",
                $"The GPU did not finish the readback of slot {slot} within {timeout.TotalSeconds:F0} s.");
            return ((IntPtr)address, (int)rowBytes);
        }

        /// <summary>Blocks until the external consumer of a shared slot signalled the consumed fence
        /// for it, then makes the slot idle.</summary>
        /// <exception cref="TimeoutException">The signal did not land in time; the slot stays in flight.</exception>
        public void ReleaseShared(int slot, TimeSpan timeout)
        {
            using var lease = Lease();
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_release_shared_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot,
                    GpuTimeout.Milliseconds(timeout)),
                "prisc_gpu_readback_ring_release_shared_windows",
                $"The consumer of slot {slot} did not signal within {timeout.TotalSeconds:F0} s.");
        }

        /// <summary>Makes a shared slot idle after its consumer failed before signalling: the
        /// consumed fence is moved to the slot's value from the CPU when it is behind, never
        /// backwards. Nothing is waited for.</summary>
        public void AbandonShared(int slot)
        {
            using var lease = Lease();
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_abandon_shared_windows((PriscGpuReadbackRing*)lease.Handle, (uint)slot),
                "prisc_gpu_readback_ring_abandon_shared_windows");
        }

        /// <summary>Blocks until the consumed fence reached <paramref name="value"/>.</summary>
        /// <exception cref="TimeoutException">It did not within <paramref name="timeout"/>.</exception>
        public void WaitConsumed(ulong value, TimeSpan timeout)
        {
            using var lease = Lease();
            GpuStatus.Check(NativeMethods.prisc_gpu_readback_ring_wait_consumed_windows((PriscGpuReadbackRing*)lease.Handle, value,
                    GpuTimeout.Milliseconds(timeout)),
                "prisc_gpu_readback_ring_wait_consumed_windows",
                $"The consumed fence did not reach {value} within {timeout.TotalSeconds:F0} s.");
        }

        protected override int Destroy(IntPtr h) => NativeMethods.prisc_gpu_readback_ring_destroy_windows((PriscGpuReadbackRing*)h);
    }
}
