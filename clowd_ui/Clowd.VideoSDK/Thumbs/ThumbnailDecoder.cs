using System;
using Clowd.VideoSDK.Media;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// Decodes one video stream into filmstrip-sized BGRA thumbnails. Deliberately separate from
    /// the render-hot <c>SyncStreamDecoder</c>: it owns its own <c>AVFormatContext</c> (so it never
    /// contends with playback or render on the same file), it can seek, and it scales <i>inside</i>
    /// the decode loop — sws_scale goes straight from the decoded frame to the thumb size, so a
    /// 4K keyframe never materializes as a full-size BGRA buffer.
    ///
    /// <para>
    /// Two modes. <see cref="KeyframesOnly"/> is the fast whole-stream pass: non-key packets are
    /// dropped at the demuxer and the decoder is told to discard them too, so the pass runs at
    /// roughly demux speed. Clearing it (after a <see cref="Seek"/>) decodes every frame, which is
    /// how a grid slot inside a long GOP gets its exact frame.
    /// </para>
    ///
    /// <para>
    /// Timestamps follow the model's <c>SourceInTicks</c> convention exactly as
    /// <c>SyncStreamDecoder</c> does: <c>best_effort_timestamp</c> rescaled with integer math and
    /// the stream's <c>start_time</c> subtracted, <c>AV_NOPTS_VALUE</c> falling back to
    /// <c>last + frame duration</c>.
    /// </para>
    /// </summary>
    internal sealed unsafe class ThumbnailDecoder : IDisposable
    {
        /// <summary>Filmstrip row height in the timeline; the width follows the source aspect.</summary>
        public const int DefaultThumbHeightPx = 48;

        public const int MinThumbHeightPx = 8;
        public const int MaxThumbHeightPx = 512;

        private readonly int _streamIndex;

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
        private bool _keyframesOnly;
        private bool _hasThumb;
        private bool _disposed;

        private readonly byte[] _thumb;

        // reused pointer arrays for sws_scale (no per-frame allocation)
        private readonly byte*[] _srcData = new byte*[4];
        private readonly int[] _srcStride = new int[4];
        private readonly byte*[] _dstData = new byte*[4];
        private readonly int[] _dstStride = new int[4];

        public ThumbnailDecoder(string path, int streamIndex, int thumbHeightPx = DefaultThumbHeightPx)
        {
            ArgumentNullException.ThrowIfNull(path);
            FFmpegLoader.EnsureInitialized();

            _streamIndex = streamIndex;

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

                int srcWidth = st->codecpar->width;
                int srcHeight = st->codecpar->height;
                if (srcWidth <= 0 || srcHeight <= 0)
                    throw new InvalidOperationException($"Stream {streamIndex} of '{path}' has no frame size.");

                SourceWidth = srcWidth;
                SourceHeight = srcHeight;
                ThumbHeight = Math.Clamp(thumbHeightPx, MinThumbHeightPx, MaxThumbHeightPx);
                ThumbWidth = Math.Max(2, (int)Math.Round(srcWidth * (double)ThumbHeight / srcHeight));
                _thumb = new byte[ThumbByteCount];

                _timeBase = st->time_base;
                bool tbValid = _timeBase.num > 0 && _timeBase.den > 0;

                _startTimeTicks = tbValid && st->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? TimeBase.StreamTimeToTicks(st->start_time, _timeBase.num, _timeBase.den)
                    : 0;

                long containerDuration = _fmt->duration != ffmpeg.AV_NOPTS_VALUE
                    ? TimeBase.Rescale(_fmt->duration, 1, ffmpeg.AV_TIME_BASE, 1, TimeBase.TicksPerSecond)
                    : 0;
                DurationTicks = tbValid && st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0
                    ? TimeBase.StreamTimeToTicks(st->duration, _timeBase.num, _timeBase.den)
                    : containerDuration;

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
                // Single-threaded on purpose: FFmpeg's worker threads are created at the OS default
                // (normal) priority, not the creating thread's, so auto threading here would let a
                // background filmstrip pass outrank playback on a busy machine — the exact thing the
                // BelowNormal scheduler thread exists to prevent. Thumbnails are latency-tolerant.
                _ctx->thread_count = 1;

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

        public int SourceWidth { get; }
        public int SourceHeight { get; }

        public int ThumbWidth { get; }
        public int ThumbHeight { get; }
        public int ThumbStride => ThumbWidth * 4;
        public int ThumbByteCount => ThumbStride * ThumbHeight;

        /// <summary>Stream duration in ticks, falling back to the container's; 0 when neither is
        /// known (fragmented/streamed input).</summary>
        public long DurationTicks { get; }

        /// <summary>
        /// Decode only keyframes — the whole-stream fast pass. Non-key packets are never sent to
        /// the decoder, and <c>skip_frame</c> guards the case of a key packet carrying non-key
        /// frames. Safe to flip at any time; flip it around a <see cref="Seek"/> so the decoder
        /// starts from a clean state.
        /// </summary>
        public bool KeyframesOnly
        {
            get => _keyframesOnly;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _keyframesOnly = value;
                _ctx->skip_frame = value ? AVDiscard.AVDISCARD_NONKEY : AVDiscard.AVDISCARD_DEFAULT;
            }
        }

        /// <summary>
        /// Decodes the next frame into the internal thumb buffer, scaled to
        /// <see cref="ThumbWidth"/>x<see cref="ThumbHeight"/>. Returns false at end of stream
        /// (final). The pixels stay valid until the next call — copy them out with
        /// <see cref="CopyThumb"/> or <see cref="CopyThumbTo"/> to keep them.
        /// </summary>
        public bool DecodeNext(out long ptsTicks)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ptsTicks = 0;

            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (ret == 0)
                {
                    Convert(out ptsTicks);
                    _hasThumb = true;
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

        /// <summary>A fresh heap copy of the last decoded thumbnail (BGRA, top-down,
        /// <see cref="ThumbStride"/> stride) — what the cache retains.</summary>
        public byte[] CopyThumb()
        {
            var copy = new byte[ThumbByteCount];
            CopyThumbTo(copy);
            return copy;
        }

        /// <summary>Copies the last decoded thumbnail into a caller-owned buffer, so a scan that
        /// keeps only one frame out of a GOP allocates nothing per frame.</summary>
        public void CopyThumbTo(byte[] destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(destination);
            if (destination.Length < ThumbByteCount)
                throw new ArgumentException($"Destination holds {destination.Length} bytes, need {ThumbByteCount}.", nameof(destination));
            if (!_hasThumb)
                throw new InvalidOperationException("No frame has been decoded yet.");

            Buffer.BlockCopy(_thumb, 0, destination, 0, ThumbByteCount);
        }

        /// <summary>
        /// Repositions to the keyframe at or before <paramref name="ticks"/> (the same normalized
        /// stream time <see cref="DecodeNext"/> reports) and flushes every piece of decode state.
        /// The caller reaches an exact instant by decoding forward from here; video packets are not
        /// self-contained, so a backward seek to a keyframe is the only correct entry point.
        /// </summary>
        public void Seek(long ticks)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            bool tbValid = _timeBase.num > 0 && _timeBase.den > 0;
            long target = Math.Max(0, ticks);
            long ts = tbValid
                ? TimeBase.TicksToStreamTime(target + _startTimeTicks, _timeBase.num, _timeBase.den)
                : TimeBase.Rescale(target, 1, TimeBase.TicksPerSecond, 1, ffmpeg.AV_TIME_BASE);

            // without a usable stream time base the seek has to go through the container's
            // AV_TIME_BASE domain (stream index -1), exactly as the audio decoder does.
            int seekStream = tbValid ? _streamIndex : -1;
            int err = ffmpeg.av_seek_frame(_fmt, seekStream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (err < 0)
                ffmpeg.av_seek_frame(_fmt, seekStream, ts, 0); // some containers reject BACKWARD near 0/EOF

            ffmpeg.avcodec_flush_buffers(_ctx);
            ffmpeg.av_packet_unref(_pkt);
            ffmpeg.av_frame_unref(_frame);

            _lastPtsTicks = long.MinValue;
            _draining = false;
            _hasThumb = false;
        }

        /// <summary>Reads packets until one of this stream is fed to the decoder (or EOF starts the
        /// drain). In <see cref="KeyframesOnly"/> mode non-key packets are dropped here rather than
        /// decoded and thrown away, which is what makes the whole-stream pass cheap. Corrupt
        /// packets are skipped — decode resumes at the next one.</summary>
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

                if (_pkt->stream_index != _streamIndex
                    || (_keyframesOnly && (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0))
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

        private void Convert(out long ptsTicks)
        {
            try
            {
                int width = _frame->width;
                int height = _frame->height;
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException($"Decoder produced a {width}x{height} frame.");

                // SWS_LANCZOS (a=3): the big downscale to thumb size is where detail is decided —
                // fast-bilinear leaves fine texture aliased and slightly mushy next to a windowed
                // sinc, and the scaler is still far from the bottleneck at these output sizes.
                _sws = ffmpeg.sws_getCachedContext(_sws, width, height, (AVPixelFormat)_frame->format,
                    ThumbWidth, ThumbHeight, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_LANCZOS,
                    null, null, null);
                if (_sws == null)
                    throw new InvalidOperationException("sws_getCachedContext failed for format " + _frame->format);

                for (uint i = 0; i < 4; i++)
                {
                    _srcData[i] = _frame->data[i];
                    _srcStride[i] = _frame->linesize[i];
                }

                fixed (byte* dst = _thumb)
                {
                    _dstData[0] = dst;
                    _dstStride[0] = ThumbStride;
                    _dstData[1] = _dstData[2] = _dstData[3] = null;
                    _dstStride[1] = _dstStride[2] = _dstStride[3] = 0;

                    ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, height, _dstData, _dstStride);
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
