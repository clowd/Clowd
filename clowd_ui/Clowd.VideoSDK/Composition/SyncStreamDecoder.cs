using System;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Synchronous forward-only decode of one video stream for the render path: no threads, no
    /// queues, no seeking — <see cref="DecodeNext"/> blocks until the next frame is decoded and
    /// sws_scaled to BGRA in a pooled CPU buffer. Software decode only (the render loop is
    /// throughput-bound on encode, and hardware decode contexts are not worth their failure
    /// modes in a batch pipeline).
    ///
    /// Timestamp handling mirrors <c>VideoDecodeWorker.PtsToTicks</c>: <c>best_effort_timestamp</c>
    /// rescaled with integer math (<see cref="TimeBase"/>), <c>AV_NOPTS_VALUE</c> falling back to
    /// <c>last + frame duration</c> — with the stream's <c>start_time</c> subtracted so decoded
    /// time starts at 0 (the model's <c>SourceInTicks</c> convention). Non-monotonic PTS are
    /// clamped by the consuming <see cref="SequentialFrameCursor{T}"/>.
    /// </summary>
    internal sealed unsafe class SyncStreamDecoder : IDisposable
    {
        private readonly int _streamIndex;
        private readonly FrameBufferPool _pool;

        private AVFormatContext* _fmt;
        private AVCodecContext* _ctx;
        private AVFrame* _frame;
        private AVPacket* _pkt;
        private SwsContext* _sws;

        private readonly AVRational _timeBase;
        private readonly long _startTimeTicks;
        private readonly long _frameDurTicks;
        private long _lastPtsTicks = long.MinValue;
        private bool _draining;
        private bool _disposed;

        // reused pointer arrays for sws_scale (no per-frame allocation)
        private readonly byte*[] _srcData = new byte*[4];
        private readonly int[] _srcStride = new int[4];
        private readonly byte*[] _dstData = new byte*[4];
        private readonly int[] _dstStride = new int[4];

        public SyncStreamDecoder(string path, int streamIndex, FrameBufferPool pool)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(pool);
            FFmpegLoader.EnsureInitialized();

            _streamIndex = streamIndex;
            _pool = pool;

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
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    throw new ArgumentException($"Stream {streamIndex} of '{path}' is not a video stream.");

                _timeBase = st->time_base;
                bool tbValid = _timeBase.num > 0 && _timeBase.den > 0;

                _startTimeTicks = tbValid && st->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? TimeBase.StreamTimeToTicks(st->start_time, _timeBase.num, _timeBase.den)
                    : 0;

                // nominal frame duration, used only for the AV_NOPTS fallback.
                var rate = st->avg_frame_rate;
                if (rate.num <= 0 || rate.den <= 0)
                    rate = st->r_frame_rate;
                _frameDurTicks = rate.num > 0 && rate.den > 0
                    ? TimeBase.Rescale(1, rate.den, rate.num, 1, TimeBase.TicksPerSecond)
                    : TimeBase.TicksPerSecond / 30;

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
                _ctx->thread_count = 0; // auto (frame+slice threading)

                err = ffmpeg.avcodec_open2(_ctx, codec, null);
                if (err < 0)
                    throw new InvalidOperationException("avcodec_open2: " + FFmpegLoader.ErrorToString(err));

                Width = st->codecpar->width;
                Height = st->codecpar->height;

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

        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// Decodes the next frame of the stream. Returns false at end of stream (final). On
        /// success the caller owns <paramref name="buffer"/> (BGRA, top-down,
        /// <paramref name="rowBytes"/> stride) and must eventually return it to the pool.
        /// </summary>
        public bool DecodeNext(out long ptsTicks, out FrameBuffer buffer,
            out int width, out int height, out int rowBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ptsTicks = 0;
            buffer = null;
            width = height = rowBytes = 0;

            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (ret == 0)
                {
                    Convert(out ptsTicks, out buffer, out width, out height, out rowBytes);
                    return true;
                }

                if (ret == ffmpeg.AVERROR_EOF)
                    return false;

                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    throw new InvalidOperationException("avcodec_receive_frame: " + FFmpegLoader.ErrorToString(ret));

                if (_draining)
                    return false; // defensive: EAGAIN should not follow the drain packet

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

        private void Convert(out long ptsTicks, out FrameBuffer buffer,
            out int width, out int height, out int rowBytes)
        {
            try
            {
                width = _frame->width;
                height = _frame->height;
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException($"Decoder produced a {width}x{height} frame.");

                rowBytes = width * 4;
                buffer = _pool.Rent(rowBytes * height);
                try
                {
                    _sws = ffmpeg.sws_getCachedContext(_sws, width, height, (AVPixelFormat)_frame->format,
                        width, height, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
                    if (_sws == null)
                        throw new InvalidOperationException("sws_getCachedContext failed for format " + _frame->format);

                    for (uint i = 0; i < 4; i++)
                    {
                        _srcData[i] = _frame->data[i];
                        _srcStride[i] = _frame->linesize[i];
                    }

                    _dstData[0] = (byte*)buffer.Address;
                    _dstStride[0] = rowBytes;
                    _dstData[1] = _dstData[2] = _dstData[3] = null;
                    _dstStride[1] = _dstStride[2] = _dstStride[3] = 0;

                    ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, height, _dstData, _dstStride);
                }
                catch
                {
                    buffer.Return();
                    buffer = null;
                    throw;
                }

                long bet = _frame->best_effort_timestamp;
                if (bet == ffmpeg.AV_NOPTS_VALUE || _timeBase.num <= 0 || _timeBase.den <= 0)
                {
                    ptsTicks = _lastPtsTicks == long.MinValue ? 0 : _lastPtsTicks + _frameDurTicks;
                }
                else
                {
                    ptsTicks = TimeBase.StreamTimeToTicks(bet, _timeBase.num, _timeBase.den) - _startTimeTicks;
                }

                _lastPtsTicks = ptsTicks;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
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
