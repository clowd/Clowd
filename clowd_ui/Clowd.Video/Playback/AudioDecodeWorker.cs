using System;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// Decodes the audio stream and swr_converts to interleaved float stereo at the engine rate
    /// into the lock-free ring the WASAPI sink drains. On seek: codec flush + ring clear + sink
    /// timing reset; exact seeks trim samples up to the target so the audio clock restarts on it.
    /// </summary>
    internal sealed unsafe class AudioDecodeWorker : IDisposable
    {
        private const int Channels = 2;

        private readonly Demuxer _demuxer;
        private readonly int _streamIndex;
        private readonly PacketQueue _packets;
        private readonly AudioRingBuffer _ring;
        private readonly NAudioSink _sink;
        private readonly int _outRate;
        private readonly AVRational _timeBase;

        private AVCodecContext* _ctx;
        private AVFrame* _frame;
        private AVPacket* _pkt;
        private SwrContext* _swr;
        private int _swrInRate;
        private int _swrInFormat = -1;
        private ulong _swrInLayoutMask; // cheap change detection (order field of ch_layout)
        private int _swrInChannels;

        private readonly float[] _convBuffer;

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _flushRequested;
        private long _seekTargetTicks = long.MinValue;
        private int _seekExact;
        private int _decodeSerial = -1;
        private volatile bool _eofReached;
        private bool _disposed;

        public AudioDecodeWorker(Demuxer demuxer, int streamIndex, PacketQueue packets,
            AudioRingBuffer ring, NAudioSink sink, VideoOpenOptions options)
        {
            _demuxer = demuxer;
            _streamIndex = streamIndex;
            _packets = packets;
            _ring = ring;
            _sink = sink;
            _outRate = options.AudioSampleRate;

            var st = demuxer.GetStream(streamIndex);
            _timeBase = st->time_base;

            var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException("No audio decoder for " + st->codecpar->codec_id);

            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            int err = ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar);
            if (err < 0)
                throw new InvalidOperationException("avcodec_parameters_to_context: " + FFmpegLoader.ErrorToString(err));
            _ctx->pkt_timebase = st->time_base;

            err = ffmpeg.avcodec_open2(_ctx, codec, null);
            if (err < 0)
                throw new InvalidOperationException("avcodec_open2 (audio): " + FFmpegLoader.ErrorToString(err));

            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            // large enough for one worst-case frame after resampling (dur ~= 8192 samples in,
            // upsampled to the engine rate) — reused forever.
            _convBuffer = new float[32768 * Channels];
        }

        public bool EofReached => _eofReached;

        public void Start()
        {
            _running = true;
            _thread = new Thread(DecodeLoop) { Name = "clowd-adec", IsBackground = true };
            _thread.Start();
        }

        /// <summary>Controller, before the demux flush: abandon in-progress ring writes and record
        /// the trim target. The decode thread performs the actual flush on serial adoption.</summary>
        public void PrepareSeek(TimeSpan target, SeekMode mode)
        {
            Interlocked.Exchange(ref _seekTargetTicks, target.Ticks);
            Interlocked.Exchange(ref _seekExact, mode == SeekMode.Exact ? 1 : 0);
            _flushRequested = true;
        }

        private void DecodeLoop()
        {
            long exactDiscardTicks = long.MinValue;

            while (_running)
            {
                if (!_packets.Get(_pkt, out int serial, out bool eof))
                    break;

                if (serial != _decodeSerial)
                {
                    ffmpeg.avcodec_flush_buffers(_ctx);
                    if (_swr != null)
                        ffmpeg.swr_init(_swr); // reset resampler delay state
                    _sink.ResetTiming();
                    _ring.Clear(); // producer thread — safe
                    _decodeSerial = serial;
                    _flushRequested = false;
                    _eofReached = false;

                    long target = Interlocked.Read(ref _seekTargetTicks);
                    exactDiscardTicks = (target != long.MinValue && Volatile.Read(ref _seekExact) == 1)
                        ? target
                        : long.MinValue;
                }

                if (eof)
                {
                    ffmpeg.avcodec_send_packet(_ctx, null);
                    ReceiveAndOutput(ref exactDiscardTicks);
                    _eofReached = true;
                    continue;
                }

                int ret = ffmpeg.avcodec_send_packet(_ctx, _pkt);
                ffmpeg.av_packet_unref(_pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    ReceiveAndOutput(ref exactDiscardTicks);
                    continue;
                }

                if (ret < 0)
                    continue;

                ReceiveAndOutput(ref exactDiscardTicks);
            }
        }

        private void ReceiveAndOutput(ref long exactDiscardTicks)
        {
            while (_running && !_flushRequested)
            {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    return;
                if (ret < 0)
                    return;

                long ptsTicks = 0;
                if (_frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
                    ptsTicks = (long)(_frame->best_effort_timestamp * ffmpeg.av_q2d(_timeBase) * TimeSpan.TicksPerSecond);

                EnsureResampler();

                int outSamples;
                fixed (float* pOut = _convBuffer)
                {
                    byte* outPtr = (byte*)pOut;
                    outSamples = ffmpeg.swr_convert(_swr, &outPtr, _convBuffer.Length / Channels,
                        _frame->extended_data, _frame->nb_samples);
                }

                ffmpeg.av_frame_unref(_frame);
                if (outSamples <= 0)
                    continue;

                int skipFrames = 0;
                long minBaseTicks = long.MinValue;
                if (exactDiscardTicks != long.MinValue)
                {
                    long frameEndTicks = ptsTicks + outSamples * TimeSpan.TicksPerSecond / _outRate;
                    if (frameEndTicks <= exactDiscardTicks)
                        continue; // whole frame before the seek target

                    skipFrames = (int)((exactDiscardTicks - ptsTicks) * _outRate / TimeSpan.TicksPerSecond);
                    if (skipFrames < 0)
                        skipFrames = 0;
                    if (skipFrames > outSamples)
                        skipFrames = outSamples;

                    // the trim floors to a sample boundary, which can land the base pts a
                    // fraction of a sample *before* the seek target — clamp up, or a skip-range
                    // resume at exactly the range end re-triggers the skip forever.
                    minBaseTicks = exactDiscardTicks;
                    exactDiscardTicks = long.MinValue;
                }

                long effectivePtsTicks = ptsTicks + skipFrames * TimeSpan.TicksPerSecond / _outRate;
                if (effectivePtsTicks < minBaseTicks)
                    effectivePtsTicks = minBaseTicks;
                _sink.TrySetBasePts(new TimeSpan(effectivePtsTicks));

                var span = new ReadOnlySpan<float>(_convBuffer, skipFrames * Channels,
                    (outSamples - skipFrames) * Channels);
                WriteToRing(span);
            }
        }

        private void WriteToRing(ReadOnlySpan<float> samples)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                if (!_running || _flushRequested)
                    return; // seek/stop: abandon the rest, it is about to be flushed anyway

                int written = _ring.Write(samples.Slice(offset));
                if (written == 0)
                {
                    Thread.Sleep(5); // ring full (~500ms buffered); wait for the device to drain
                    continue;
                }

                offset += written;
            }
        }

        private void EnsureResampler()
        {
            // ch_layout.order+nb_channels+rate+fmt is enough to detect a change in practice.
            ulong mask = _frame->ch_layout.u.mask;
            if (_swr != null && _frame->sample_rate == _swrInRate && _frame->format == _swrInFormat &&
                _frame->ch_layout.nb_channels == _swrInChannels && mask == _swrInLayoutMask)
                return;

            if (_swr != null)
            {
                var s = _swr;
                ffmpeg.swr_free(&s);
                _swr = null;
            }

            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, Channels);

            SwrContext* swr = null;
            AVChannelLayout* inLayout = &_frame->ch_layout;
            int err = ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT,
                _outRate, inLayout, (AVSampleFormat)_frame->format, _frame->sample_rate, 0, null);
            if (err < 0)
                throw new InvalidOperationException("swr_alloc_set_opts2: " + FFmpegLoader.ErrorToString(err));

            int initErr = ffmpeg.swr_init(swr);
            if (initErr < 0)
            {
                ffmpeg.swr_free(&swr);
                throw new InvalidOperationException("swr_init: " + FFmpegLoader.ErrorToString(initErr));
            }

            _swr = swr;
            _swrInRate = _frame->sample_rate;
            _swrInFormat = _frame->format;
            _swrInChannels = _frame->ch_layout.nb_channels;
            _swrInLayoutMask = mask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _running = false;
            _flushRequested = true;
            _thread?.Join(3000);

            if (_swr != null)
            {
                var s = _swr;
                ffmpeg.swr_free(&s);
                _swr = null;
            }

            if (_frame != null)
            {
                var f = _frame;
                ffmpeg.av_frame_free(&f);
                _frame = null;
            }

            if (_pkt != null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }

            if (_ctx != null)
            {
                var c = _ctx;
                ffmpeg.avcodec_free_context(&c);
                _ctx = null;
            }
        }
    }
}
