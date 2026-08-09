using System;
using System.Collections.Generic;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Owns the AVFormatContext and the single av_read_frame thread. One demux pass feeds every
    /// selected stream's bounded <see cref="PacketQueue"/>; a full queue blocks the read loop
    /// (natural decode-ahead bound). Seeks run on the demux thread: flush all queues (bumping
    /// their serials in lockstep), avformat keyframe-backward seek, resume reading.
    /// </summary>
    internal sealed unsafe class Demuxer : IDisposable
    {
        private AVFormatContext* _fmt;
        private AVPacket* _pkt;
        private Thread _thread;
        private readonly Dictionary<int, PacketQueue> _queues = new Dictionary<int, PacketQueue>();
        private PacketQueue[] _queueList = Array.Empty<PacketQueue>();
        private int[] _queueStreamIndexes = Array.Empty<int>();

        private readonly object _cmdSync = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _seekDone = new ManualResetEventSlim(false);
        private volatile bool _seekRequested;
        private long _seekTargetTicks;
        private int _serialAfterSeek;
        private volatile bool _running;
        private volatile bool _eofReached;
        private bool _disposed;

        public bool IsEof => _eofReached;

        public MediaInfo Open(string path)
        {
            FFmpegLoader.EnsureInitialized();

            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"Failed to open '{path}': {FFmpegLoader.ErrorToString(err)}");

            err = ffmpeg.avformat_find_stream_info(fmt, null);
            if (err < 0)
            {
                ffmpeg.avformat_close_input(&fmt);
                throw new InvalidOperationException($"Failed to read stream info: {FFmpegLoader.ErrorToString(err)}");
            }

            _fmt = fmt;
            _pkt = ffmpeg.av_packet_alloc();
            return MediaProbe.BuildInfo(path, fmt);
        }

        public AVFormatContext* FormatContext => _fmt;

        public AVStream* GetStream(int index) => _fmt->streams[index];

        public void AttachQueue(int streamIndex, PacketQueue queue)
        {
            _queues[streamIndex] = queue;
            var list = new List<PacketQueue>(_queues.Values);
            var idx = new List<int>(_queues.Keys);
            _queueList = list.ToArray();
            _queueStreamIndexes = idx.ToArray();
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(ReadLoop) { Name = "clowd-demux", IsBackground = true };
            _thread.Start();
        }

        /// <summary>
        /// Synchronous seek executed on the demux thread: flushes every packet queue (serial++),
        /// seeks avformat backward to the nearest keyframe at/before target. Returns the new
        /// serial shared by all queues. Callable from any thread except the demux thread.
        /// </summary>
        public int SeekAndFlush(TimeSpan target)
        {
            lock (_cmdSync)
            {
                _seekDone.Reset();
                Interlocked.Exchange(ref _seekTargetTicks, target.Ticks);
                _seekRequested = true;

                // unblock the read loop if it is parked inside a full queue's Put.
                var queues = _queueList;
                for (int i = 0; i < queues.Length; i++)
                    queues[i].SetProducerInterrupt(true);
                _wake.Set();

                _seekDone.Wait();
                return _serialAfterSeek;
            }
        }

        private void ReadLoop()
        {
            bool eofSent = false;

            while (_running)
            {
                if (_seekRequested)
                {
                    DoSeek();
                    eofSent = false;
                    continue;
                }

                if (_eofReached)
                {
                    if (!eofSent)
                    {
                        var queues = _queueList;
                        for (int i = 0; i < queues.Length; i++)
                            queues[i].Put(null, eof: true);
                        eofSent = true;
                    }

                    // idle until a seek (or stop) wakes us.
                    _wake.WaitOne(100);
                    continue;
                }

                int ret = ffmpeg.av_read_frame(_fmt, _pkt);
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    _eofReached = true;
                    continue;
                }

                if (ret < 0)
                {
                    // transient read error; avoid a hot spin.
                    Thread.Sleep(5);
                    continue;
                }

                if (_queues.TryGetValue(_pkt->stream_index, out var queue))
                {
                    if (!queue.Put(_pkt, eof: false))
                        ffmpeg.av_packet_unref(_pkt); // interrupted (seek/stop pending)
                }
                else
                {
                    ffmpeg.av_packet_unref(_pkt);
                }
            }
        }

        private void DoSeek()
        {
            var queues = _queueList;
            int newSerial = 0;
            for (int i = 0; i < queues.Length; i++)
            {
                newSerial = queues[i].Flush();
                queues[i].SetProducerInterrupt(false);
            }

            long targetTicks = Interlocked.Read(ref _seekTargetTicks);
            long ts = (long)(targetTicks / (double)TimeSpan.TicksPerSecond * ffmpeg.AV_TIME_BASE);
            int err = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (err < 0)
            {
                // retry without backward flag (some containers reject it near 0/EOF).
                ffmpeg.av_seek_frame(_fmt, -1, ts, 0);
            }

            _eofReached = false;
            _serialAfterSeek = newSerial;
            _seekRequested = false;
            _seekDone.Set();
        }

        public void Stop()
        {
            _running = false;
            var queues = _queueList;
            for (int i = 0; i < queues.Length; i++)
                queues[i].SetProducerInterrupt(true);
            _wake.Set();

            // a stuck seek waiter would deadlock Stop; release it.
            if (_seekRequested)
            {
                _seekRequested = false;
                _seekDone.Set();
            }

            _thread?.Join(3000);
            _thread = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();

            if (_pkt != null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }

            if (_fmt != null)
            {
                var f = _fmt;
                ffmpeg.avformat_close_input(&f);
                _fmt = null;
            }

            _wake.Dispose();
            _seekDone.Dispose();
        }
    }
}
