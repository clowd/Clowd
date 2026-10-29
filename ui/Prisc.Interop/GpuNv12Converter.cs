using System;

namespace Prisc.Interop
{
    /// <summary>
    /// The Direct3D 11 video processor that turns a readback ring slot's BGRA texture into an NV12
    /// texture in video memory (BT.601, limited range), and the NV12 textures it writes into
    /// (<c>prisc_gpu_nv12_*_windows</c>).
    /// </summary>
    /// <remarks>
    /// <para>Per frame: <see cref="GpuReadbackRing.SubmitShared"/> on the drawing thread, then
    /// <see cref="Convert"/> with that slot and fence value on the convert thread, then
    /// <see cref="GpuReadbackRing.ReleaseShared"/>. <see cref="Convert"/> queues a GPU wait, the blit
    /// and a signal and waits for none of them.</para>
    /// <para>The textures (<see cref="AcquireTexture"/>) are what FFmpeg encodes from. They are
    /// released with the static <see cref="ReleaseTexture"/>, which needs no converter: the crate
    /// keeps a texture's converter alive until the texture comes back, so FFmpeg's free path may run
    /// after this wrapper (or the encoder) was disposed. Disposing this wrapper drains what it
    /// queued with a short bound; call <see cref="Drain"/> first for the ordered, reported wait.</para>
    /// <para>Windows only; elsewhere <see cref="TryCreate"/> answers null.</para>
    /// </remarks>
    public sealed unsafe class GpuNv12Converter : InteropHandle
    {
        private GpuNv12Converter(PriscGpuNv12Converter* handle)
        {
            Adopt((IntPtr)handle);
        }

        /// <summary>
        /// Builds the converter over a shareable ring of <paramref name="device"/> and self-checks
        /// it (one known colour through slot 0, converted and compared with BT.601 limited range),
        /// or returns null with the reason. Slot 0 must be idle and is idle again either way, so a
        /// caller that falls back reads back over the same ring. Never throws for a driver that
        /// refused: the caller stays on readback.
        /// </summary>
        /// <param name="status">The crate's status: <see cref="InteropStatus.Capability"/> for the
        /// conditions under which this GPU cannot run the zero-copy path at all (skip), anything
        /// else for a capable GPU on which it failed, a wrong self-check conversion included.</param>
        public static GpuNv12Converter? TryCreate(GpuDevice device, GpuReadbackRing ring, out string failureReason, out InteropStatus status)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(ring);

            PriscGpuNv12Converter* created = null;
            int result;
            using (var deviceLease = device.Lease())
            using (var ringLease = ring.Lease())
            {
                result = NativeMethods.prisc_gpu_nv12_converter_create_windows((PriscGpuDevice*)deviceLease.Handle,
                    (PriscGpuReadbackRing*)ringLease.Handle, &created);
            }

            status = (InteropStatus)result;
            if (result != (int)InteropStatus.Ok || created == null)
            {
                failureReason = GpuStatus.Describe(result, "prisc_gpu_nv12_converter_create_windows");
                return null;
            }

            failureReason = String.Empty;
            return new GpuNv12Converter(created);
        }

        /// <summary>Converts a shared slot (ready at <paramref name="fenceValue"/>) into
        /// <paramref name="dstTexture"/>, a texture from <see cref="AcquireTexture"/>. Safe from any
        /// thread: the crate serialises concurrent converts.</summary>
        /// <param name="signalQueued">Written on every path, before this method throws: whether the
        /// consumed-fence signal reached the GPU timeline. A refused GPU wait queues nothing, so the
        /// slot must be abandoned; a refused blit still queues the signal, so it can be released.</param>
        public void Convert(int slot, ulong fenceValue, IntPtr dstTexture, out bool signalQueued)
        {
            using var lease = Lease();
            byte queued = 0;
            int status = NativeMethods.prisc_gpu_nv12_converter_convert_windows((PriscGpuNv12Converter*)lease.Handle,
                (uint)slot, fenceValue, (void*)dstTexture, &queued);
            signalQueued = queued != 0;
            GpuStatus.Check(status, "prisc_gpu_nv12_converter_convert_windows");
        }

        /// <summary>Blocks until every conversion this converter queued completed (a no-op before
        /// the first).</summary>
        /// <exception cref="TimeoutException">It did not within <paramref name="timeout"/>.</exception>
        public void Drain(TimeSpan timeout)
        {
            using var lease = Lease();
            GpuStatus.Check(NativeMethods.prisc_gpu_nv12_converter_drain_windows((PriscGpuNv12Converter*)lease.Handle,
                    GpuTimeout.Milliseconds(timeout)),
                "prisc_gpu_nv12_converter_drain_windows",
                $"The NV12 conversions did not finish within {timeout.TotalSeconds:F0} s.");
        }

        /// <summary>Reads one of this converter's textures back through a staging copy: the packed
        /// luma plane into <paramref name="y"/> (width times height bytes) and the interleaved chroma
        /// plane into <paramref name="uv"/> (half that). Slow; for tests.</summary>
        public void Read(IntPtr texture, byte[] y, byte[] uv)
        {
            ArgumentNullException.ThrowIfNull(y);
            ArgumentNullException.ThrowIfNull(uv);
            using var lease = Lease();
            fixed (byte* luma = y)
            fixed (byte* chroma = uv)
            {
                GpuStatus.Check(NativeMethods.prisc_gpu_nv12_converter_read_windows((PriscGpuNv12Converter*)lease.Handle,
                        (void*)texture, luma, (nuint)y.Length, chroma, (nuint)uv.Length),
                    "prisc_gpu_nv12_converter_read_windows");
            }
        }

        /// <summary>A new NV12 encoder texture with this converter's output view. The crate owns it
        /// until <see cref="ReleaseTexture"/>; FFmpeg never releases it.</summary>
        public IntPtr AcquireTexture()
        {
            using var lease = Lease();
            void* texture = null;
            GpuStatus.Check(NativeMethods.prisc_gpu_nv12_texture_acquire_windows((PriscGpuNv12Converter*)lease.Handle, &texture),
                "prisc_gpu_nv12_texture_acquire_windows");
            return (IntPtr)texture;
        }

        /// <summary>
        /// Releases a texture from <see cref="AcquireTexture"/> and its output view. Needs no
        /// converter (it may already be disposed), runs on any thread, and never throws: a pointer
        /// the crate does not know is a no-op, so a teardown loop over many textures always runs to
        /// the end.
        /// </summary>
        public static void ReleaseTexture(IntPtr texture)
        {
            if (texture == IntPtr.Zero || !InteropLibrary.IsAvailable)
                return;
            NativeMethods.prisc_gpu_nv12_texture_release_windows((void*)texture);
        }

        protected override int Destroy(IntPtr h) => NativeMethods.prisc_gpu_nv12_converter_destroy_windows((PriscGpuNv12Converter*)h);
    }
}
