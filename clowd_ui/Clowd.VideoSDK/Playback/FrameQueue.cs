using System;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// Small bounded queue of decoded frames between a track's decode thread and its present
    /// thread. Slots own preallocated AVFrames; the decode thread reserves a slot, moves the
    /// decoded frame's ref in, and commits with pts+serial; the present thread peeks the oldest
    /// committed slot, decides (present / drop / stale), and releases it.
    /// </summary>
    internal sealed unsafe class FrameQueue : IDisposable
    {
        /// <summary>Reading = peeked by the present thread and possibly mid-sws_scale; only
        /// <see cref="Release"/> may free it (a concurrent <see cref="Flush"/> must leave it
        /// alone or the frame's buffers would be unref'd under the scaler).</summary>
        internal enum SlotState { Free, Writing, Ready, Reading }

        private struct Slot
        {
            public AVFrame* Frame;
            public SlotState State;
            public long Sequence;
            public int Serial;
            public long PtsTicks;
            public bool Eof;
        }

        private readonly object _sync = new object();
        private readonly Slot[] _slots;
        private long _nextSequence;
        private bool _stopped;
        private bool _disposed;

        public FrameQueue(int capacity = 4)
        {
            _slots = new Slot[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _slots[i].Frame = ffmpeg.av_frame_alloc();
                if (_slots[i].Frame == null)
                    throw new OutOfMemoryException("av_frame_alloc failed");
            }
        }

        public int ReadyCount
        {
            get
            {
                lock (_sync)
                {
                    int n = 0;
                    for (int i = 0; i < _slots.Length; i++)
                        if (_slots[i].State == SlotState.Ready || _slots[i].State == SlotState.Reading)
                            n++;
                    return n;
                }
            }
        }

        /// <summary>Blocks until a slot is free (backpressure = decode-ahead bound). Returns the
        /// slot index, or -1 when stopped. The slot's AVFrame is clean and ready to move into.</summary>
        public int Reserve()
        {
            lock (_sync)
            {
                while (true)
                {
                    if (_stopped)
                        return -1;
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        if (_slots[i].State == SlotState.Free)
                        {
                            _slots[i].State = SlotState.Writing;
                            return i;
                        }
                    }

                    Monitor.Wait(_sync);
                }
            }
        }

        public AVFrame* GetFrame(int slot) => _slots[slot].Frame;

        public void Commit(int slot, int serial, long ptsTicks, bool eof)
        {
            lock (_sync)
            {
                ref var s = ref _slots[slot];
                s.Serial = serial;
                s.PtsTicks = ptsTicks;
                s.Eof = eof;
                s.Sequence = _nextSequence++;
                s.State = SlotState.Ready;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>Aborts a reservation without committing (frame unref'd, slot freed).</summary>
        public void CancelReserve(int slot)
        {
            lock (_sync)
            {
                ffmpeg.av_frame_unref(_slots[slot].Frame);
                _slots[slot].State = SlotState.Free;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// Blocks until a committed frame exists (FIFO by commit order) or timeout. Returns the
        /// slot index or -1 (stopped / timed out). Does not consume — call <see cref="Release"/>.
        /// The slot is marked Reading so a concurrent <see cref="Flush"/> cannot free it while
        /// the present thread holds it; re-peeking returns the same held slot (it stays oldest).
        /// </summary>
        public int PeekWait(int timeoutMs, out int serial, out long ptsTicks, out bool eof)
        {
            lock (_sync)
            {
                while (true)
                {
                    if (_stopped)
                        break;

                    int best = -1;
                    long bestSeq = long.MaxValue;
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        if ((_slots[i].State == SlotState.Ready || _slots[i].State == SlotState.Reading)
                            && _slots[i].Sequence < bestSeq)
                        {
                            bestSeq = _slots[i].Sequence;
                            best = i;
                        }
                    }

                    if (best >= 0)
                    {
                        _slots[best].State = SlotState.Reading;
                        serial = _slots[best].Serial;
                        ptsTicks = _slots[best].PtsTicks;
                        eof = _slots[best].Eof;
                        return best;
                    }

                    if (timeoutMs == 0 || !Monitor.Wait(_sync, timeoutMs < 0 ? Timeout.Infinite : timeoutMs))
                        break;
                }

                serial = 0;
                ptsTicks = 0;
                eof = false;
                return -1;
            }
        }

        /// <summary>Frees a peeked slot (unrefs the frame data).</summary>
        public void Release(int slot)
        {
            lock (_sync)
            {
                ffmpeg.av_frame_unref(_slots[slot].Frame);
                _slots[slot].State = SlotState.Free;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>Discards all committed frames (seek). Slots mid-write are left to the decode
        /// thread; their stale serial gets them discarded by the present thread on commit. A slot
        /// the present thread holds (Reading) is left alone — its serial check + Release discard
        /// it, and unref'ing it here would free buffers sws_scale may be reading.</summary>
        public void Flush()
        {
            lock (_sync)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].State == SlotState.Ready)
                    {
                        ffmpeg.av_frame_unref(_slots[i].Frame);
                        _slots[i].State = SlotState.Free;
                    }
                }

                Monitor.PulseAll(_sync);
            }
        }

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
                    if (_slots[i].Frame != null)
                    {
                        var f = _slots[i].Frame;
                        ffmpeg.av_frame_free(&f);
                        _slots[i].Frame = null;
                        _slots[i].State = SlotState.Free;
                    }
                }

                Monitor.PulseAll(_sync);
            }
        }
    }
}
