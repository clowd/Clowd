using System;
using System.Runtime.Versioning;
using Prisc.VideoSDK.Media;
using Interop = Prisc.Interop;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The zero-copy encode path on Windows: hands the <see cref="D3D12ReadbackRing"/>'s render
    /// targets to a Direct3D 11 device on the same adapter and turns each composed frame into an
    /// NV12 texture NVENC or AMF read directly (<see cref="HardwareFrames"/>), so the picture never
    /// leaves video memory: no readback DMA, no swscale, no encoder-side upload.
    ///
    /// <para><b>Mechanism.</b> prisc_interop's <see cref="Interop.GpuNv12Converter"/> opens the
    /// ring's slot textures and fences on the composer device's Direct3D 11 twin (same adapter,
    /// checked by LUID) and runs a video processor. Per frame, on the pipeline's convert thread, it
    /// queues a GPU wait for the render fence at the value <see cref="D3D12ReadbackRing.SubmitShared"/>
    /// returned (the composer thread never blocks), the blit from the shared BGRA texture into an
    /// NV12 texture, a signal of the consumed fence with the same value (what
    /// <see cref="D3D12ReadbackRing.ReleaseShared"/> waits for before Skia may draw into the slot
    /// again) and a flush. The NV12 texture goes to the encoder as a <see cref="HardwareFrame"/> and
    /// returns to the <see cref="EncoderTexturePool"/> when the encoder drops its reference, the
    /// only correct "done reading" signal. So the ring keeps its few slots, each free again as soon
    /// as its blit has run, while the encoder's in-flight depth (NVENC maps about 15 inputs at
    /// these settings) is absorbed by the NV12 pool, whose textures are a third the size.</para>
    ///
    /// <para><b>Why a GPU colour conversion rather than BGRA frames.</b> NVENC does accept BGRA
    /// D3D11 textures but converts them in the driver with a fixed BT.601-limited matrix and tags
    /// the stream BT.470BG, and AMF's FFmpeg 7.1 encoder takes no RGB D3D11 format at all. The
    /// video processor runs with an explicit BT.601 matrix, full-range RGB in, 16 to 235 out and
    /// driver auto-processing off: the same matrix swscale applies on the readback path, so the
    /// zero-copy output matches the fallback it may silently be replaced by. A one-frame self-check
    /// at creation (in the crate) converts a known colour and reads the NV12 back, so a driver that
    /// ignores the colour-space flags (or a broken share) degrades to readback at startup rather
    /// than producing a wrong render.</para>
    ///
    /// <para>Lifetime: create on the ring's (composer) thread, because the self-check draws through
    /// the ring's slot 0, which must be idle; call <see cref="Convert"/> from one thread at a time (the convert stage);
    /// dispose after the encoder opened over <see cref="Frames"/> was closed and before the ring.
    /// Creation is all-or-nothing and leaves slot 0 idle, so the caller stays on readback over the
    /// same ring.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11EncodeBridge : IEncodeBridge
    {
        private readonly D3D12ReadbackRing _ring;
        private Interop.GpuNv12Converter _converter;
        private HardwareFrames _frames;
        private bool _converted;
        private bool _disposed;

        private D3D11EncodeBridge(D3D12ReadbackRing ring)
        {
            _ring = ring;
        }

        /// <summary>The encoder-facing side: what <see cref="Mp4WriterOptions.HardwareFrames"/> takes.</summary>
        public HardwareFrames Frames => _frames;

        public int Width => _ring.Width;

        public int Height => _ring.Height;

        /// <summary>For the diagnostic line that names the encode path.</summary>
        public string Description =>
            $"Direct3D 11 zero-copy: {_ring.SlotCount} shared render targets, VideoProcessor BGRA->NV12 (BT.601 limited) into pooled encoder textures";

        /// <summary>The ring's <see cref="D3D12ReadbackRing.SubmitShared"/> for the slot.</summary>
        public ulong SubmitFrame(int slot) => _ring.SubmitShared(slot);

        /// <summary>The ring's <see cref="D3D12ReadbackRing.ReleaseShared"/> for the slot: waits for
        /// the consumed-fence signal <see cref="Convert"/> queued behind the slot's blit.</summary>
        public void ReleaseSlot(int slot) => _ring.ReleaseShared(slot);

        /// <summary>
        /// Builds the bridge over a shareable ring, or returns null with the reason. Must run on the
        /// ring's thread (the converter's creation self-checks through slot 0, which must be idle
        /// and is idle again on return either way).
        /// </summary>
        /// <param name="missingCapability">True when the crate answered that this machine simply
        /// cannot run the bridge (<see cref="Interop.InteropStatus.Capability"/>: no shareable ring,
        /// no Direct3D 11.4 runtime, or a Direct3D 11 device without video processing, as on the
        /// GitHub Windows runner's virtual GPU) as opposed to something going wrong on a capable
        /// one, a wrong self-check conversion included. Tests skip on the former and fail on the
        /// latter; the render falls back to readback on both.</param>
        /// <param name="afterBuilt">For tests: runs once the converter (self-checked) and the frames
        /// context are up, just before the bridge is handed out. Throwing from it stands in for a
        /// late failure, which must leave nothing behind.</param>
        public static D3D11EncodeBridge TryCreate(D3D12ReadbackRing ring, out string failureReason,
            out bool missingCapability, Action<D3D11EncodeBridge> afterBuilt = null)
        {
            ArgumentNullException.ThrowIfNull(ring);
            missingCapability = false;

            var bridge = new D3D11EncodeBridge(ring);
            try
            {
                // One native call builds the converter and runs its self-check, recovery of slot 0
                // included.
                bridge._converter = Interop.GpuNv12Converter.TryCreate(ring.Device, ring.Ring, out failureReason, out var status);
                if (bridge._converter == null)
                {
                    missingCapability = status == Interop.InteropStatus.Capability;
                    return null;
                }

                bridge._frames = new HardwareFrames(ring.Device, ring.Width, ring.Height,
                    bridge._converter.AcquireTexture, Interop.GpuNv12Converter.ReleaseTexture,
                    "Direct3D 11 NV12 textures (VideoProcessor BGRA->NV12, BT.601 limited)");

                afterBuilt?.Invoke(bridge);

                var built = bridge;
                bridge = null; // ownership passes to the caller
                failureReason = null;
                return built;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Interop.InteropException
                                       or DllNotFoundException or EntryPointNotFoundException)
            {
                // a native call refused mid-way, a fence that never signalled, or no FFmpeg
                // D3D11VA: every one of them means "readback on this machine"
                failureReason = ex.Message;
                return null;
            }
            finally
            {
                bridge?.Dispose();
            }
        }

        // -------------------------------------------------------------------- per frame

        /// <summary>
        /// Converts the slot the ring submitted with <see cref="D3D12ReadbackRing.SubmitShared"/>
        /// (which returned <paramref name="renderFenceValue"/>) into a pooled NV12 texture and
        /// returns it as a frame for the encoder. Nothing here waits on the CPU; the ring's
        /// <see cref="D3D12ReadbackRing.ReleaseShared"/> for the slot may follow at once. One
        /// thread at a time (the bridge's own bookkeeping is not synchronised; the native convert
        /// is).
        /// </summary>
        public HardwareFrame Convert(int slot, ulong renderFenceValue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_ring.SlotCount, nameof(slot));

            IntPtr texture = _frames.RentTexture();
            try
            {
                _converter.Convert(slot, renderFenceValue, texture, out bool signalQueued);
                _converted |= signalQueued;
            }
            catch
            {
                _frames.ReturnTexture(texture);
                throw;
            }

            return _frames.Wrap(texture);
        }

        /// <summary>Reads an NV12 pool texture back to the CPU (a staging copy: slow, for tests):
        /// the Y plane (<c>Width * Height</c> bytes) and the interleaved UV plane (half that),
        /// tightly packed. Waits for every queued conversion to complete first.</summary>
        internal (byte[] Y, byte[] Uv) ReadNv12(IntPtr texture)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int w = _ring.Width, h = _ring.Height;
            var y = new byte[w * h];
            var uv = new byte[w * h / 2];
            _converter.Read(texture, y, uv);
            return (y, uv);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Every blit this bridge queued must have finished reading the ring's textures and
            // writing the pool's before either goes away. This is the ordered drain with the full
            // fence timeout; the converter's own destroy only backs it up with a short bound.
            if (_converted && _converter != null)
            {
                try
                {
                    _converter.Drain(D3D12ReadbackRing.FenceTimeout);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ObjectDisposedException
                                           or Interop.InteropException)
                {
                    // a removed device or a ring already torn down: nothing left to wait for
                }
            }

            // The pool releases its textures here; the encoder that referenced them must already
            // be closed (the caller's ordering contract). A texture FFmpeg still holds is safe to
            // release later: the crate keeps its converter alive until it comes back.
            _frames?.Dispose();
            _frames = null;
            _converter?.Dispose();
            _converter = null;
        }
    }
}
