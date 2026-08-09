using System;
using System.Threading;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Single-producer / single-consumer lock-free ring of float samples (~500ms of decoded
    /// audio). The producer is the audio decode thread; the consumer is the WASAPI render
    /// callback. <see cref="Clear"/> must only be called from the producer thread (it rewrites
    /// the producer-owned head); the worst case of the benign race with a concurrent Read is one
    /// callback of stale samples immediately after a seek, which the seek flushed anyway.
    /// </summary>
    public sealed class AudioRingBuffer
    {
        private readonly float[] _buffer;
        private long _head; // total samples written (producer-owned)
        private long _tail; // total samples read (consumer-owned)

        public AudioRingBuffer(int capacitySamples)
        {
            if (capacitySamples <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacitySamples));
            _buffer = new float[capacitySamples];
        }

        public int Capacity => _buffer.Length;

        public int Available
        {
            get
            {
                long avail = Volatile.Read(ref _head) - Volatile.Read(ref _tail);
                if (avail < 0) avail = 0;
                if (avail > _buffer.Length) avail = _buffer.Length;
                return (int)avail;
            }
        }

        public int Free => _buffer.Length - Available;

        /// <summary>Producer: copies up to <paramref name="count"/> samples; returns samples written.</summary>
        public int Write(ReadOnlySpan<float> source)
        {
            long head = Volatile.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            if (tail > head)
            {
                // defensive resync after the Clear/Read race described above.
                head = tail;
                Volatile.Write(ref _head, head);
            }

            int free = _buffer.Length - (int)Math.Min(head - tail, _buffer.Length);
            int toWrite = Math.Min(free, source.Length);
            if (toWrite <= 0)
                return 0;

            int idx = (int)(head % _buffer.Length);
            int first = Math.Min(toWrite, _buffer.Length - idx);
            source.Slice(0, first).CopyTo(_buffer.AsSpan(idx, first));
            if (toWrite > first)
                source.Slice(first, toWrite - first).CopyTo(_buffer.AsSpan(0, toWrite - first));

            Volatile.Write(ref _head, head + toWrite);
            return toWrite;
        }

        /// <summary>Consumer: copies up to <paramref name="destination"/>.Length samples; returns samples read.</summary>
        public int Read(Span<float> destination)
        {
            long head = Volatile.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            long avail = head - tail;
            if (avail <= 0)
                return 0;

            int toRead = (int)Math.Min(avail, destination.Length);
            int idx = (int)(tail % _buffer.Length);
            int first = Math.Min(toRead, _buffer.Length - idx);
            _buffer.AsSpan(idx, first).CopyTo(destination.Slice(0, first));
            if (toRead > first)
                _buffer.AsSpan(0, toRead - first).CopyTo(destination.Slice(first, toRead - first));

            Volatile.Write(ref _tail, tail + toRead);
            return toRead;
        }

        /// <summary>Producer thread only: discards all buffered samples (seek flush).</summary>
        public void Clear()
        {
            Volatile.Write(ref _head, Volatile.Read(ref _tail));
        }
    }
}
