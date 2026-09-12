using System;
using System.Runtime.Versioning;
using System.Threading;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The asynchronous <see cref="IReadbackRing"/> for the Direct3D 12 backend. Each slot owns a
    /// D3D12 texture that we create ourselves and hand to Skia as a wrapped render target
    /// (<c>GRBackendTexture</c> + <c>SKSurface.Create(context, texture, ...)</c>), a readback-heap
    /// buffer kept permanently mapped, and a one-instruction copy command list. <see cref="Submit"/>
    /// flushes Skia's draws to its direct queue, signals a fence behind them, and queues the
    /// texture → readback copy on a separate copy queue that waits for that fence on the GPU; the
    /// copy queue signals a second fence when the pixels have landed. Nothing on the composer
    /// thread waits for the GPU — <see cref="Wait"/> (the convert stage's thread) blocks on the
    /// second fence and reads the mapped memory in place. Measured in the research harness: 0.35 ms
    /// per frame at 1240x1166 and 1.2 ms at 2480x2332 against 2.1 / 8.1 ms for the synchronous
    /// <c>Flush + ReadPixels</c> path, with bit-exact pixels versus the CPU raster.
    ///
    /// <para><b>Why our own texture, and why re-wrap per frame.</b> SkiaSharp 3.119 exposes no way to
    /// get the D3D12 resource behind a Skia-owned surface, and no way to read or set the resource
    /// state Skia tracks for a wrapped one. So the texture is ours, created with
    /// <c>ALLOW_SIMULTANEOUS_ACCESS</c>: such textures decay to <c>COMMON</c> at the end of every
    /// <c>ExecuteCommandLists</c> and are implicitly promoted on first use in the next one, which is
    /// what makes the cross-queue copy legal without a single barrier of our own. The wrapper is
    /// created fresh at every <see cref="Begin"/> with <c>ResourceState = COMMON</c>, so Skia's
    /// tracked state always matches the decayed reality and its one <c>COMMON → RENDER_TARGET</c>
    /// barrier is exactly right; a persistent wrapper would keep tracking <c>RENDER_TARGET</c> and
    /// rely on implicit promotion for writes, which renders correctly but trips a (since fixed)
    /// debug-layer validation error. Re-wrapping measured at no cost.</para>
    ///
    /// <para>Textures and fences are created shareable (<c>D3D12_HEAP_FLAG_SHARED</c>,
    /// <c>D3D12_FENCE_FLAG_SHARED</c>) when the driver allows, so a D3D11 device can open them for
    /// the zero-copy hardware-encode path (<see cref="D3D11EncodeBridge"/>) without re-allocating
    /// the ring; the readback path does not depend on it. The flags are all-or-nothing: if the
    /// driver refuses any one fence or slot texture, every one of them is re-created without the
    /// flag, so <see cref="Shareable"/> describes each object the zero-copy path would open, never
    /// a mixed set.</para>
    ///
    /// <para><b>Shared (zero-copy) protocol.</b> A slot can alternatively be handed to an external
    /// GPU consumer instead of being read back: <see cref="Begin"/> → draw →
    /// <see cref="SubmitShared"/> (flushes Skia and signals the render fence; no copy is queued)
    /// → the consumer makes its GPU wait for the render fence at the returned value, reads the
    /// slot texture and signals the consumed fence with the same value from its own context →
    /// <see cref="ReleaseShared"/> (any thread) waits for that signal and makes the slot idle
    /// again. So a slot is reused by Skia only after the consumer's GPU has finished reading it;
    /// whether the consumer's own downstream (an encoder) is done with what it produced from the
    /// slot is the consumer's business, not the ring's. Fence values are one monotonic sequence
    /// across both protocols, and the consumer must signal exactly the value it was given, in
    /// order. <see cref="Wait"/> is not valid for a shared slot (its readback buffer holds nothing).</para>
    ///
    /// <para>Lifetime: create, use and dispose on the composer thread (Skia context affinity); only
    /// the fence waits — <see cref="Wait"/>, <see cref="ReleaseShared"/>, <see cref="WaitConsumed"/>
    /// — are free-threaded. The adapter, device and direct queue are borrowed from the factory,
    /// which must outlive the ring.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class D3D12ReadbackRing : IReadbackRing
    {
        private const int Idle = 0, Composing = 1, InFlight = 2;

        /// <summary>A GPU that takes this long to finish a copy is hung; device removal signals
        /// fences to UINT64_MAX, so a healthy device never gets here.</summary>
        private static readonly TimeSpan FenceTimeout = TimeSpan.FromSeconds(30);

        private sealed class Slot
        {
            public IntPtr Texture;      // ID3D12Resource, DEFAULT heap, B8G8R8A8_UNORM render target
            public IntPtr Readback;     // ID3D12Resource, READBACK heap buffer, mapped for life
            public IntPtr Allocator;    // ID3D12CommandAllocator (copy)
            public IntPtr CopyList;     // ID3D12GraphicsCommandList (copy), recorded once, never reset
            public byte* Mapped;
            public GRBackendTexture BackendTexture; // live between Begin and Submit
            public SKSurface Surface;               // live between Begin and Submit
            public ulong FenceValue;                // consumedFence value that means "pixels landed" / "consumer done"
            public bool Shared;                     // submitted with SubmitShared: released by ReleaseShared, not Wait
            public int State;
        }

        private readonly GRContext _context;
        private readonly IntPtr _adapter;     // borrowed (the factory's IDXGIAdapter1)
        private readonly IntPtr _device;      // borrowed
        private readonly IntPtr _directQueue; // borrowed (Skia's)
        private readonly Slot[] _slots;
        private readonly int _rowPitch;
        private bool _shareable; // set by TryCreateSharedObjects once every fence and texture agrees
        private IntPtr _copyQueue, _renderFence, _consumedFence;
        // one event for all CPU waits; Wait() serializes on _waitSync and re-checks the fence
        // value after every wake, so a stale signal from an earlier SetEventOnCompletion is harmless
        private readonly AutoResetEvent _fenceEvent = new AutoResetEvent(false);
        private readonly object _waitSync = new object();
        private ulong _lastSignaled; // fence values are frame-monotonic and never reused
        private bool _disposed;

        private D3D12ReadbackRing(GRContext context, IntPtr adapter, IntPtr device, IntPtr directQueue,
            int width, int height, int slots, int rowPitch)
        {
            _context = context;
            _adapter = adapter;
            _device = device;
            _directQueue = directQueue;
            Width = width;
            Height = height;
            _rowPitch = rowPitch;
            _slots = new Slot[slots];
            for (int i = 0; i < slots; i++)
                _slots[i] = new Slot();
        }

        public int Width { get; }

        public int Height { get; }

        public int SlotCount => _slots.Length;

        public string Description =>
            $"Direct3D 12 copy-queue readback, {SlotCount} slots, row pitch {_rowPitch}" +
            (_shareable ? ", shareable" : "");

        /// <summary>True when every slot texture and both fences carry the shared-handle flags
        /// (the zero-copy encode path needs them; readback does not). Never true for a partial
        /// set: see the class notes.</summary>
        public bool Shareable => _shareable;

        /// <summary>The IDXGIAdapter1 the device runs on (borrowed from the factory): what a
        /// consumer creates its own device on so the shared textures are on the same GPU.</summary>
        internal IntPtr Adapter => _adapter;

        /// <summary>The ID3D12Device (borrowed): the consumer creates shared handles with it.</summary>
        internal IntPtr Device => _device;

        /// <summary>The fence Skia's direct queue signals after each submitted slot; shareable when
        /// <see cref="Shareable"/>.</summary>
        internal IntPtr RenderFence => _renderFence;

        /// <summary>The fence a slot's consumer (the copy queue, or the external consumer under
        /// the shared protocol) signals when it has finished reading the slot texture.</summary>
        internal IntPtr ConsumedFence => _consumedFence;

        /// <summary>The ID3D12Resource of a slot's render target (borrowed; valid for the ring's lifetime).</summary>
        internal IntPtr SlotTexture(int slot) => SlotAt(slot).Texture;

        /// <summary>
        /// Builds the ring, or returns null with a reason when any D3D12 or Skia step fails (an
        /// old driver without copy-queue support, a wrapped-texture surface Skia refuses, VRAM
        /// exhaustion). Failure is all-or-nothing and leaks nothing; the caller falls back to
        /// <see cref="SyncReadbackRing"/>. A one-frame round trip verifies the pixels come back
        /// top-down BGRA before the ring is handed out, so a misbehaving driver degrades at
        /// startup rather than corrupting a render.
        /// </summary>
        public static D3D12ReadbackRing TryCreate(GRContext context, IntPtr adapter, IntPtr device, IntPtr directQueue,
            int width, int height, int slots, out string failureReason)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slots, 0);
            if (adapter == IntPtr.Zero || device == IntPtr.Zero || directQueue == IntPtr.Zero)
            {
                failureReason = "No D3D12 device.";
                return null;
            }

            var texDesc = new D3D12Backend.D3D12_RESOURCE_DESC
            {
                Dimension = D3D12Backend.ResourceDimensionTexture2D,
                Width = (ulong)width,
                Height = (uint)height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = D3D12Backend.FormatB8G8R8A8Unorm,
                SampleCount = 1,
                Layout = D3D12Backend.TextureLayoutUnknown,
                Flags = D3D12Backend.ResourceFlagAllowRenderTarget | D3D12Backend.ResourceFlagAllowSimultaneousAccess,
            };
            D3D12Backend.GetCopyableFootprints(device, texDesc, out var footprint, out uint numRows,
                out ulong rowSize, out ulong totalBytes);
            if (numRows != (uint)height || rowSize != (ulong)width * 4 || footprint.Offset != 0
                || footprint.Footprint.RowPitch < (uint)width * 4 || totalBytes > int.MaxValue)
            {
                failureReason = $"Unexpected copyable footprint for {width}x{height} BGRA " +
                    $"(rows {numRows}, row {rowSize} B, pitch {footprint.Footprint.RowPitch}, total {totalBytes} B).";
                return null;
            }

            D3D12ReadbackRing ring = null;
            try
            {
                ring = new D3D12ReadbackRing(context, adapter, device, directQueue, width, height, slots,
                    (int)footprint.Footprint.RowPitch);

                // Shared fences and slot textures are what a later D3D11 zero-copy path opens; a
                // driver that refuses the flag on any of them gets the whole set re-created
                // non-shared, so Shareable is exact and the readback ring works either way.
                if (!ring.TryCreateSharedObjects(texDesc, shared: true, out int hr, out string step)
                    && !ring.TryCreateSharedObjects(texDesc, shared: false, out hr, out step))
                {
                    failureReason = $"{step} failed (0x{hr:X8}).";
                    return null;
                }

                hr = D3D12Backend.CreateCommandQueue(device, D3D12Backend.CommandListTypeCopy, out ring._copyQueue);
                if (hr < 0)
                {
                    failureReason = $"CreateCommandQueue(COPY) failed (0x{hr:X8}).";
                    return null;
                }

                var bufDesc = new D3D12Backend.D3D12_RESOURCE_DESC
                {
                    Dimension = D3D12Backend.ResourceDimensionBuffer,
                    Width = totalBytes,
                    Height = 1,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleCount = 1,
                    Layout = D3D12Backend.TextureLayoutRowMajor,
                };

                for (int i = 0; i < slots; i++)
                {
                    var slot = ring._slots[i];

                    // READBACK heaps must be created in COPY_DEST and stay there forever.
                    hr = D3D12Backend.CreateCommittedResource(device, D3D12Backend.HeapTypeReadback,
                        D3D12Backend.HeapFlagNone, bufDesc, D3D12Backend.ResourceStateCopyDest, out slot.Readback);
                    if (hr < 0)
                    {
                        failureReason = $"CreateCommittedResource({totalBytes} B readback) failed (0x{hr:X8}).";
                        return null;
                    }

                    // Mapped once and kept: READBACK heaps are write-back cached system memory
                    // (UPLOAD heaps are the uncached ones), so the convert stage reads in place.
                    hr = D3D12Backend.ResourceMap(slot.Readback, out void* mapped);
                    if (hr < 0)
                    {
                        failureReason = $"Map(readback) failed (0x{hr:X8}).";
                        return null;
                    }
                    slot.Mapped = (byte*)mapped;

                    hr = D3D12Backend.CreateCommandAllocator(device, D3D12Backend.CommandListTypeCopy, out slot.Allocator);
                    if (hr < 0)
                    {
                        failureReason = $"CreateCommandAllocator(COPY) failed (0x{hr:X8}).";
                        return null;
                    }

                    hr = D3D12Backend.CreateCommandList(device, D3D12Backend.CommandListTypeCopy, slot.Allocator, out slot.CopyList);
                    if (hr < 0)
                    {
                        failureReason = $"CreateCommandList(COPY) failed (0x{hr:X8}).";
                        return null;
                    }

                    // The one copy this slot ever does, recorded once and re-executed per frame:
                    // texture subresource 0 → the readback buffer at the placed footprint.
                    var dst = new D3D12Backend.D3D12_TEXTURE_COPY_LOCATION
                    {
                        pResource = slot.Readback,
                        Type = D3D12Backend.TextureCopyTypePlacedFootprint,
                        PlacedFootprint = footprint,
                    };
                    var src = new D3D12Backend.D3D12_TEXTURE_COPY_LOCATION
                    {
                        pResource = slot.Texture,
                        Type = D3D12Backend.TextureCopyTypeSubresourceIndex,
                        SubresourceIndex = 0,
                    };
                    D3D12Backend.CommandListCopyTextureRegion(slot.CopyList, dst, src);
                    hr = D3D12Backend.CommandListClose(slot.CopyList);
                    if (hr < 0)
                    {
                        failureReason = $"CommandList.Close failed (0x{hr:X8}).";
                        return null;
                    }
                }

                if (!ring.SmokeTest(out failureReason))
                    return null;

                var built = ring;
                ring = null; // ownership passes to the caller
                failureReason = null;
                return built;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // SKSurface.Create over a wrapped texture returning null, a COM call failing, or
                // the smoke test's fence never signalling — all "no async ring on this driver".
                failureReason = ex.Message;
                return null;
            }
            finally
            {
                ring?.Dispose();
            }
        }

        /// <summary>
        /// Creates the two fences and every slot's render-target texture with or without the
        /// shared-handle flags, as one unit: on any failure everything this call created is
        /// released again and false is returned, so the caller can retry the whole set the other
        /// way. Sharing a texture needs <c>ALLOW_SIMULTANEOUS_ACCESS</c> (always set here) plus
        /// driver support, which is the part that may be missing.
        /// </summary>
        private bool TryCreateSharedObjects(in D3D12Backend.D3D12_RESOURCE_DESC texDesc, bool shared,
            out int hr, out string step)
        {
            int fenceFlags = shared ? D3D12Backend.FenceFlagShared : D3D12Backend.FenceFlagNone;
            int heapFlags = shared ? D3D12Backend.HeapFlagShared : D3D12Backend.HeapFlagNone;
            string flagNote = shared ? " (shared)" : "";

            step = "CreateFence" + flagNote;
            hr = D3D12Backend.CreateFence(_device, fenceFlags, out _renderFence);
            if (hr >= 0)
                hr = D3D12Backend.CreateFence(_device, fenceFlags, out _consumedFence);
            if (hr >= 0)
            {
                step = $"CreateCommittedResource({Width}x{Height} render target{flagNote})";
                for (int i = 0; hr >= 0 && i < _slots.Length; i++)
                {
                    hr = D3D12Backend.CreateCommittedResource(_device, D3D12Backend.HeapTypeDefault, heapFlags,
                        texDesc, D3D12Backend.ResourceStateCommon, out _slots[i].Texture);
                }
            }

            if (hr >= 0)
            {
                _shareable = shared;
                return true;
            }

            ReleaseSharedObjects();
            return false;
        }

        private void ReleaseSharedObjects()
        {
            foreach (var s in _slots)
            {
                D3D12Backend.Release(s.Texture);
                s.Texture = IntPtr.Zero;
            }
            D3D12Backend.Release(_consumedFence);
            D3D12Backend.Release(_renderFence);
            _consumedFence = _renderFence = IntPtr.Zero;
            _shareable = false;
        }

        /// <summary>One full Begin → clear → Submit → Wait round trip on slot 0, checking that the
        /// first and last pixels carry the cleared colour in BGRA memory order — catches a
        /// wrapped-surface origin or format mismatch before any real frame goes through.</summary>
        private bool SmokeTest(out string failureReason)
        {
            var probe = new SKColor(0x11, 0x22, 0x33); // R, G, B → memory B G R A = 33 22 11 FF
            var canvas = Begin(0);
            canvas.Clear(probe);
            Submit(0);
            var pixels = Wait(0);
            byte* first = (byte*)pixels.Address;
            byte* last = first + (long)(Height - 1) * pixels.RowBytes + (Width - 1) * 4;
            foreach (var p in new[] { first, last })
            {
                if (p[0] != 0x33 || p[1] != 0x22 || p[2] != 0x11 || p[3] != 0xFF)
                {
                    failureReason = $"Readback round trip returned {p[0]:X2}{p[1]:X2}{p[2]:X2}{p[3]:X2}, expected 332211FF.";
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
            Transition(s, Idle, Composing);
            try
            {
                var info = new GRD3DTextureResourceInfo
                {
                    Resource = s.Texture,
                    ResourceState = D3D12Backend.ResourceStateCommon, // decayed: see class notes
                    Format = D3D12Backend.FormatB8G8R8A8Unorm,
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
                Volatile.Write(ref s.State, Idle);
                throw;
            }
        }

        public void Submit(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            s.Shared = false; // written before the state transition publishes the slot (see Wait)
            Transition(s, Composing, InFlight);

            ulong value = FlushAndSignalRender(s);
            Check(D3D12Backend.QueueWait(_copyQueue, _renderFence, value), "ID3D12CommandQueue::Wait");
            D3D12Backend.ExecuteCommandList(_copyQueue, s.CopyList);
            Check(D3D12Backend.QueueSignal(_copyQueue, _consumedFence, value), "ID3D12CommandQueue::Signal");
            _lastSignaled = value;
            s.FenceValue = value;
        }

        /// <summary>
        /// Ends drawing on <paramref name="slot"/> for an external GPU consumer (the shared
        /// protocol in the class notes): Skia's draws are submitted and the render fence signalled
        /// behind them with the returned value; no readback is queued. The consumer waits for the
        /// render fence at that value on its own GPU timeline, reads
        /// <see cref="SlotTexture"/>, and signals the consumed fence with the same value; the
        /// slot then goes idle through <see cref="ReleaseShared"/>. Composer thread only.
        /// </summary>
        public ulong SubmitShared(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            s.Shared = true;
            Transition(s, Composing, InFlight);

            ulong value = FlushAndSignalRender(s);
            _lastSignaled = value;
            s.FenceValue = value;
            return value;
        }

        /// <summary>Submits Skia's draws for a slot on the direct queue and signals the render
        /// fence with the next value; returns that value. Shared by both submit protocols.</summary>
        private ulong FlushAndSignalRender(Slot s)
        {
            // Skia closes its command list and executes it on the direct queue; nothing waits.
            s.Surface.Flush(submit: true, synchronous: false);
            // The wrapper goes now: Skia keeps the texture alive until its command list has
            // completed, and the next Begin starts tracking from COMMON again.
            DisposeWrapper(s);

            // _lastSignaled is what Dispose waits for, so it must only ever hold a value the
            // consumed fence will actually reach: a Check that throws (a Signal/Wait refused
            // with something other than device removal) leaves it at the previous, accepted
            // value instead of parking teardown on the 30 s fence timeout.
            ulong value = _lastSignaled + 1;
            Check(D3D12Backend.QueueSignal(_directQueue, _renderFence, value), "ID3D12CommandQueue::Signal");
            return value;
        }

        public ReadbackPixels Wait(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            if (Volatile.Read(ref s.State) != InFlight)
                throw new InvalidOperationException($"Readback slot {slot} has not been submitted.");
            if (s.Shared)
                throw new InvalidOperationException($"Readback slot {slot} was submitted for an external consumer; use ReleaseShared.");

            WaitForFence(s.FenceValue);
            Volatile.Write(ref s.State, Idle);
            return new ReadbackPixels((IntPtr)s.Mapped, _rowPitch);
        }

        /// <summary>Blocks until the external consumer of a <see cref="SubmitShared"/> slot has
        /// signalled the consumed fence for it (its GPU has finished reading the texture), then
        /// makes the slot idle. Any thread.</summary>
        public void ReleaseShared(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            if (Volatile.Read(ref s.State) != InFlight)
                throw new InvalidOperationException($"Readback slot {slot} has not been submitted.");
            if (!s.Shared)
                throw new InvalidOperationException($"Readback slot {slot} was submitted for readback; use Wait.");

            WaitForFence(s.FenceValue);
            Volatile.Write(ref s.State, Idle);
        }

        /// <summary>
        /// Makes a <see cref="SubmitShared"/> slot idle again when its consumer failed before it
        /// could signal the consumed fence (a refused GPU wait or blit, a pool allocation that
        /// threw), so the slot is not stuck in flight for whoever uses the ring next — the
        /// readback fallback after a failed zero-copy bridge draws into slot 0 first of all.
        /// Nothing is waited for: the caller vouches that no GPU read of the slot is still
        /// pending. When the fence has not reached the slot's value it is signalled to it from
        /// the CPU (<c>ID3D12Fence::Signal</c>), because <c>_lastSignaled</c> already counts that
        /// value as one the fence will reach (<see cref="Dispose"/> and <see cref="WaitConsumed"/>
        /// rely on that); a fence that is already past it — the consumer's own signal did land,
        /// or the device was removed — is left alone, never moved backwards. Any thread.
        /// </summary>
        internal void AbandonShared(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = SlotAt(slot);
            if (Volatile.Read(ref s.State) != InFlight)
                throw new InvalidOperationException($"Readback slot {slot} has not been submitted.");
            if (!s.Shared)
                throw new InvalidOperationException($"Readback slot {slot} was submitted for readback; use Wait.");

            lock (_waitSync)
            {
                if (D3D12Backend.FenceGetCompletedValue(_consumedFence) < s.FenceValue)
                    Check(D3D12Backend.FenceSignal(_consumedFence, s.FenceValue), "ID3D12Fence::Signal");
            }
            Volatile.Write(ref s.State, Idle);
        }

        /// <summary>Blocks until the consumed fence has reached <paramref name="value"/> — for a
        /// consumer that wants to know its own last signal has landed before it goes away.</summary>
        internal void WaitConsumed(ulong value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WaitForFence(value);
        }

        private void WaitForFence(ulong value)
        {
            lock (_waitSync)
            {
                while (true)
                {
                    ulong completed = D3D12Backend.FenceGetCompletedValue(_consumedFence);
                    if (completed == ulong.MaxValue)
                        throw new InvalidOperationException("The Direct3D 12 device was removed during the render.");
                    if (completed >= value)
                        return;

                    Check(D3D12Backend.FenceSetEventOnCompletion(_consumedFence, value,
                        _fenceEvent.SafeWaitHandle.DangerousGetHandle()), "ID3D12Fence::SetEventOnCompletion");
                    if (!_fenceEvent.WaitOne(FenceTimeout)
                        && D3D12Backend.FenceGetCompletedValue(_consumedFence) < value)
                        throw new TimeoutException($"The GPU did not finish readback {value} within {FenceTimeout.TotalSeconds:F0} s.");
                }
            }
        }

        private Slot SlotAt(int slot)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_slots.Length, nameof(slot));
            return _slots[slot];
        }

        private static void Transition(Slot s, int from, int to)
        {
            if (Interlocked.CompareExchange(ref s.State, to, from) != from)
                throw new InvalidOperationException(
                    $"Readback slot is in state {s.State}, not {from} (Begin → Submit → Wait order violated).");
        }

        private static void DisposeWrapper(Slot s)
        {
            s.Surface?.Dispose();
            s.Surface = null;
            s.BackendTexture?.Dispose();
            s.BackendTexture = null;
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var s in _slots)
                DisposeWrapper(s); // a Begin that never reached Submit (an exception mid-compose)

            // Skia must be done with the textures (it AddRefs them per wrapper and releases when
            // its command list completes), and the copy queue must be done reading them.
            try
            {
                _context.Flush();
                _context.Submit(synchronous: true);
                if (_copyQueue != IntPtr.Zero && _consumedFence != IntPtr.Zero)
                {
                    // A fresh signal queued behind everything on the copy queue covers the one
                    // case _lastSignaled cannot: a copy that executed but whose own Signal in
                    // Submit failed. Every copy-queue Wait ahead of it is for a render-fence
                    // value the direct queue accepted, so this completes on a healthy device.
                    ulong drain = _lastSignaled + 1;
                    if (D3D12Backend.QueueSignal(_copyQueue, _consumedFence, drain) >= 0)
                        WaitForFence(drain);
                    else if (_lastSignaled > 0)
                        WaitForFence(_lastSignaled);
                }
            }
            catch (Exception)
            {
                // a removed device or a hung GPU: nothing left to wait for, release what we hold
            }

            foreach (var s in _slots)
            {
                if (s.Readback != IntPtr.Zero && s.Mapped != null)
                    D3D12Backend.ResourceUnmap(s.Readback);
                D3D12Backend.Release(s.CopyList);
                D3D12Backend.Release(s.Allocator);
                D3D12Backend.Release(s.Readback);
                D3D12Backend.Release(s.Texture);
                s.CopyList = s.Allocator = s.Readback = s.Texture = IntPtr.Zero;
                s.Mapped = null;
            }

            D3D12Backend.Release(_consumedFence);
            D3D12Backend.Release(_renderFence);
            D3D12Backend.Release(_copyQueue);
            _consumedFence = _renderFence = _copyQueue = IntPtr.Zero;
            _fenceEvent.Dispose();
        }
    }
}
