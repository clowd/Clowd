using System;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Elastic FIFO of demuxed packets for one stream, with ffplay-style flush serials. Slots are
    /// preallocated AVPackets; producing/consuming moves refs (zero allocation, zero copy).
    ///
    /// The serial is the seek generation: <see cref="Flush"/> discards everything and increments
    /// it; consumers compare the serial attached to each packet against the last one they saw and
    /// flush their codec on change. A special EOF entry (no data) is queued at end of stream.
    ///
    /// The queue grows rather than blocking its producer: there is one demux thread feeding every
    /// stream, so a queue that blocks when full stops the packets of *all* the other streams too
    /// (see <see cref="Demuxer"/> — that starves the audio clock the video presenter waits on).
    /// Read-ahead is bounded across streams by the demuxer's budget instead, which is why
    /// <see cref="Bytes"/> is tracked here.
    /// </summary>
    internal sealed unsafe class PacketQueue : IDisposable
    {
        private struct Slot
        {
            public AVPacket* Packet;
            public int Serial;
            public bool Eof;
        }

        private readonly object _sync = new object();
        private Slot[] _slots;
        private int _head;   // next slot to consume
        private int _count;
        private long _bytes;  // queued packet payload, for the demuxer's read-ahead budget
        private int _serial;
        private bool _stopped;
        private bool _producerInterrupt;
        private bool _disposed;

        public PacketQueue(int capacity = 32)
        {
            _slots = new Slot[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _slots[i].Packet = ffmpeg.av_packet_alloc();
                if (_slots[i].Packet == null)
                    throw new OutOfMemoryException("av_packet_alloc failed");
            }
        }

        public int Serial
        {
            get { lock (_sync) return _serial; }
        }

        public int Count
        {
            get { lock (_sync) return _count; }
        }

        /// <summary>Payload bytes currently queued (EOF markers count as zero).</summary>
        public long Bytes
        {
            get { lock (_sync) return _bytes; }
        }

        /// <summary>
        /// Moves <paramref name="packet"/>'s ref into the queue, growing it if it is full. Returns
        /// false (packet untouched) when the producer was interrupted (pending seek/stop command)
        /// so the demux loop can go handle it.
        /// </summary>
        public bool Put(AVPacket* packet, bool eof)
        {
            lock (_sync)
            {
                if (_stopped || _producerInterrupt)
                    return false;

                if (_count == _slots.Length)
                    Grow();

                int tail = (_head + _count) % _slots.Length;
                ref var slot = ref _slots[tail];
                slot.Serial = _serial;
                slot.Eof = eof;
                if (!eof && packet != null)
                {
                    ffmpeg.av_packet_move_ref(slot.Packet, packet);
                    _bytes += slot.Packet->size;
                }

                _count++;
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        /// <summary>Doubles the slot ring. Only called while full, so every existing slot holds a
        /// queued packet and re-linearizing from <see cref="_head"/> copies all of them.</summary>
        private void Grow()
        {
            var bigger = new Slot[_slots.Length * 2];
            for (int i = 0; i < _count; i++)
                bigger[i] = _slots[(_head + i) % _slots.Length];

            for (int i = _count; i < bigger.Length; i++)
            {
                bigger[i].Packet = ffmpeg.av_packet_alloc();
                if (bigger[i].Packet == null)
                    throw new OutOfMemoryException("av_packet_alloc failed");
            }

            _slots = bigger;
            _head = 0;
        }

        /// <summary>
        /// Blocks until a packet is available and moves its ref into <paramref name="packet"/>.
        /// Returns false when the queue is stopped. EOF entries return true with
        /// <paramref name="eof"/> set and no packet data.
        /// </summary>
        public bool Get(AVPacket* packet, out int serial, out bool eof)
        {
            lock (_sync)
            {
                while (_count == 0 && !_stopped)
                    Monitor.Wait(_sync);

                if (_count == 0)
                {
                    serial = _serial;
                    eof = false;
                    return false;
                }

                ref var slot = ref _slots[_head];
                serial = slot.Serial;
                eof = slot.Eof;
                if (!eof)
                {
                    _bytes -= slot.Packet->size;
                    ffmpeg.av_packet_move_ref(packet, slot.Packet);
                }

                _head = (_head + 1) % _slots.Length;
                _count--;
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        /// <summary>Discards all queued packets and bumps the serial. Returns the new serial.</summary>
        public int Flush()
        {
            lock (_sync)
            {
                for (int i = 0; i < _count; i++)
                {
                    ref var slot = ref _slots[(_head + i) % _slots.Length];
                    if (!slot.Eof)
                        ffmpeg.av_packet_unref(slot.Packet);
                }

                _head = 0;
                _count = 0;
                _bytes = 0;
                _serial++;
                Monitor.PulseAll(_sync);
                return _serial;
            }
        }

        /// <summary>Makes a blocked/future <see cref="Put"/> return false until cleared —
        /// the demuxer uses this to break out and service a command.</summary>
        public void SetProducerInterrupt(bool value)
        {
            lock (_sync)
            {
                _producerInterrupt = value;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>Permanently wakes and fails all waiters (shutdown).</summary>
        public void Stop()
        {
            lock (_sync)
            {
                _stopped = true;
                Monitor.PulseAll(_sync);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _stopped = true;

                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Packet != null)
                    {
                        var p = _slots[i].Packet;
                        ffmpeg.av_packet_free(&p);
                        _slots[i].Packet = null;
                    }
                }

                _count = 0;
                _bytes = 0;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
