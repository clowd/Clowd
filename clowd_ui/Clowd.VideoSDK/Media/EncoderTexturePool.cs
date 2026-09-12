using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// The texture pool behind <see cref="HardwareFrames"/>: GPU textures an encoder reads
    /// directly, lent out one per frame and returned by the encoder itself. Storage is opaque
    /// here (an <c>ID3D11Texture2D*</c> as an <see cref="IntPtr"/>, created and destroyed
    /// through the two delegates), so the bookkeeping is testable without a GPU.
    ///
    /// <para><b>Reuse protocol.</b> <see cref="Rent"/> takes a free texture (allocating one when
    /// none is free — the pool grows on demand and never blocks, like FFmpeg's own hardware
    /// frame pools, because how many frames an encoder holds in flight is its business: NVENC
    /// keeps up to <c>rc-lookahead + B-frames + 4</c> input surfaces mapped, some 15 at the
    /// project's settings, and a fixed pool smaller than that would deadlock the pipeline).
    /// <see cref="CreateReference"/> then wraps the texture in the <c>AVBufferRef</c> a hardware
    /// <c>AVFrame</c> carries in <c>buf[0]</c>, following FFmpeg's D3D11 convention (the buffer's
    /// data is an <c>AVD3D11FrameDescriptor</c> naming the texture and its array slice). Its free
    /// callback does not free the texture — the pool owns it — it hands the texture back to the
    /// pool, and that callback fires exactly when the last reference to the frame is dropped:
    /// the encoder's, which every hardware encoder releases only once it has finished reading
    /// the picture (<c>nvenc.c</c> unrefs its <c>in_ref</c> after the packet is produced and the
    /// input unmapped; <c>amfenc.c</c> keeps a frame reference on the AMF surface until AMF
    /// destroys it). So "texture free again" is precisely "encoder done", with no fence of our
    /// own and no guessing at packet order. A texture that was rented but never wrapped (a failed
    /// conversion) goes back with <see cref="Return"/>.</para>
    ///
    /// <para>Thread-safety: <see cref="Rent"/> runs on the pipeline's convert thread, the free
    /// callback on whichever thread drops the last reference (the encode thread inside
    /// <c>avcodec_receive_packet</c>, or <c>avcodec_free_context</c> at teardown); a lock
    /// serializes them. Every callback finds its pool through a process-wide registry keyed by
    /// an id rather than a raw pointer, so a reference outliving its pool (a frame nobody
    /// disposed) is ignored rather than dereferenced. Dispose the encoder and every frame before
    /// the pool: <see cref="Dispose"/> destroys the textures whether or not they are still lent
    /// out (<see cref="InUse"/> tells).</para>
    /// </summary>
    internal sealed unsafe class EncoderTexturePool : IDisposable
    {
        private static readonly ConcurrentDictionary<long, EncoderTexturePool> Pools = new ConcurrentDictionary<long, EncoderTexturePool>();
        private static long _nextId;

        private sealed class Entry
        {
            public IntPtr Texture;
            public AVD3D11FrameDescriptor* Descriptor; // what a reference's data points at; lives as long as the entry
            public bool InUse;
        }

        private readonly long _id;
        private readonly Func<IntPtr> _allocate;
        private readonly Action<IntPtr> _release;
        private readonly object _sync = new object();
        private readonly Dictionary<IntPtr, Entry> _entries = new Dictionary<IntPtr, Entry>();
        private readonly Stack<Entry> _free = new Stack<Entry>();
        private int _peakInUse;
        private bool _disposed;

        /// <param name="allocate">Creates one texture; returns its handle (never zero) or throws.</param>
        /// <param name="release">Destroys a texture <paramref name="allocate"/> created.</param>
        public EncoderTexturePool(Func<IntPtr> allocate, Action<IntPtr> release)
        {
            ArgumentNullException.ThrowIfNull(allocate);
            ArgumentNullException.ThrowIfNull(release);
            _allocate = allocate;
            _release = release;
            _id = Interlocked.Increment(ref _nextId);
            Pools[_id] = this;
        }

        /// <summary>Textures allocated so far.</summary>
        public int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>Textures currently lent out (rented, or referenced by a frame).</summary>
        public int InUse
        {
            get { lock (_sync) return _entries.Count - _free.Count; }
        }

        /// <summary>The most textures ever lent out at once — how deep the encoder's in-flight
        /// queue got.</summary>
        public int PeakInUse
        {
            get { lock (_sync) return _peakInUse; }
        }

        /// <summary>A free texture, allocating one when none is. The caller owns it until
        /// <see cref="CreateReference"/> hands ownership to the reference or <see cref="Return"/>
        /// gives it back.</summary>
        public IntPtr Rent()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_free.Count > 0)
                {
                    var entry = _free.Pop();
                    entry.InUse = true;
                    _peakInUse = Math.Max(_peakInUse, _entries.Count - _free.Count);
                    return entry.Texture;
                }
            }

            // Allocation is a driver call; it runs outside the lock so a free callback from the
            // encode thread is never held up by it.
            IntPtr texture = _allocate();
            if (texture == IntPtr.Zero)
                throw new InvalidOperationException("The texture allocator returned a null texture.");

            var descriptor = (AVD3D11FrameDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(AVD3D11FrameDescriptor));
            descriptor->texture = (ID3D11Texture2D*)texture;
            descriptor->index = 0;
            lock (_sync)
            {
                if (_disposed)
                {
                    NativeMemory.Free(descriptor);
                    _release(texture);
                    throw new ObjectDisposedException(nameof(EncoderTexturePool));
                }

                _entries.Add(texture, new Entry { Texture = texture, Descriptor = descriptor, InUse = true });
                _peakInUse = Math.Max(_peakInUse, _entries.Count - _free.Count);
            }
            return texture;
        }

        /// <summary>Gives a rented texture back without a reference having been created for it.</summary>
        public void Return(IntPtr texture)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(texture, out var entry))
                    throw new ArgumentException("The texture does not belong to this pool.", nameof(texture));
                if (!entry.InUse)
                    throw new InvalidOperationException("The texture is already free (returned twice).");
                entry.InUse = false;
                _free.Push(entry);
            }
        }

        /// <summary>
        /// The <c>AVBufferRef</c> for a rented texture: its data is the texture's
        /// <c>AVD3D11FrameDescriptor</c>, and dropping the last reference returns the texture to
        /// the pool (the free callback releases nothing — the pool owns the texture). Ownership
        /// of the rent passes to the reference: do not <see cref="Return"/> a texture that has one.
        /// </summary>
        public AVBufferRef* CreateReference(IntPtr texture)
        {
            Entry entry;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(texture, out entry))
                    throw new ArgumentException("The texture does not belong to this pool.", nameof(texture));
                if (!entry.InUse)
                    throw new InvalidOperationException("The texture is not rented.");
            }

            var buffer = ffmpeg.av_buffer_create((byte*)entry.Descriptor, (ulong)sizeof(AVD3D11FrameDescriptor),
                ReturnOnFree, (void*)_id, 0);
            if (buffer == null)
                throw new InvalidOperationException("Could not create the hardware frame's buffer reference.");
            return buffer;
        }

        // FFmpeg's free callback for the references above: runs inside av_buffer_unref on
        // whatever thread dropped the last reference, so it must never throw and must not
        // touch a pool that is gone (the registry lookup guards that; the descriptor is only
        // read under the pool's lock, where Dispose has not yet run).
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ReturnOnFreeImpl(void* opaque, byte* data)
        {
            if (Pools.TryGetValue((long)opaque, out var pool))
                pool.ReturnFromReference((AVD3D11FrameDescriptor*)data);
        }

        private static readonly av_buffer_create_free_func ReturnOnFree = new av_buffer_create_free_func
        {
            Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&ReturnOnFreeImpl,
        };

        private void ReturnFromReference(AVD3D11FrameDescriptor* descriptor)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                var texture = (IntPtr)descriptor->texture;
                if (_entries.TryGetValue(texture, out var entry) && entry.InUse)
                {
                    entry.InUse = false;
                    _free.Push(entry);
                }
                else
                {
                    Debug.Fail("A hardware frame reference was released for a texture that is not lent out.");
                }
            }
        }

        public void Dispose()
        {
            List<Entry> entries;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                Pools.TryRemove(_id, out _);
                entries = new List<Entry>(_entries.Values);
                _entries.Clear();
                _free.Clear();
            }

            foreach (var entry in entries)
            {
                _release(entry.Texture);
                NativeMemory.Free(entry.Descriptor);
            }
        }
    }
}
