using System;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>Options for <see cref="Mp4Writer"/>. Width/Height are the output canvas (must be
    /// even — 4:2:0 chroma), FpsNum/FpsDen the constant output rate.</summary>
    public sealed class Mp4WriterOptions
    {
        /// <summary>Default crf, matching vid-render's args contract (args.rs DEFAULT_CRF).</summary>
        public const int DefaultCrf = 21;

        public int Width { get; init; }
        public int Height { get; init; }
        public int FpsNum { get; init; }
        public int FpsDen { get; init; } = 1;

        /// <summary>Constant rate factor, 0-51 (lower = higher quality), on x264's scale; every
        /// encoder maps it onto its own (<see cref="H264EncoderSettings"/>).</summary>
        public int Crf { get; init; } = DefaultCrf;

        /// <summary>
        /// Which H.264 encoder writes the video stream. <see cref="VideoEncoder.Software"/> by
        /// default: the writer is the SDK's primitive, used by fixtures and tests that want
        /// deterministic bytes from any machine, and x264 is the only encoder that guarantees
        /// that. The render job opts into <see cref="VideoEncoder.Auto"/>. A hardware encoder
        /// that cannot open, or fails at its first frame, falls back to x264 with a
        /// <see cref="DiagnosticLog"/> line; <see cref="Mp4Writer.Encoder"/> says what ran.
        /// </summary>
        public VideoEncoder Encoder { get; init; } = VideoEncoder.Software;

        /// <summary>Receives encoder selection and fallback diagnostics (the SDK has no logging
        /// dependency). Called from the constructing thread.</summary>
        public Action<string> DiagnosticLog { get; init; }

        /// <summary>
        /// GPU-resident input frames for the zero-copy encode path: when set and the encoder is
        /// one with a Direct3D 11 input (NVENC, AMF), the writer tries to open it over these
        /// frames and, if that works, takes its video through
        /// <see cref="Mp4Writer.SubmitVideoFrame(HardwareFrame, long)"/> only. When the encoder
        /// refuses them (or is x264) the writer logs why and opens the system-memory path
        /// instead — <see cref="Mp4Writer.HardwareFrames"/> says which it got. Null (the
        /// default) is the system-memory path. The frames must be at the output size, and they
        /// must outlive the writer.
        /// </summary>
        public HardwareFrames HardwareFrames { get; init; }

        /// <summary>Audio stream settings; null renders a video-only mp4.</summary>
        public Mp4AudioOptions Audio { get; init; }

        /// <summary>
        /// Encode video with a 1/1,000,000 (microsecond) time base instead of FpsDen/FpsNum, so
        /// <see cref="Mp4Writer.SubmitVideoFrame(IntPtr, int, int, int, long)"/> pts are
        /// microseconds and frames can sit on an arbitrary (VFR) grid. This is the time base
        /// vid-render's trim/concat filter graph negotiated, so a v1-compat VFR render muxes with
        /// the identical sample timing. FpsNum/FpsDen are still required (the encoder's
        /// rate-control/level hint and the GOP length).
        /// </summary>
        public bool UseMicrosecondTimeBase { get; init; }

        /// <summary>
        /// Reproduce vid-render's container timing exactly: submit video frames and packets with
        /// <b>no duration</b>, exactly as render.rs did. movenc then derives sample durations from
        /// dts deltas, gives the final sample duration 0, and rounds the track's edit-list duration
        /// up to whole milliseconds — with the quirk that a final frame whose pts lands on an exact
        /// millisecond falls outside the edit window and is invisible to decoders. The v1 parity
        /// gate requires this byte-level behavior; leave it off for v2 renders, where every sample
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
    /// mp4 muxer with an H.264 video stream and (optionally) an aac audio stream — the C# port of
    /// vid-render's <c>Writer</c> (render.rs), minus the filtergraph. Synchronous and dumb by
    /// design: the caller (RenderJob) drives the loop, owns progress and cancellation, and submits
    /// frames in presentation order.
    /// <para>
    /// Video: the encoder <see cref="Mp4WriterOptions.Encoder"/> selects — x264 (preset fast),
    /// NVENC, AMF or VideoToolbox, configured from the one crf by <see cref="H264EncoderSettings"/>
    /// — CFR at FpsNum/FpsDen. A hardware encoder is exercised at the output size before the
    /// header is written (<see cref="H264EncoderProbe"/>); if it fails to open or to take a
    /// frame, the writer opens x264 instead and logs why, so a render never fails for lack of a
    /// GPU encoder. Frames arrive either as BGRA (converted to NV12 internally by an
    /// <see cref="Nv12Converter"/>), already converted as an <see cref="Nv12Frame"/> — the
    /// render pipeline converts on its own stage so this thread only encodes — or, on the
    /// zero-copy path, as a GPU-resident <see cref="HardwareFrame"/> from the
    /// <see cref="Mp4WriterOptions.HardwareFrames"/> the encoder was opened over; <c>pts =
    /// frameIndex</c> in the encoder time base (FpsDen/FpsNum), so frame times are exact
    /// rational — never doubles or integer milliseconds.
    /// </para>
    /// <para>
    /// Audio: aac at 192 kb/s (aligned to the recorder's rate: speech and system audio get
    /// re-encoded again at upload time, and the headroom is cheap next to the video), fltp.
    /// Interleaved float input is FIFO-chunked to the encoder's frame_size (a short final frame
    /// is fine — same contract as av_buffersink_set_frame_size in render.rs); pts accounts in
    /// samples.
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
        /// <summary>AAC bitrate, bits/s — the recorder's 192 kbps (obs-express
        /// <c>AUDIO_BITRATE_KBPS</c>), up from the 160 kb/s render.rs used.</summary>
        public const int AudioBitRate = 192_000;

        private AVFormatContext* _fmt;
        private AVCodecContext* _venc;
        private AVCodecContext* _aenc; // null when video-only
        private Nv12Frame _scratch;    // reusable NV12 frame at output size (BGRA entry point)
        private Nv12Converter _converter; // BGRA -> NV12 for the last submitted source size
        private AVFrame* _aframe;      // reusable fltp frame at encoder frame_size
        private AVPacket* _pkt;
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

        public bool HasAudio => _aenc != null;

        /// <summary>The encoder that is actually writing the video stream — the requested one,
        /// what <see cref="VideoEncoder.Auto"/> resolved to, or <see cref="VideoEncoder.Software"/>
        /// after a hardware fallback.</summary>
        public VideoEncoder Encoder { get; private set; }

        /// <summary>The FFmpeg name of <see cref="Encoder"/> (<c>libx264</c>, <c>h264_nvenc</c>, …).</summary>
        public string EncoderName { get; private set; }

        /// <summary>The effective rate control and quality
        /// (<see cref="H264EncoderSettings.Description"/>), as logged.</summary>
        public string EncoderDescription { get; private set; }

        /// <summary>The GPU frames the video encoder reads from when the zero-copy path is
        /// active — the <see cref="Mp4WriterOptions.HardwareFrames"/> it was given — or null
        /// when video arrives in system memory (the option was unset, or the encoder would not
        /// open over the frames and the writer fell back).</summary>
        public HardwareFrames HardwareFrames { get; private set; }

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
                throw new ArgumentOutOfRangeException(nameof(options), $"Output size {options.Width}x{options.Height} must be even (4:2:0 chroma).");
            if (options.FpsNum <= 0 || options.FpsDen <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), $"Frame rate {options.FpsNum}/{options.FpsDen} components must be positive.");
            if (options.Crf < 0 || options.Crf > H264EncoderSettings.MaxCrf)
                throw new ArgumentOutOfRangeException(nameof(options), $"crf {options.Crf} out of range (0-{H264EncoderSettings.MaxCrf}).");
            if (!Enum.IsDefined(options.Encoder))
                throw new ArgumentOutOfRangeException(nameof(options), $"Unknown video encoder {options.Encoder}.");
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

            // ------------------------------------------------------------------- video: H.264
            OpenVideoEncoder(options, globalHeader);

            var vstream = ffmpeg.avformat_new_stream(_fmt, null);
            if (vstream == null)
                throw new InvalidOperationException("Could not create the output video stream.");
            Check(ffmpeg.avcodec_parameters_from_context(vstream->codecpar, _venc),
                "could not copy video encoder parameters");
            // The stream describes the coded picture, not how the encoder was fed: over
            // hardware frames the context's pix_fmt is the D3D11 handle type, so the container
            // gets the frames' actual layout (as it would from a system-memory NV12 context).
            if (HardwareFrames != null)
                vstream->codecpar->format = (int)HardwareFrames.SoftwareFormat;
            vstream->time_base = _venc->time_base;
            _vstreamIndex = vstream->index;

            if (HardwareFrames == null)
                _scratch = new Nv12Frame(options.Width, options.Height);

            // ------------------------------------------------------------- audio: aac 192 kb/s
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
                _aenc->bit_rate = AudioBitRate;
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

        /// <summary>
        /// Opens the video encoder into <c>_venc</c>: the requested encoder (Auto resolved by
        /// the process-wide probe), first over the caller's GPU frames when it has a Direct3D 11
        /// input path and they were offered, then in system memory; its bitrate variant when the
        /// quality variant will not open (Intel-Mac VideoToolbox); and x264 when a hardware
        /// encoder fails at this size — each step logged. A hardware encoder is exercised on a
        /// scratch context at the real size first (open, one frame, flush), because a failure
        /// at the first frame would otherwise surface after the mp4 header has been written
        /// with its parameters.
        /// </summary>
        private void OpenVideoEncoder(Mp4WriterOptions options, bool globalHeader)
        {
            var log = options.DiagnosticLog;
            // CFR: one pts unit == one frame, so SubmitVideoFrame's pts is the frame index.
            // Microsecond mode (VFR passthrough): pts are microseconds — the time base
            // vid-render's filter graph handed its encoder.
            var timeBase = options.UseMicrosecondTimeBase
                ? new AVRational { num = 1, den = 1_000_000 }
                : new AVRational { num = options.FpsDen, den = options.FpsNum };
            // Same sanity cap as render.rs: only advertise a plausible rate to the encoder's
            // level selection (frames keep their own pts either way).
            var framerate = options.FpsNum <= 240L * options.FpsDen
                ? new AVRational { num = options.FpsNum, den = options.FpsDen }
                : default;

            var chosen = H264EncoderProbe.Resolve(options.Encoder, log);
            var settings = H264EncoderSettings.For(chosen, options.Crf, options.Width, options.Height,
                options.FpsNum, options.FpsDen);

            AVCodecContext* ctx = null;
            string failure = null;
            if (options.HardwareFrames != null)
            {
                if (chosen is VideoEncoder.Nvenc or VideoEncoder.Amf)
                {
                    ctx = TryOpen(settings, options.HardwareFrames, out failure);
                    if (ctx != null)
                        HardwareFrames = options.HardwareFrames;
                    else
                        log?.Invoke($"Mp4Writer: {settings.CodecName} would not open over Direct3D 11 frames ({failure}); using system-memory frames");
                }
                else
                {
                    log?.Invoke($"Mp4Writer: {settings.CodecName} has no Direct3D 11 input; using system-memory frames");
                }
            }

            if (ctx == null)
                ctx = TryOpen(settings, null, out failure);
            if (ctx == null && settings.BitrateFallback != null)
            {
                log?.Invoke($"Mp4Writer: {settings.CodecName} quality mode unavailable ({failure}); trying its bitrate mode");
                settings = settings.BitrateFallback;
                ctx = TryOpen(settings, null, out failure);
            }
            if (ctx == null && chosen != VideoEncoder.Software)
            {
                log?.Invoke($"Mp4Writer: {settings.CodecName} failed at {options.Width}x{options.Height} ({failure}); falling back to libx264");
                settings = H264EncoderSettings.For(VideoEncoder.Software, options.Crf, options.Width, options.Height,
                    options.FpsNum, options.FpsDen);
                ctx = TryOpen(settings, null, out failure);
            }
            if (ctx == null)
                throw new InvalidOperationException($"could not open the h264 encoder ({settings.CodecName}): {failure}");

            _venc = ctx;
            Encoder = settings.Encoder;
            EncoderName = settings.CodecName;
            EncoderDescription = settings.Description;
            log?.Invoke($"Mp4Writer: video encoder {settings.CodecName} ({settings.Description}; " +
                        $"requested {VideoEncoderNames.Of(options.Encoder)}, crf {options.Crf}" +
                        (HardwareFrames != null ? "; input: " + HardwareFrames.Description : "") + ")");

            AVCodecContext* TryOpen(H264EncoderSettings s, HardwareFrames hw, out string why)
            {
                // x264 cannot fail at its first frame once open; the hardware encoders can.
                if (s.Encoder != VideoEncoder.Software
                    && !H264EncoderProbe.TryExercise(s, options.Width, options.Height, options.FpsNum, options.FpsDen, hw, out why))
                    return null;
                return H264EncoderProbe.TryOpenContext(s, options.Width, options.Height, timeBase, framerate,
                    globalHeader, hw, out why);
            }
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
            ThrowIfHardwareInput();
            if (bgra == IntPtr.Zero)
                throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Source size {width}x{height} is not positive.");
            if (rowBytes < width * 4)
                throw new ArgumentOutOfRangeException(nameof(rowBytes), $"rowBytes {rowBytes} is smaller than {width}*4.");

            if (_converter == null || _converter.SourceWidth != width || _converter.SourceHeight != height)
            {
                _converter?.Dispose();
                _converter = null;
                _converter = new Nv12Converter(width, height, _venc->width, _venc->height);
            }

            _converter.Convert(bgra, rowBytes, _scratch);
            SubmitVideoFrame(_scratch, pts);
        }

        /// <summary>
        /// Encodes one already-converted NV12 frame at the output size (see
        /// <see cref="Nv12Converter"/>); <paramref name="pts"/> as for the BGRA overload. The
        /// encoder has copied the frame when this returns (every encoder here copies a
        /// system-memory picture inside <c>avcodec_send_frame</c>), so the caller may refill it
        /// immediately.
        /// </summary>
        public void SubmitVideoFrame(Nv12Frame frame, long pts)
        {
            ThrowIfNotWritable();
            ThrowIfHardwareInput();
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Width != _venc->width || frame.Height != _venc->height)
                throw new ArgumentException(
                    $"Frame is {frame.Width}x{frame.Height}, the output is {_venc->width}x{_venc->height}.", nameof(frame));
            var av = frame.Frame;
            if (av == null)
                throw new ObjectDisposedException(nameof(Nv12Frame));

            EncodeVideoFrame(av, pts);
        }

        /// <summary>
        /// Encodes one GPU-resident frame (zero-copy path: the writer must have been opened
        /// with <see cref="Mp4WriterOptions.HardwareFrames"/> and kept them, see
        /// <see cref="HardwareFrames"/>); <paramref name="pts"/> as for the BGRA overload. The
        /// encoder takes its own reference to the frame's texture and keeps it until it has read
        /// the picture — the caller disposes the frame right after this call and the texture
        /// returns to the pool when the encoder is done.
        /// </summary>
        public void SubmitVideoFrame(HardwareFrame frame, long pts)
        {
            ThrowIfNotWritable();
            ArgumentNullException.ThrowIfNull(frame);
            if (HardwareFrames == null)
                throw new InvalidOperationException("The writer takes system-memory frames; it was not opened over hardware frames.");
            if (frame.Width != _venc->width || frame.Height != _venc->height)
                throw new ArgumentException(
                    $"Frame is {frame.Width}x{frame.Height}, the output is {_venc->width}x{_venc->height}.", nameof(frame));
            var av = frame.Frame;
            if (av == null)
                throw new ObjectDisposedException(nameof(HardwareFrame));

            EncodeVideoFrame(av, pts);
        }

        private void EncodeVideoFrame(AVFrame* av, long pts)
        {
            av->pts = pts;
            // CFR: every frame lasts exactly one time-base unit. Propagates frame -> packet ->
            // movenc sample duration; without it the final sample gets duration 0 and the track's
            // avg_frame_rate probes back as nb_frames/(n-1 intervals) instead of the true rate.
            // Legacy mode leaves the duration unset — vid-render never set one, and its movenc
            // edit-list behavior (see Mp4WriterOptions.LegacyContainerTiming) depends on that.
            av->duration = _legacyContainerTiming ? 0 : 1;
            // No decoder upstream here, but keep render.rs's contract: the encoder chooses its own GOP.
            av->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            EncodeAndMux(_venc, _vstreamIndex, av, "video");
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

            _converter?.Dispose();
            _converter = null;
            _scratch?.Dispose();
            _scratch = null;
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

        private void ThrowIfHardwareInput()
        {
            if (HardwareFrames != null)
                throw new InvalidOperationException(
                    "The writer was opened over hardware frames; submit HardwareFrame instances.");
        }

        private static int Check(int error, string what)
        {
            if (error < 0)
                throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorToString(error)}");
            return error;
        }
    }
}
