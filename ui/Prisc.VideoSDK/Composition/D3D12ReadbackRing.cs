using System;
using System.Runtime.Versioning;
using SkiaSharp;
using Interop = Prisc.Interop;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The asynchronous <see cref="IReadbackRing"/> for the Direct3D 12 backend. Each slot owns a
    /// D3D12 texture that prisc_interop creates and this class hands to Skia as a wrapped render
    /// target (<c>GRBackendTexture</c> + <c>SKSurface.Create(context, texture, ...)</c>), a
    /// readback-heap buffer kept permanently mapped, and a one-instruction copy command list.
    /// <see cref="Submit"/> flushes Skia's draws to its direct queue, signals a fence behind them,
    /// and queues the texture to readback copy on a separate copy queue that waits for that fence on
    /// the GPU; the copy queue signals a second fence when the pixels have landed. Nothing on the
    /// composer thread waits for the GPU: <see cref="Wait"/> (the convert stage's thread) blocks on
    /// the second fence and reads the mapped memory in place. Measured in the research harness:
    /// 0.35 ms per frame at 1240x1166 and 1.2 ms at 2480x2332 against 2.1 / 8.1 ms for the
    /// synchronous <c>Flush + ReadPixels</c> path, with bit-exact pixels versus the CPU raster.
    ///
    /// <para><b>What lives where.</b> Everything below the Skia line (textures, readback buffers,
    /// both fences, the copy queue, the slot state machine and a creation self-check of the copy
    /// path) lives in prisc_interop (<see cref="Interop.GpuReadbackRing"/>). What stays here needs
    /// Skia: the per-frame wrapper and a one-frame check that Skia's draws into the wrapped texture
    /// come back top-down BGRA.</para>
    ///
    /// <para><b>Why our own texture, and why re-wrap per frame.</b> SkiaSharp 3.119 exposes no way to
    /// get the D3D12 resource behind a Skia-owned surface, and no way to read or set the resource
    /// state Skia tracks for a wrapped one. So the texture is ours, created with
    /// <c>ALLOW_SIMULTANEOUS_ACCESS</c>: such textures decay to <c>COMMON</c> at the end of every
    /// <c>ExecuteCommandLists</c> and are implicitly promoted on first use in the next one, which is
    /// what makes the cross-queue copy legal without a single barrier. The wrapper is created fresh
    /// at every <see cref="Begin"/> with <c>ResourceState = COMMON</c>, so Skia's tracked state
    /// always matches the decayed reality and its one <c>COMMON to RENDER_TARGET</c> barrier is
    /// exactly right. Re-wrapping measured at no cost.</para>
    ///
    /// <para><b>Shared (zero-copy) protocol.</b> A slot can alternatively be handed to an external
    /// GPU consumer instead of being read back: <see cref="Begin"/>, draw,
    /// <see cref="SubmitShared"/> (flushes Skia and signals the render fence; no copy is queued),
    /// the consumer waits for the render fence at the returned value on its own timeline, reads the
    /// slot texture and signals the consumed fence with the same value, then
    /// <see cref="ReleaseShared"/> (any thread) waits for that signal and makes the slot idle
    /// again. Fence values are one monotonic sequence across both protocols. <see cref="Wait"/> is
    /// not valid for a shared slot.</para>
    ///
    /// <para>Lifetime: create, use and dispose on the composer thread (Skia context affinity); only
    /// the fence waits (<see cref="Wait"/>, <see cref="ReleaseShared"/>) are free-threaded. The device is the factory's, and the crate keeps it alive for as long as
    /// the ring, so disposal order is a tidiness question rather than a safety one.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D12ReadbackRing : IReadbackRing
    {
        /// <summary><c>D3D12_RESOURCE_STATE_COMMON</c>, the state a simultaneous-access slot texture
        /// has decayed to at every <see cref="Begin"/>.</summary>
        private const int ResourceStateCommon = 0;

        /// <summary><c>DXGI_FORMAT_B8G8R8A8_UNORM</c>, the slot textures' format.</summary>
        private const int FormatB8G8R8A8Unorm = 87;

        /// <summary>A GPU that takes this long to finish a copy is hung; device removal signals
        /// fences to UINT64_MAX, so a healthy device never gets here.</summary>
        internal static readonly TimeSpan FenceTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The Skia wrapper that lives between <see cref="Begin"/> and the submit.</summary>
        private sealed class Slot
        {
            public GRBackendTexture BackendTexture;
            public SKSurface Surface;
        }

        private readonly GRContext _context;
        private readonly Interop.GpuDevice _device;
        private readonly Interop.GpuReadbackRing _ring;
        private readonly Slot[] _slots;
        private bool _disposed;

        private D3D12ReadbackRing(GRContext context, Interop.GpuDevice device, Interop.GpuReadbackRing ring)
        {
            _context = context;
            _device = device;
            _ring = ring;
            _slots = new Slot[ring.SlotCount];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new Slot();
        }

        public int Width => _ring.Width;

        public int Height => _ring.Height;

        public int SlotCount => _slots.Length;

        public string Description =>
            $"Direct3D 12 copy-queue readback, {SlotCount} slots, row pitch {_ring.RowPitch}" +
            (Shareable ? ", shareable" : "");

        /// <summary>True when every slot texture and both fences carry the shared-handle flags
        /// (the zero-copy encode path needs them; readback does not). Never a partial set.</summary>
        public bool Shareable => _ring.Shareable;

        /// <summary>The device the ring was built on; the encode bridge's converter must use it.</summary>
        internal Interop.GpuDevice Device => _device;

        /// <summary>The native ring, which the encode bridge's converter is built over.</summary>
        internal Interop.GpuReadbackRing Ring => _ring;

        /// <summary>
        /// Builds the ring, or returns null with a reason when any D3D12 or Skia step fails (an old
        /// driver without copy-queue support, a wrapped-texture surface Skia refuses, VRAM
        /// exhaustion, a failed self-check). Failure is all-or-nothing; the caller falls back to
        /// <see cref="SyncReadbackRing"/>. The crate self-checks the copy path at creation; a
        /// one-frame Skia round trip then checks the wrapped surface, so a misbehaving driver
        /// degrades at startup rather than corrupting a render.
        /// </summary>
        public static D3D12ReadbackRing TryCreate(GRContext context, Interop.GpuDevice device,
            int width, int height, int slots, out string failureReason)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(device);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slots, 0);

            var native = Interop.GpuReadbackRing.TryCreate(device, width, height, slots, out failureReason, out _);
            if (native == null)
                return null;

            D3D12ReadbackRing ring = null;
            try
            {
                ring = new D3D12ReadbackRing(context, device, native);
                native = null; // the ring owns it now

                if (!ring.SkiaRoundTrip(out failureReason))
                    return null;

                var built = ring;
                ring = null; // ownership passes to the caller
                failureReason = null;
                return built;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Interop.InteropException)
            {
                // SKSurface.Create over a wrapped texture returning null, a native call failing,
                // or the round trip's fence never signalling: all "no async ring on this driver".
                failureReason = ex.Message;
                return null;
            }
            finally
            {
                ring?.Dispose();
                native?.Dispose();
            }
        }

        /// <summary>One Begin, Skia clear, Submit, Wait round trip on slot 0: the first and last
        /// pixels must carry the cleared colour in BGRA memory order, which catches a wrapped
        /// surface Skia draws with the wrong format or origin.</summary>
        private unsafe bool SkiaRoundTrip(out string failureReason)
        {
            var probe = new SKColor(0x11, 0x22, 0x33); // R, G, B, so memory B G R A = 33 22 11 FF
            Begin(0).Clear(probe);
            Submit(0);
            var pixels = Wait(0);
            byte* first = (byte*)pixels.Address;
            byte* last = first + (long)(Height - 1) * pixels.RowBytes + (Width - 1) * 4;
            foreach (var p in new[] { (IntPtr)first, (IntPtr)last })
            {
                byte* px = (byte*)p;
                if (px[0] != 0x33 || px[1] != 0x22 || px[2] != 0x11 || px[3] != 0xFF)
                {
                    failureReason = $"Readback round trip returned {px[0]:X2}{px[1]:X2}{px[2]:X2}{px[3]:X2}, expected 332211FF.";
                    return false;
                }
            }

            failureReason = null;
            return true;
        }

        public SKCanvas Begin(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            IntPtr texture = _ring.Begin(slot);
            try
            {
                var info = new GRD3DTextureResourceInfo
                {
                    Resource = texture,
                    ResourceState = ResourceStateCommon, // decayed: see class notes
                    Format = FormatB8G8R8A8Unorm,
                    SampleCount = 1,
                    LevelCount = 1,
                };
                s.BackendTexture = new GRBackendTexture(Width, Height, info);
                // TopLeft is required: the texture memory is read directly, and only this origin
                // stores rows top-down (the overloads that omit it default to BottomLeft).
                s.Surface = SKSurface.Create(_context, s.BackendTexture, GRSurfaceOrigin.TopLeft, 1,
                    SKColorType.Bgra8888);
                if (s.Surface == null)
                    throw new InvalidOperationException(
                        "SKSurface.Create over the wrapped D3D12 texture returned null.");
                return s.Surface.Canvas;
            }
            catch
            {
                DisposeWrapper(s);
                _ring.CancelBegin(slot);
                throw;
            }
        }

        public void Submit(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushAndDisposeWrapper(slot);
            _ring.Submit(slot);
        }

        /// <summary>
        /// Ends drawing on <paramref name="slot"/> for an external GPU consumer (the shared
        /// protocol in the class notes): Skia's draws are submitted and the render fence signalled
        /// behind them with the returned value; no readback is queued. Composer thread only.
        /// </summary>
        public ulong SubmitShared(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushAndDisposeWrapper(slot);
            return _ring.SubmitShared(slot);
        }

        /// <summary>Submits Skia's draws for a begun slot on the direct queue and drops the wrapper
        /// (Skia keeps the texture alive until its command list completed). A slot that was never
        /// begun is the <see cref="InvalidOperationException"/> the interface documents.</summary>
        private void FlushAndDisposeWrapper(int slot)
        {
            var s = SlotAt(slot);
            if (s.Surface == null)
                throw new InvalidOperationException($"Readback slot {slot} has not been begun.");
            s.Surface.Flush(submit: true, synchronous: false);
            DisposeWrapper(s);
        }

        public ReadbackPixels Wait(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSlot(slot);
            var (address, rowBytes) = _ring.Wait(slot, FenceTimeout);
            return new ReadbackPixels(address, rowBytes);
        }

        /// <summary>Blocks until the external consumer of a <see cref="SubmitShared"/> slot has
        /// signalled the consumed fence for it, then makes the slot idle. Any thread.</summary>
        public void ReleaseShared(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSlot(slot);
            _ring.ReleaseShared(slot, FenceTimeout);
        }

        private Slot SlotAt(int slot)
        {
            ValidateSlot(slot);
            return _slots[slot];
        }

        /// <summary>The range check stays on this side, so a slot out of range is an
        /// <see cref="ArgumentOutOfRangeException"/> naming <c>slot</c>.</summary>
        private void ValidateSlot(int slot)
            => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_slots.Length, nameof(slot));

        private static void DisposeWrapper(Slot s)
        {
            s.Surface?.Dispose();
            s.Surface = null;
            s.BackendTexture?.Dispose();
            s.BackendTexture = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var s in _slots)
                DisposeWrapper(s); // a Begin that never reached Submit (an exception mid-compose)

            // Skia must be done with the textures (it references them per wrapper until its command
            // list completes) before the crate's own drain runs.
            try
            {
                _context.Flush();
                _context.Submit(synchronous: true);
            }
            catch (Exception)
            {
                // a removed device or a hung GPU: nothing left to flush, release what we hold
            }

            // The ordered drain gets the full fence timeout; the destroy after it then only unmaps
            // the readback buffers and closes the wait event. (A ring that reaches the crate's
            // destroy any other way, a finalizer included, drains for at most two seconds.)
            try
            {
                _ring.Drain(FenceTimeout);
            }
            catch (Exception)
            {
                // a removed device or a hung GPU: release what we hold regardless
            }

            _ring.Dispose();
        }
    }
}
