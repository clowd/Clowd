using System;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The <see cref="IReadbackRing"/> every backend can run: one factory surface per slot, read
    /// back synchronously with <see cref="ISurfaceFactory.TryReadPixels"/> at
    /// <see cref="Submit"/> into a slot-owned staging buffer, so <see cref="Wait"/> has nothing
    /// to wait for. On the CPU backend the readback is a memcpy and this is the real thing; on a
    /// GPU backend without an asynchronous ring (Metal today, or Direct3D 12 when
    /// <see cref="D3D12ReadbackRing"/> cannot be created) it reproduces the render loop's
    /// original behaviour exactly, which keeps the fallback path the well-tested one.
    ///
    /// <para>Depth: a factory clamps the caller's requested slot count to
    /// <see cref="MaxUsefulSlots"/> (see <see cref="UsefulSlots"/>). The render loop asks for the
    /// depth an asynchronous ring needs — a slot being composed, one whose GPU copy is in flight,
    /// one being converted, one for jitter — but here <see cref="Submit"/> finishes the readback
    /// before returning, so nothing is ever in flight: two slots (one composing, one being read
    /// by the convert stage) give the same overlap the original two-in-flight loop had, and
    /// every further slot only duplicates a full-size surface plus staging buffer (23 MB each at
    /// 2480x2332) to let the composer run ahead, slack the converted-frame queue downstream
    /// already provides.</para>
    /// </summary>
    public sealed class SyncReadbackRing : IReadbackRing
    {
        /// <summary>The most slots a synchronous ring can put to use: one being composed, one
        /// being read by the consumer.</summary>
        public const int MaxUsefulSlots = 2;

        // slot states; misuse (double Begin, Wait before Submit) is a caller bug and throws
        private const int Idle = 0, Composing = 1, InFlight = 2;

        private readonly ISurfaceFactory _factory;
        private readonly SKSurface[] _surfaces;
        private readonly IntPtr[] _staging;
        private readonly int[] _state;
        private readonly int _rowBytes;
        private bool _disposed;

        /// <summary>The slot count a factory gives a synchronous ring for a caller that asked for
        /// <paramref name="requested"/>: at most <see cref="MaxUsefulSlots"/>.</summary>
        public static int UsefulSlots(int requested)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requested, 0);
            return Math.Min(requested, MaxUsefulSlots);
        }

        /// <summary>Creates the ring on the factory's thread with exactly <paramref name="slots"/>
        /// slots (factories pass <see cref="UsefulSlots"/>). The factory must outlive it.</summary>
        public SyncReadbackRing(ISurfaceFactory factory, int width, int height, int slots)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slots, 0);

            _factory = factory;
            Width = width;
            Height = height;
            _rowBytes = width * 4;
            _surfaces = new SKSurface[slots];
            _staging = new IntPtr[slots];
            _state = new int[slots];
            try
            {
                for (int i = 0; i < slots; i++)
                {
                    _surfaces[i] = factory.CreateSurface(width, height);
                    _staging[i] = AllocStaging(_rowBytes * height);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static unsafe IntPtr AllocStaging(int bytes)
            => (IntPtr)NativeMemory.AlignedAlloc((nuint)bytes, 64); // sws_scale-friendly alignment

        public int Width { get; }

        public int Height { get; }

        public int SlotCount => _surfaces.Length;

        public string Description => $"synchronous readback, {SlotCount} slots";

        public SKCanvas Begin(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Transition(slot, Idle, Composing);
            return _surfaces[slot].Canvas;
        }

        public void Submit(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Transition(slot, Composing, InFlight);
            if (!_factory.TryReadPixels(_surfaces[slot], Width, Height, _staging[slot], _rowBytes))
                throw new InvalidOperationException(
                    $"Pixel readback failed on the {_factory.BackendName} backend.");
        }

        public ReadbackPixels Wait(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Transition(slot, InFlight, Idle);
            return new ReadbackPixels(_staging[slot], _rowBytes);
        }

        private void Transition(int slot, int from, int to)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)_state.Length, nameof(slot));
            if (Interlocked.CompareExchange(ref _state[slot], to, from) != from)
                throw new InvalidOperationException(
                    $"Readback slot {slot} is in state {_state[slot]}, not {from} (Begin → Submit → Wait order violated).");
        }

        public unsafe void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            for (int i = 0; i < _surfaces.Length; i++)
            {
                _surfaces[i]?.Dispose();
                _surfaces[i] = null;
                if (_staging[i] != IntPtr.Zero)
                {
                    NativeMemory.AlignedFree((void*)_staging[i]);
                    _staging[i] = IntPtr.Zero;
                }
            }
        }
    }
}
