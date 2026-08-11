using System;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>Options for <see cref="Mp4Writer"/>. Width/Height are the output canvas (must be
    /// even — yuv420p subsampling), FpsNum/FpsDen the constant output rate.</summary>
    public sealed class Mp4WriterOptions
    {
        /// <summary>x264 CRF default, matching vid-render's args contract (args.rs DEFAULT_CRF).</summary>
        public const int DefaultCrf = 21;

        public int Width { get; init; }
        public int Height { get; init; }
        public int FpsNum { get; init; }
        public int FpsDen { get; init; } = 1;

        /// <summary>x264 constant rate factor, 0-51 (lower = higher quality).</summary>
        public int Crf { get; init; } = DefaultCrf;

        /// <summary>Audio stream settings; null renders a video-only mp4.</summary>
        public Mp4AudioOptions Audio { get; init; }

        /// <summary>
        /// Encode video with a 1/1,000,000 (microsecond) time base instead of FpsDen/FpsNum, so
        /// <see cref="Mp4Writer.SubmitVideoFrame"/> pts are microseconds and frames can sit on an
        /// arbitrary (VFR) grid. This is the time base vid-render's trim/concat filter graph
        /// negotiated, so a v1-compat VFR render muxes with the identical sample timing.
        /// FpsNum/FpsDen are still required (x264's rate-control/level hint).
        /// </summary>
        public bool UseMicrosecondTimeBase { get; init; }

        /// <summary>
        /// Reproduce vid-render's container timing exactly: submit video frames and packets with
        /// <b>no duration</b>, exactly as render.rs did. movenc then derives sample durations from
        /// dts deltas, gives the final sample duration 0, and rounds the track's edit-list duration
        /// up to whole milliseconds — with the quirk that a final frame whose pts lands on an exact
        /// millisecond falls outside the edit window and is invisible to decoders. The v1 parity
        /// gate requires this byte-level behaviour; leave it off for v2 renders, where every sample
        /// carries its true duration and the last frame is always decodable.
        /// </summary>
        public bool LegacyContainerTiming { get; init; }
    }

    /// <summary>Audio settings for <see cref="Mp4Writer"/>. Submitted samples must already be at
    /// <see cref="SampleRate"/> — the writer does no resampling (the render mixer produces output
    /// at the project's rate by construction).</summary>
    public sealed class Mp4AudioOptions
    {
        public int SampleRate { get; init; } = 48000;
        public int Channels { get; init; } = 2;
    }

    /// <summary>
    /// mp4 muxer with a libx264 video stream and (optionally) an aac audio stream — the C# port of
    /// vid-render's <c>Writer</c> (render.rs), minus the filtergraph. Synchronous and dumb by
    /// design: the caller (RenderJob) drives the loop, owns progress and cancellation, and submits
    /// frames in presentation order.
    /// <para>
    /// Video: libx264, preset veryfast, CRF (default 21), yuv420p, CFR at FpsNum/FpsDen. Frames
    /// arrive as BGRA and are sws_scaled to yuv420p internally; <c>pts = frameIndex</c> in the
    /// encoder time base (FpsDen/FpsNum), so frame times are exact rational — never doubles or
    /// integer milliseconds.
    /// </para>
    /// <para>
    /// Audio: aac at 160 kb/s (render.rs's bit_rate), fltp. Interleaved float input is FIFO-chunked
    /// to the encoder's frame_size (a short final frame is fine — same contract as
    /// av_buffersink_set_frame_size in render.rs); pts accounts in samples.
    /// </para>
    /// <para>
    /// <c>+faststart</c> is set via movflags, so av_write_trailer rewrites the file to put moov
    /// first. Trailer semantics port render.rs's fix verbatim: FFmpeg deinits the muxer even when
    /// av_write_trailer fails (movenc frees its track array), so a retry from Dispose would
    /// dereference freed state — the trailer is marked written BEFORE the result is checked; one
    /// attempt, success or not.
    /// </para>
    /// </summary>
    public sealed unsafe class Mp4Writer : IDisposable
    {
        private AVFormatContext* _fmt;
        private AVCodecContext* _venc;
        private AVCodecContext* _aenc; // null when video-only
        private AVFrame* _vframe;      // reusable yuv420p frame at output size
        private AVFrame* _aframe;      // reusable fltp frame at encoder frame_size
        private AVPacket* _pkt;
        private SwsContext* _sws;      // cached BGRA -> yuv420p scaler
        private int _vstreamIndex = -1;
        private int _astreamIndex = -1;
        private bool _legacyContainerTiming;

        private bool _headerWritten;
        private bool _trailerWritten;
        private bool _finished;
        private bool _abandoned;
        private bool _disposed;

        // audio FIFO: interleaved floats not yet chunked into an encoder frame
        private int _channels;
        private int _audioFrameSize;   // encoder nb_samples per frame
        private long _audioPts;        // in samples (encoder time base is 1/sample_rate)
        private float[] _fifo = Array.Empty<float>();
        private int _fifoCount;

        // reused pointer arrays for sws_scale (no per-frame allocation)
        private readonly byte*[] _srcData = new byte*[4];
        private readonly int[] _srcStride = new int[4];
        private readonly byte*[] _dstData = new byte*[4];
        private readonly int[] _dstStride = new int[4];

        public bool HasAudio => _aenc != null;

        public Mp4Writer(string outputPath, Mp4WriterOptions options)
        {
            FFmpegLoader.EnsureInitialized();

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is empty.", nameof(outputPath));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Width <= 0 || options.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), $"Output size {options.Width}x{options.Height} is not positive.");
            if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(options), $"Output size {options.Width}x{options.Height} must be even (yuv420p).");
            if (options.FpsNum <= 0 || options.FpsDen <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), $"Frame rate {options.FpsNum}/{options.FpsDen} components must be positive.");
            if (options.Crf < 0 || options.Crf > 51)
                throw new ArgumentOutOfRangeException(nameof(options), $"crf {options.Crf} out of range (0-51).");
            if (options.Audio != null)
            {
                if (options.Audio.SampleRate <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), $"Sample rate {options.Audio.SampleRate} must be positive.");
                if (options.Audio.Channels <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), $"Channel count {options.Audio.Channels} must be positive.");
            }

            try
            {
                Initialize(outputPath, options);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Initialize(string outputPath, Mp4WriterOptions options)
        {
            _legacyContainerTiming = options.LegacyContainerTiming;
            AVFormatContext* fmt = null;
            Check(ffmpeg.avformat_alloc_output_context2(&fmt, null, "mp4", outputPath),
                "could not create mp4 muxer");
            _fmt = fmt;
            bool globalHeader = (_fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

            // ------------------------------------------------------------------ video: libx264
            var vcodec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            if (vcodec == null)
                throw new InvalidOperationException("libx264 encoder not available in the bundled FFmpeg.");

            _venc = ffmpeg.avcodec_alloc_context3(vcodec);
            if (_venc == null)
                throw new InvalidOperationException("Could not allocate video encoder context.");

            _venc->width = options.Width;
            _venc->height = options.Height;
            _venc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            // CFR: one pts unit == one frame, so SubmitVideoFrame's pts is the frame index.
            // Microsecond mode (VFR passthrough): pts are microseconds — the time base
            // vid-render's filter graph handed its encoder.
            _venc->time_base = options.UseMicrosecondTimeBase
                ? new AVRational { num = 1, den = 1_000_000 }
                : new AVRational { num = options.FpsDen, den = options.FpsNum };
            // Same sanity cap as render.rs: only advertise a plausible rate to x264's level
            // selection (frames keep their own pts either way).
            if (options.FpsNum <= 240L * options.FpsDen)
                _venc->framerate = new AVRational { num = options.FpsNum, den = options.FpsDen };
            if (globalHeader)
                _venc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            Check(ffmpeg.av_opt_set(_venc->priv_data, "preset", "veryfast", 0), "could not set x264 preset");
            Check(ffmpeg.av_opt_set(_venc->priv_data, "crf", options.Crf.ToString(), 0), "could not set x264 crf");
            Check(ffmpeg.avcodec_open2(_venc, vcodec, null), "could not open the h264 encoder");

            var vstream = ffmpeg.avformat_new_stream(_fmt, null);
            if (vstream == null)
                throw new InvalidOperationException("Could not create the output video stream.");
            Check(ffmpeg.avcodec_parameters_from_context(vstream->codecpar, _venc),
                "could not copy video encoder parameters");
            vstream->time_base = _venc->time_base;
            _vstreamIndex = vstream->index;

            _vframe = ffmpeg.av_frame_alloc();
            if (_vframe == null)
                throw new InvalidOperationException("Could not allocate the video frame.");
            _vframe->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _vframe->width = options.Width;
            _vframe->height = options.Height;
            Check(ffmpeg.av_frame_get_buffer(_vframe, 0), "could not allocate video frame buffers");

            // ------------------------------------------------------------- audio: aac 160 kb/s
            if (options.Audio != null)
            {
                var acodec = ffmpeg.avcodec_find_encoder_by_name("aac");
                if (acodec == null)
                    throw new InvalidOperationException("aac encoder not available in the bundled FFmpeg.");

                _aenc = ffmpeg.avcodec_alloc_context3(acodec);
                if (_aenc == null)
                    throw new InvalidOperationException("Could not allocate audio encoder context.");

                _channels = options.Audio.Channels;
                _aenc->sample_rate = options.Audio.SampleRate;
                _aenc->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                ffmpeg.av_channel_layout_default(&_aenc->ch_layout, _channels);
                _aenc->time_base = new AVRational { num = 1, den = options.Audio.SampleRate };
                _aenc->bit_rate = 160_000;
                if (globalHeader)
                    _aenc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                Check(ffmpeg.avcodec_open2(_aenc, acodec, null), "could not open the aac encoder");

                // The aac encoder needs fixed-size frames; the FIFO delivers them pre-chunked
                // (a short final frame is fine) — same contract render.rs got from
                // av_buffersink_set_frame_size.
                _audioFrameSize = _aenc->frame_size > 0 ? _aenc->frame_size : 1024;

                var astream = ffmpeg.avformat_new_stream(_fmt, null);
                if (astream == null)
                    throw new InvalidOperationException("Could not create the output audio stream.");
                Check(ffmpeg.avcodec_parameters_from_context(astream->codecpar, _aenc),
                    "could not copy audio encoder parameters");
                astream->time_base = _aenc->time_base;
                _astreamIndex = astream->index;

                _aframe = ffmpeg.av_frame_alloc();
                if (_aframe == null)
                    throw new InvalidOperationException("Could not allocate the audio frame.");
                _aframe->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                _aframe->sample_rate = options.Audio.SampleRate;
                _aframe->nb_samples = _audioFrameSize;
                Check(ffmpeg.av_channel_layout_copy(&_aframe->ch_layout, &_aenc->ch_layout),
                    "could not copy the audio channel layout");
                Check(ffmpeg.av_frame_get_buffer(_aframe, 0), "could not allocate audio frame buffers");
            }

            _pkt = ffmpeg.av_packet_alloc();
            if (_pkt == null)
                throw new InvalidOperationException("Could not allocate the encode packet.");

            // AVIO_FLAG_WRITE truncates an existing file, per the contract.
            Check(ffmpeg.avio_open(&_fmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE),
                "could not open output file");

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
            int header = ffmpeg.avformat_write_header(_fmt, &opts);
            ffmpeg.av_dict_free(&opts);
            Check(header, "could not write mp4 header");
            _headerWritten = true;
        }

        // ------------------------------------------------------------------------------- video

        /// <summary>
        /// Encodes one BGRA frame. <paramref name="pts"/> is in the encoder time base: the output
        /// frame number in CFR mode (time base FpsDen/FpsNum), microseconds when
        /// <see cref="Mp4WriterOptions.UseMicrosecondTimeBase"/> is set; frames must be submitted
        /// in increasing pts order. The source is scaled to the output size when it differs.
        /// </summary>
        public void SubmitVideoFrame(IntPtr bgra, int rowBytes, int width, int height, long pts)
        {
            ThrowIfNotWritable();
            if (bgra == IntPtr.Zero)
                throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Source size {width}x{height} is not positive.");
            if (rowBytes < width * 4)
                throw new ArgumentOutOfRangeException(nameof(rowBytes), $"rowBytes {rowBytes} is smaller than {width}*4.");

            // The encoder may still hold refs to the frame's buffers; get fresh ones if so.
            Check(ffmpeg.av_frame_make_writable(_vframe), "could not make the video frame writable");

            _sws = ffmpeg.sws_getCachedContext(_sws, width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                _venc->width, _venc->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null)
                throw new InvalidOperationException("Could not create the BGRA->yuv420p scaler.");

            _srcData[0] = (byte*)bgra;
            _srcStride[0] = rowBytes;
            _srcData[1] = _srcData[2] = _srcData[3] = null;
            _srcStride[1] = _srcStride[2] = _srcStride[3] = 0;
            for (uint i = 0; i < 4; i++)
            {
                _dstData[i] = _vframe->data[i];
                _dstStride[i] = _vframe->linesize[i];
            }
            ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, height, _dstData, _dstStride);

            _vframe->pts = pts;
            // CFR: every frame lasts exactly one time-base unit. Propagates frame -> packet ->
            // movenc sample duration; without it the final sample gets duration 0 and the track's
            // avg_frame_rate probes back as nb_frames/(n-1 intervals) instead of the true rate.
            // Legacy mode leaves the duration unset — vid-render never set one, and its movenc
            // edit-list behaviour (see Mp4WriterOptions.LegacyContainerTiming) depends on that.
            _vframe->duration = _legacyContainerTiming ? 0 : 1;
            // No decoder upstream here, but keep render.rs's contract: x264 chooses its own GOP.
            _vframe->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            EncodeAndMux(_venc, _vstreamIndex, _vframe, "video");
        }

        // ------------------------------------------------------------------------------- audio

        /// <summary>
        /// Queues interleaved float samples (<paramref name="frames"/> frames of
        /// <c>Channels</c> samples each) and encodes every full encoder frame that becomes
        /// available. Input must already be at the output sample rate — no resampling happens here.
        /// </summary>
        public void SubmitAudioSamples(float[] interleaved, int frames)
        {
            ThrowIfNotWritable();
            if (_aenc == null)
                throw new InvalidOperationException("The writer was created without an audio stream.");
            if (interleaved == null)
                throw new ArgumentNullException(nameof(interleaved));
            if (frames < 0 || (long)frames * _channels > interleaved.Length)
                throw new ArgumentOutOfRangeException(nameof(frames),
                    $"{frames} frames of {_channels} channels do not fit in a buffer of {interleaved.Length} floats.");
            if (frames == 0)
                return;

            int floats = frames * _channels;
            if (_fifoCount + floats > _fifo.Length)
            {
                int cap = Math.Max(_fifo.Length * 2, _fifoCount + floats);
                cap = Math.Max(cap, _audioFrameSize * _channels * 4);
                Array.Resize(ref _fifo, cap);
            }
            Array.Copy(interleaved, 0, _fifo, _fifoCount, floats);
            _fifoCount += floats;

            DrainAudioFifo(final: false);
        }

        /// <summary>Encodes every full frame in the FIFO; with <paramref name="final"/> also the
        /// short remainder (aac accepts a smaller-than-frame_size last frame).</summary>
        private void DrainAudioFifo(bool final)
        {
            int floatsPerFrame = _audioFrameSize * _channels;
            int offset = 0;
            while (_fifoCount - offset >= floatsPerFrame)
            {
                EncodeAudioChunk(_fifo, offset, _audioFrameSize);
                offset += floatsPerFrame;
            }
            if (final && _fifoCount - offset >= _channels)
            {
                int remFrames = (_fifoCount - offset) / _channels;
                EncodeAudioChunk(_fifo, offset, remFrames);
                offset += remFrames * _channels;
            }
            if (offset > 0)
            {
                Array.Copy(_fifo, offset, _fifo, 0, _fifoCount - offset);
                _fifoCount -= offset;
            }
        }

        private void EncodeAudioChunk(float[] src, int offset, int frames)
        {
            Check(ffmpeg.av_frame_make_writable(_aframe), "could not make the audio frame writable");
            // Shrinking nb_samples below the allocated frame_size is fine (final short frame);
            // it never grows past the allocation.
            _aframe->nb_samples = frames;

            // interleaved -> planar (fltp)
            for (int ch = 0; ch < _channels; ch++)
            {
                float* plane = (float*)_aframe->data[(uint)ch];
                for (int i = 0; i < frames; i++)
                    plane[i] = src[offset + i * _channels + ch];
            }

            _aframe->pts = _audioPts; // encoder time base is 1/sample_rate: pts counts samples
            _aframe->duration = frames;
            _audioPts += frames;
            EncodeAndMux(_aenc, _astreamIndex, _aframe, "audio");
        }

        // ------------------------------------------------------------------------ mux + finish

        /// <summary>send_frame/receive_packet loop for one encoder; muxes every packet with the
        /// stream-time-base rescale. A null frame flushes.</summary>
        private void EncodeAndMux(AVCodecContext* enc, int streamIndex, AVFrame* frame, string what)
        {
            // Our loop always drains, so EAGAIN on send would be a logic error; treat it like any
            // other failure (same stance as render.rs).
            Check(ffmpeg.avcodec_send_frame(enc, frame), $"could not send frame to the {what} encoder");
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_packet(enc, _pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    return;
                Check(ret, $"could not encode {what}");

                // libx264 leaves packet duration at 0; movenc then writes the final sample with
                // duration 0 and the track's avg_frame_rate probes back wrong (n frames over n-1
                // intervals). CFR means every video packet lasts exactly one time-base unit.
                // Legacy mode keeps vid-render's zero-duration packets (movenc infers durations
                // from dts deltas), which the v1 parity gate depends on.
                if (_pkt->duration == 0 && enc == _venc && !_legacyContainerTiming)
                    _pkt->duration = 1;

                var stream = _fmt->streams[streamIndex];
                ffmpeg.av_packet_rescale_ts(_pkt, enc->time_base, stream->time_base);
                _pkt->stream_index = streamIndex;
                // av_interleaved_write_frame owns (and unrefs) the packet, success or not.
                Check(ffmpeg.av_interleaved_write_frame(_fmt, _pkt), $"could not write {what} packet");
            }
        }

        /// <summary>
        /// Drains both encoders, writes the trailer (which performs the +faststart rewrite) and
        /// closes the output file. The file is complete when this returns. Idempotent.
        /// </summary>
        public void Finish()
        {
            ThrowIfDisposed();
            if (_finished)
                return;
            _finished = true;

            EncodeAndMux(_venc, _vstreamIndex, null, "video");
            if (_aenc != null)
            {
                DrainAudioFifo(final: true);
                EncodeAndMux(_aenc, _astreamIndex, null, "audio");
            }

            // Port of the render.rs double-trailer fix: mark the trailer written BEFORE checking
            // the result — FFmpeg deinits the muxer even when av_write_trailer fails (movenc frees
            // its track array), so a retry from Dispose would dereference freed state. One
            // attempt, success or not.
            int ret = ffmpeg.av_write_trailer(_fmt);
            _trailerWritten = true;
            Check(ret, "could not finalize the mp4");

            Check(ffmpeg.avio_closep(&_fmt->pb), "could not close the output file");
        }

        /// <summary>
        /// Marks the writer abandoned: <see cref="Dispose"/> skips the mp4 trailer and just
        /// closes the file handle and frees the contexts. The muxer runs with
        /// <c>movflags=+faststart</c>, so the trailer is not a small moov append — movenc's
        /// shift_data() re-reads and rewrites the <b>entire</b> mdat to relocate moov, which on
        /// a multi-GB partial render blocks the caller for tens of seconds. Callers that are
        /// about to delete the partial output anyway (render cancellation, render error) must
        /// abandon instead of finalizing. The resulting file has no moov and is unreadable —
        /// deleting it is the caller's contract. No effect once <see cref="Finish"/> has run.
        /// </summary>
        public void Abandon() => _abandoned = true;

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
            if (_vframe != null)
            {
                var f = _vframe;
                ffmpeg.av_frame_free(&f);
                _vframe = null;
            }
            if (_aframe != null)
            {
                var f = _aframe;
                ffmpeg.av_frame_free(&f);
                _aframe = null;
            }
            if (_pkt != null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }
            if (_venc != null)
            {
                var c = _venc;
                ffmpeg.avcodec_free_context(&c);
                _venc = null;
            }
            if (_aenc != null)
            {
                var c = _aenc;
                ffmpeg.avcodec_free_context(&c);
                _aenc = null;
            }
            if (_fmt != null)
            {
                // Abort path (Finish not reached): a header without a trailer would leave an
                // unreadable mp4 — try once, and never after a failed Finish (see Finish).
                // An Abandon()ed writer skips the trailer entirely: with +faststart it would
                // rewrite the whole file, and the caller is deleting the output anyway.
                if (_headerWritten && !_trailerWritten && !_abandoned)
                {
                    ffmpeg.av_write_trailer(_fmt);
                    _trailerWritten = true;
                }
                if (_fmt->pb != null)
                    ffmpeg.avio_closep(&_fmt->pb);
                ffmpeg.avformat_free_context(_fmt);
                _fmt = null;
            }
        }

        // ------------------------------------------------------------------------------ helpers

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Mp4Writer));
        }

        private void ThrowIfNotWritable()
        {
            ThrowIfDisposed();
            if (_finished)
                throw new InvalidOperationException("The writer is finished; no more frames can be submitted.");
        }

        private static int Check(int error, string what)
        {
            if (error < 0)
                throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorToString(error)}");
            return error;
        }
    }
}
