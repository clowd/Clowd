using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// Decides what <see cref="VideoEncoder.Auto"/> means on this machine, and opens encoder
    /// contexts from <see cref="H264EncoderSettings"/> for <see cref="Mp4Writer"/>.
    ///
    /// <para>
    /// The name of a hardware encoder being present in the FFmpeg build says nothing about
    /// whether it works here: the bundled <c>avcodec-61.dll</c> carries <c>h264_amf</c> on every
    /// machine and its open fails in 0 ms on an NVIDIA box (<c>amfrt64.dll</c> is not there),
    /// <c>h264_nvenc</c> fails without an NVIDIA driver, and <c>h264_mf</c> fails to open on this
    /// NVIDIA machine outright. So the probe opens each candidate on a small context, sends one
    /// frame through it and flushes — the same calls a render makes — and the first that
    /// survives is the answer. The order NVENC → AMF → VideoToolbox (macOS) → x264 is the
    /// project owner's; the choice is cached for the process, since the hardware does not change
    /// under a running app and a probe costs an encoder session (tens of milliseconds on NVENC).
    /// </para>
    ///
    /// <para>
    /// <see cref="Choose"/> is the pure half — order and fallback over any "can this open" oracle
    /// — so the policy is testable with a fake availability list; <see cref="Resolve"/> is the
    /// real one. A hardware encoder chosen here can still fail at the render's real size (a
    /// resolution past the engine's limit, VRAM); <see cref="Mp4Writer"/> re-checks by exercising
    /// it at full size and falls back to x264 itself, so a render never fails for lack of a GPU
    /// encoder.
    /// </para>
    /// </summary>
    public static unsafe class H264EncoderProbe
    {
        /// <summary>The probe context's size: small enough to be instant, comfortably above
        /// NVENC's H.264 floor of 145x49 (measured on an RTX 4070: 144x48 is refused with
        /// "Frame Dimension less than the minimum supported value", 146x50 opens), which is
        /// also why hardware encoders are never asked to render the 64x64 test fixtures.</summary>
        public const int ProbeWidth = 320, ProbeHeight = 240;

        private const int ProbeFps = 30;

        private static readonly object _sync = new object();
        private static VideoEncoder? _autoChoice;

        /// <summary>The candidates <see cref="VideoEncoder.Auto"/> tries, in order, ending with
        /// the software fallback. VideoToolbox only exists on macOS, so it is only listed there.</summary>
        public static IReadOnlyList<VideoEncoder> AutoCandidates(bool macOS) => macOS
            ? new[] { VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.VideoToolbox, VideoEncoder.Software }
            : new[] { VideoEncoder.Nvenc, VideoEncoder.Amf, VideoEncoder.Software };

        /// <summary>
        /// The first of <paramref name="candidates"/> that <paramref name="tryOpen"/> accepts
        /// (null = opened; a string = why not), or <see cref="VideoEncoder.Software"/>.
        /// Software is never probed: it is the terminal fallback, and its absence is the
        /// writer's error to raise. One diagnostic line reports the outcome and every
        /// rejection.
        /// </summary>
        public static VideoEncoder Choose(IEnumerable<VideoEncoder> candidates,
            Func<VideoEncoder, string> tryOpen, Action<string> log)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            ArgumentNullException.ThrowIfNull(tryOpen);

            var rejected = new List<string>();
            VideoEncoder choice = VideoEncoder.Software;
            foreach (var candidate in candidates)
            {
                if (candidate == VideoEncoder.Software)
                    break;
                if (candidate == VideoEncoder.Auto)
                    throw new ArgumentException("Auto cannot be a candidate of itself.", nameof(candidates));

                string reason = tryOpen(candidate);
                if (reason == null)
                {
                    choice = candidate;
                    break;
                }

                rejected.Add($"{H264EncoderSettings.CodecNameOf(candidate)}: {reason}");
            }

            log?.Invoke($"H264EncoderProbe: auto -> {H264EncoderSettings.CodecNameOf(choice)}" +
                        (rejected.Count > 0 ? " (rejected " + string.Join("; ", rejected) + ")" : ""));
            return choice;
        }

        /// <summary>
        /// The concrete encoder for <paramref name="requested"/>: an explicit choice is returned
        /// as is (the writer opens it and falls back on failure); <see cref="VideoEncoder.Auto"/>
        /// runs the probe once per process and returns the cached answer after that.
        /// </summary>
        public static VideoEncoder Resolve(VideoEncoder requested, Action<string> log)
        {
            if (requested != VideoEncoder.Auto)
                return requested;

            lock (_sync)
            {
                if (_autoChoice is VideoEncoder cached)
                    return cached;

                var choice = Choose(AutoCandidates(OperatingSystem.IsMacOS()), candidate =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    bool ok = CanOpen(candidate, out var reason);
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    return ok ? null : $"{reason} ({ms.ToString("F0", CultureInfo.InvariantCulture)} ms)";
                }, log);
                _autoChoice = choice;
                return choice;
            }
        }

        /// <summary>
        /// Whether <paramref name="encoder"/> opens and encodes on this machine: a
        /// <see cref="ProbeWidth"/> x <see cref="ProbeHeight"/> context at the default crf,
        /// one frame sent, the encoder flushed. False for <see cref="VideoEncoder.Auto"/>.
        /// </summary>
        public static bool CanOpen(VideoEncoder encoder, out string reason)
        {
            if (encoder == VideoEncoder.Auto)
            {
                reason = "Auto is not a concrete encoder.";
                return false;
            }

            var settings = H264EncoderSettings.For(encoder, Mp4WriterOptions.DefaultCrf,
                ProbeWidth, ProbeHeight, ProbeFps, 1);
            return TryExercise(settings, ProbeWidth, ProbeHeight, ProbeFps, 1, out reason)
                || (settings.BitrateFallback != null
                    && TryExercise(settings.BitrateFallback, ProbeWidth, ProbeHeight, ProbeFps, 1, out reason));
        }

        /// <summary>
        /// Opens a scratch context for <paramref name="settings"/> at the given size, pushes one
        /// black NV12 frame through it and flushes it, then frees it. This is the cheap way to
        /// learn that a hardware encoder which opened will also encode: session limits, VRAM and
        /// driver refusals surface at the first <c>avcodec_send_frame</c> or at the flush, and
        /// an encoder cannot be reused after a flush, so the real context is opened separately
        /// afterwards. Costs one extra session open — tens of milliseconds on NVENC, once per
        /// render.
        /// </summary>
        internal static bool TryExercise(H264EncoderSettings settings, int width, int height,
            int fpsNum, int fpsDen, out string failure)
            => TryExercise(settings, width, height, fpsNum, fpsDen, null, out failure);

        /// <summary>
        /// As <see cref="TryExercise(H264EncoderSettings, int, int, int, int, out string)"/>, with
        /// the encoder opened over <paramref name="hardwareFrames"/> when given: the one frame
        /// sent is then a pooled GPU texture (its contents do not matter to an open test), which
        /// the closed encoder hands back before this returns.
        /// </summary>
        internal static bool TryExercise(H264EncoderSettings settings, int width, int height,
            int fpsNum, int fpsDen, HardwareFrames hardwareFrames, out string failure)
        {
            var ctx = TryOpenContext(settings, width, height,
                new AVRational { num = fpsDen, den = fpsNum }, new AVRational { num = fpsNum, den = fpsDen },
                globalHeader: false, hardwareFrames, out failure);
            if (ctx == null)
                return false;

            AVPacket* pkt = null;
            Nv12Frame frame = null;
            HardwareFrame hardwareFrame = null;
            try
            {
                pkt = ffmpeg.av_packet_alloc();
                AVFrame* input;
                if (hardwareFrames != null)
                {
                    hardwareFrame = hardwareFrames.Wrap(hardwareFrames.RentTexture());
                    input = hardwareFrame.Frame;
                }
                else
                {
                    frame = new Nv12Frame(width, height);
                    frame.FillBlack();
                    input = frame.Frame;
                }
                input->pts = 0;
                input->duration = 1;

                int ret = ffmpeg.avcodec_send_frame(ctx, input);
                if (ret < 0)
                {
                    failure = "send_frame: " + FFmpegLoader.ErrorToString(ret);
                    return false;
                }
                if (!Drain(ctx, pkt, out failure))
                    return false;

                ret = ffmpeg.avcodec_send_frame(ctx, null);
                if (ret < 0)
                {
                    failure = "flush: " + FFmpegLoader.ErrorToString(ret);
                    return false;
                }
                if (!Drain(ctx, pkt, out failure))
                    return false;

                failure = null;
                return true;
            }
            finally
            {
                frame?.Dispose();
                hardwareFrame?.Dispose();
                if (pkt != null)
                    ffmpeg.av_packet_free(&pkt);
                ffmpeg.avcodec_free_context(&ctx); // releases the encoder's frame references too
            }
        }

        private static bool Drain(AVCodecContext* ctx, AVPacket* pkt, out string failure)
        {
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_packet(ctx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    failure = null;
                    return true;
                }
                if (ret < 0)
                {
                    failure = "receive_packet: " + FFmpegLoader.ErrorToString(ret);
                    return false;
                }
                ffmpeg.av_packet_unref(pkt);
            }
        }

        /// <summary>
        /// Allocates, configures and opens an encoder context for <paramref name="settings"/>:
        /// NV12 input (the one format every encoder here takes from system memory) — or, with
        /// <paramref name="hardwareFrames"/>, GPU frames of that layout over the frames context
        /// (<c>pix_fmt</c> D3D11, <c>sw_pix_fmt</c> NV12, <c>hw_frames_ctx</c> set) — the given
        /// time base and (when <c>framerate.num > 0</c>) rate hint, the generic rate-control
        /// fields, then every private option, then <c>avcodec_open2</c>. Null with a reason on
        /// any failure — a codec missing from the build, an option the encoder does not know,
        /// or the open itself — and nothing leaks.
        /// </summary>
        internal static AVCodecContext* TryOpenContext(H264EncoderSettings settings, int width, int height,
            AVRational timeBase, AVRational framerate, bool globalHeader, out string failure)
            => TryOpenContext(settings, width, height, timeBase, framerate, globalHeader, null, out failure);

        internal static AVCodecContext* TryOpenContext(H264EncoderSettings settings, int width, int height,
            AVRational timeBase, AVRational framerate, bool globalHeader, HardwareFrames hardwareFrames, out string failure)
        {
            ArgumentNullException.ThrowIfNull(settings);
            FFmpegLoader.EnsureInitialized();

            var codec = ffmpeg.avcodec_find_encoder_by_name(settings.CodecName);
            if (codec == null)
            {
                failure = "not present in this FFmpeg build";
                return null;
            }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null)
            {
                failure = "could not allocate the encoder context";
                return null;
            }

            ctx->width = width;
            ctx->height = height;
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            if (hardwareFrames != null)
            {
                if (hardwareFrames.Width != width || hardwareFrames.Height != height)
                {
                    failure = $"hardware frames are {hardwareFrames.Width}x{hardwareFrames.Height}, the encoder {width}x{height}";
                    ffmpeg.avcodec_free_context(&ctx);
                    return null;
                }
                ctx->pix_fmt = hardwareFrames.PixelFormat;
                ctx->sw_pix_fmt = hardwareFrames.SoftwareFormat;
                ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(hardwareFrames.FramesContext);
                if (ctx->hw_frames_ctx == null)
                {
                    failure = "could not reference the hardware frames context";
                    ffmpeg.avcodec_free_context(&ctx);
                    return null;
                }
            }
            ctx->time_base = timeBase;
            if (framerate.num > 0 && framerate.den > 0)
                ctx->framerate = framerate;
            ctx->gop_size = settings.GopSize;
            ctx->max_b_frames = settings.MaxBFrames;
            ctx->bit_rate = settings.BitRate;
            ctx->rc_max_rate = settings.MaxRate;
            if (settings.ConstantQuality)
            {
                ctx->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                ctx->global_quality = settings.GlobalQuality;
            }
            if (globalHeader)
                ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            foreach (var option in settings.PrivateOptions)
            {
                int ret = ffmpeg.av_opt_set(ctx->priv_data, option.Key, option.Value, 0);
                if (ret < 0)
                {
                    failure = $"option {option.Key}={option.Value}: {FFmpegLoader.ErrorToString(ret)}";
                    ffmpeg.avcodec_free_context(&ctx);
                    return null;
                }
            }

            int open = ffmpeg.avcodec_open2(ctx, codec, null);
            if (open < 0)
            {
                failure = "open: " + FFmpegLoader.ErrorToString(open);
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            failure = null;
            return ctx;
        }
    }
}
