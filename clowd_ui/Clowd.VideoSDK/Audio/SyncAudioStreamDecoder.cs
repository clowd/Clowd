using System;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Synchronous forward-only decode of one audio stream for the render path — the audio twin
    /// of <c>SyncStreamDecoder</c>: no threads, no queues, no seeking. <see cref="DecodeNext"/>
    /// blocks until the next chunk of samples is decoded and swr_converted to interleaved float
    /// stereo at the output rate (the ONE spot where resample/layout conversion happens; per the
    /// design, only float <i>mixing</i> is managed — decode and resample stay native).
    ///
    /// Timestamp handling mirrors <c>AudioDecodeWorker</c>/<c>SyncStreamDecoder</c>:
    /// <c>best_effort_timestamp</c> rescaled with integer math (<see cref="TimeBase"/>),
    /// <c>AV_NOPTS_VALUE</c> falling back to <c>last + frame duration</c>, the stream's
    /// <c>start_time</c> subtracted so decoded time starts at 0. The consuming
    /// <see cref="SequentialAudioSource"/> owns positioning (gap silence, backwards-PTS clamp).
    /// </summary>
    internal sealed unsafe class SyncAudioStreamDecoder : IDisposable
    {
        private const int Channels = 2; // the SDK's fixed mixing layout (AudioMixer.Channels)

        private readonly int _streamIndex;
        private readonly int _outRate;

        private AVFormatContext* _fmt;
        private AVCodecContext* _ctx;
        private AVFrame* _frame;
        private AVPacket* _pkt;
        private SwrContext* _swr;

        // swr input-format change detection, same fields AudioDecodeWorker tracks
        private int _swrInRate;
        private int _swrInFormat = -1;
        private int _swrInChannels;
        private ulong _swrInLayoutMask;

        private readonly AVRational _timeBase;
        private readonly long _startTimeTicks;
        private long _lastPtsTicks = long.MinValue;
        private long _lastDurTicks;
        private bool _draining;    // codec drain packet sent
        private bool _codecDone;   // codec returned EOF; swr flush remains
        private bool _swrFlushed;
        private bool _disposed;

        private float[] _buffer = new float[8192 * Channels];

        public SyncAudioStreamDecoder(string path, int streamIndex, int outputSampleRate)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outputSampleRate, 0);
            FFmpegLoader.EnsureInitialized();

            _streamIndex = streamIndex;
            _outRate = outputSampleRate;

            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"Failed to open '{path}': {FFmpegLoader.ErrorToString(err)}");
            _fmt = fmt;

            try
            {
                err = ffmpeg.avformat_find_stream_info(_fmt, null);
                if (err < 0)
                    throw new InvalidOperationException($"Failed to read stream info: {FFmpegLoader.ErrorToString(err)}");

                if (streamIndex < 0 || streamIndex >= _fmt->nb_streams)
                    throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex,
                        $"'{path}' has {_fmt->nb_streams} streams.");

                var st = _fmt->streams[streamIndex];
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                    throw new ArgumentException($"Stream {streamIndex} of '{path}' is not an audio stream.");

                _timeBase = st->time_base;
                bool tbValid = _timeBase.num > 0 && _timeBase.den > 0;

                _startTimeTicks = tbValid && st->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? TimeBase.StreamTimeToTicks(st->start_time, _timeBase.num, _timeBase.den)
                    : 0;

                var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
                if (codec == null)
                    throw new InvalidOperationException("No decoder for codec " + st->codecpar->codec_id);

                _ctx = ffmpeg.avcodec_alloc_context3(codec);
                if (_ctx == null)
                    throw new OutOfMemoryException("avcodec_alloc_context3 failed");

                err = ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar);
                if (err < 0)
                    throw new InvalidOperationException("avcodec_parameters_to_context: " + FFmpegLoader.ErrorToString(err));

                _ctx->pkt_timebase = st->time_base;

                err = ffmpeg.avcodec_open2(_ctx, codec, null);
                if (err < 0)
                    throw new InvalidOperationException("avcodec_open2: " + FFmpegLoader.ErrorToString(err));

                _frame = ffmpeg.av_frame_alloc();
                _pkt = ffmpeg.av_packet_alloc();
                if (_frame == null || _pkt == null)
                    throw new OutOfMemoryException("av_frame/av_packet alloc failed");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Decodes the next chunk of the stream into <paramref name="samples"/> (interleaved
        /// stereo at the output rate; valid until the next call). Returns false at end of stream
        /// (final). <paramref name="ptsTicks"/> is the normalized input presentation time of the
        /// chunk's first sample, or <see cref="long.MinValue"/> for a resampler-flush chunk at
        /// EOF (append contiguously, no position to check).
        /// </summary>
        public bool DecodeNext(out long ptsTicks, out float[] samples, out int frames)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ptsTicks = long.MinValue;
            samples = _buffer;
            frames = 0;

            while (true)
            {
                if (_codecDone)
                    return FlushResampler(out frames);

                int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (ret == 0)
                {
                    if (Convert(out ptsTicks, out frames))
                    {
                        samples = _buffer;
                        return true;
                    }
                    continue; // resampler buffered everything (priming) — decode more
                }

                if (ret == ffmpeg.AVERROR_EOF)
                {
                    _codecDone = true;
                    continue;
                }

                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    throw new InvalidOperationException("avcodec_receive_frame: " + FFmpegLoader.ErrorToString(ret));

                if (_draining)
                {
                    _codecDone = true; // defensive: EAGAIN should not follow the drain packet
                    continue;
                }

                FeedPacket();
            }
        }

        /// <summary>Reads packets until one of this stream is fed to the decoder (or EOF starts
        /// the drain). Corrupt packets are skipped — decode resumes at the next one.</summary>
        private void FeedPacket()
        {
            while (true)
            {
                int ret = ffmpeg.av_read_frame(_fmt, _pkt);
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.avcodec_send_packet(_ctx, null);
                    _draining = true;
                    return;
                }

                if (ret < 0)
                    throw new InvalidOperationException("av_read_frame: " + FFmpegLoader.ErrorToString(ret));

                if (_pkt->stream_index != _streamIndex)
                {
                    ffmpeg.av_packet_unref(_pkt);
                    continue;
                }

                ret = ffmpeg.avcodec_send_packet(_ctx, _pkt);
                ffmpeg.av_packet_unref(_pkt);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    continue; // corrupt packet — skip
                return;
            }
        }

        /// <summary>swr_converts the decoded frame into <see cref="_buffer"/>. False when the
        /// resampler buffered all input (nothing to output yet).</summary>
        private bool Convert(out long ptsTicks, out int frames)
        {
            try
            {
                int inRate = _frame->sample_rate;
                int inSamples = _frame->nb_samples;
                if (inRate <= 0 || inSamples <= 0)
                {
                    ptsTicks = long.MinValue;
                    frames = 0;
                    return false;
                }

                EnsureResampler();

                // worst case output: buffered delay + this frame, rescaled to the output rate
                long cap = TimeBase.Rescale(ffmpeg.swr_get_delay(_swr, inRate) + inSamples,
                    1, inRate, 1, _outRate) + 64;
                EnsureBuffer((int)cap);

                int outFrames;
                fixed (float* pOut = _buffer)
                {
                    byte* outPtr = (byte*)pOut;
                    outFrames = ffmpeg.swr_convert(_swr, &outPtr, (int)cap,
                        _frame->extended_data, inSamples);
                }
                if (outFrames < 0)
                    throw new InvalidOperationException("swr_convert: " + FFmpegLoader.ErrorToString(outFrames));

                long bet = _frame->best_effort_timestamp;
                long pts;
                if (bet == ffmpeg.AV_NOPTS_VALUE || _timeBase.num <= 0 || _timeBase.den <= 0)
                {
                    pts = _lastPtsTicks == long.MinValue ? 0 : _lastPtsTicks + _lastDurTicks;
                }
                else
                {
                    pts = TimeBase.StreamTimeToTicks(bet, _timeBase.num, _timeBase.den) - _startTimeTicks;
                }

                _lastPtsTicks = pts;
                _lastDurTicks = TimeBase.Rescale(inSamples, 1, inRate, 1, TimeBase.TicksPerSecond);

                ptsTicks = pts;
                frames = outFrames;
                return outFrames > 0;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        /// <summary>Drains samples buffered inside the resampler after the codec is done. One
        /// chunk per call; false when nothing remains (end of stream, final).</summary>
        private bool FlushResampler(out int frames)
        {
            frames = 0;
            if (_swrFlushed || _swr == null)
                return false;

            EnsureBuffer(4096);
            int outFrames;
            fixed (float* pOut = _buffer)
            {
                byte* outPtr = (byte*)pOut;
                outFrames = ffmpeg.swr_convert(_swr, &outPtr, _buffer.Length / Channels, null, 0);
            }

            if (outFrames <= 0)
            {
                _swrFlushed = true;
                return false;
            }

            frames = outFrames;
            return true;
        }

        private void EnsureResampler()
        {
            // ch_layout.order+nb_channels+rate+fmt is enough to detect a change in practice
            // (same heuristic as AudioDecodeWorker).
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

        private void EnsureBuffer(int frames)
        {
            int floats = frames * Channels;
            if (_buffer.Length < floats)
                _buffer = new float[Math.Max(floats, _buffer.Length * 2)];
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

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

            if (_fmt != null)
            {
                var fmt = _fmt;
                ffmpeg.avformat_close_input(&fmt);
                _fmt = null;
            }
        }
    }
}
