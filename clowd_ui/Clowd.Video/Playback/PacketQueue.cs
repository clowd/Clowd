using System;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Bounded FIFO of demuxed packets for one stream, with ffplay-style flush serials. Slots are
    /// preallocated AVPackets; producing/consuming moves refs (zero allocation, zero copy).
    ///
    /// The serial is the seek generation: <see cref="Flush"/> discards everything and increments
    /// it; consumers compare the serial attached to each packet against the last one they saw and
    /// flush their codec on change. A special EOF entry (no data) is queued at end of stream.
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
        private readonly Slot[] _slots;
        private int _head;   // next slot to consume
        private int _count;
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

        /// <summary>
        /// Moves <paramref name="packet"/>'s ref into the queue; blocks while full. Returns false
        /// (packet untouched) when the producer was interrupted (pending seek/stop command) so the
        /// demux loop can go handle it.
        /// </summary>
        public bool Put(AVPacket* packet, bool eof)
        {
            lock (_sync)
            {
                while (_count == _slots.Length)
                {
                    if (_stopped || _producerInterrupt)
                        return false;
                    Monitor.Wait(_sync);
                }

                if (_stopped || _producerInterrupt)
                    return false;

                int tail = (_head + _count) % _slots.Length;
                ref var slot = ref _slots[tail];
                slot.Serial = _serial;
                slot.Eof = eof;
                if (!eof && packet != null)
                    ffmpeg.av_packet_move_ref(slot.Packet, packet);
                _count++;
                Monitor.PulseAll(_sync);
                return true;
            }
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
                    ffmpeg.av_packet_move_ref(packet, slot.Packet);
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
                Monitor.PulseAll(_sync);
            }
        }
    }
}
