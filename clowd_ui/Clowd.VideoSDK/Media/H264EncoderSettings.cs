using System;
using System.Collections.Generic;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// The FFmpeg configuration of one concrete H.264 encoder for one render: codec name, the
    /// private options to <c>av_opt_set</c> on its context, and the generic
    /// <c>AVCodecContext</c> fields that carry rate control. A pure value — building one touches
    /// no native code — so the crf mapping is unit-testable per encoder and the diagnostic line
    /// (<see cref="Description"/>) says exactly what the encoder was handed.
    ///
    /// <para>
    /// <c>crf</c> (0 best – 51 worst, x264's scale) is the single quality knob the caller sees;
    /// each encoder maps it onto its own scale so a given crf lands on comparable bytes and
    /// quality whichever encoder ends up selected. The mapping is the one measured for the
    /// recorder (obs-express <c>encoder_config.rs</c>, branch <c>caesay/multi-track-encoding</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><b>x264</b>: <c>crf</c> passthrough, preset <c>fast</c> (the project owner's choice,
    /// not configurable), profile high. crf 0 is x264's lossless mode, which
    /// <c>x264_param_apply_profile</c> refuses to combine with the High profile ("high profile
    /// doesn't support lossless" fails the open), so there the profile is left to x264, which
    /// picks High 4:4:4 Predictive — what the writer produced before it set a profile at all.</item>
    /// <item><b>NVENC</b>: constant-quality VBR (<c>rc=vbr cq=crf+4 b:v=0 maxrate=0</c>). NVENC's
    /// CQ scale sits about 4 points below x264's CRF scale, so +4 recentres bytes to match; the
    /// bitrate and its ceiling must be 0 or NVENC caps bursts (a page jump) instead of letting
    /// the target quality decide. Preset p6 with tune hq, quarter-resolution multipass, 8-frame
    /// lookahead and spatial AQ is NVIDIA's recording configuration; p7 produced byte-identical
    /// output to p6.</item>
    /// <item><b>AMF</b>: constant QP at <c>crf</c> for I, P and B (what OBS's own recording
    /// presets use), quality preset.</item>
    /// <item><b>VideoToolbox</b>: its inverted 0–100 quality slider on Apple Silicon
    /// (<see cref="VideoToolboxQuality"/>); Intel Macs have no quality mode, and the encoder
    /// refuses <c>q:v</c> there, so <see cref="BitrateFallback"/> carries the average-bitrate
    /// variant (<see cref="VideoToolboxBitrateKbps"/>) to open instead.</item>
    /// </list>
    /// Uniform across all of them: a 10-second GOP (<see cref="GopFrames"/>, see
    /// <see cref="GopSeconds"/> for the size measurements), 2 B-frames where the encoder supports them (AMF and
    /// VideoToolbox silently drop to what the hardware offers), profile high.
    /// </summary>
    public sealed class H264EncoderSettings
    {
        /// <summary>The x264 preset, fixed by the project owner (not configurable). Measured on
        /// the 3863-frame benchmark job (i7-14700K): 1.95 ms/frame at 1240x1166, 9.6 ms at
        /// 2480x2332 — as fast as NVENC p6 at 1x and faster at 2x on this CPU.</summary>
        public const string X264Preset = "fast";

        /// <summary>Keyframe interval in seconds, every encoder. The recorder keys every 2 s
        /// (obs-express <c>KEYINT_SEC</c>) for crash resilience, which a finished render does not
        /// need, and keyframes are the biggest lever on file size for screen content. Measured on
        /// the 3863-frame 60 fps benchmark job at crf 23 (x264 fast): 2 s = 4.88 MB, 4 s = 4.28 MB,
        /// 10 s = 3.89 MB, 20 s = 3.79 MB, 60 s = 3.75 MB. 10 s takes most of the saving and keeps
        /// a seek in a rendered file to at most 10 s of decode-forward; x264's scene-cut detection
        /// still inserts keyframes at hard cuts.</summary>
        public const int GopSeconds = 10;

        /// <summary>B-frames between references, where the encoder supports them.</summary>
        public const int BFrames = 2;

        /// <summary>The worst quality x264's crf and NVENC's cq accept.</summary>
        public const int MaxCrf = 51;

        /// <summary>x264's lossless crf. The hardware encoders have no lossless mode on this
        /// scale (NVENC's cq 0 means "automatic", AMF's qp 0 is merely its best step), so only
        /// the x264 mapping treats it specially.</summary>
        public const int LosslessCrf = 0;

        /// <summary>NVENC's CQ scale sits about this far below x264's CRF scale (measured:
        /// bytes at cq=crf+4 match x264 at crf).</summary>
        public const int NvencCqOffset = 4;

        /// <summary>VideoToolbox quality slider bounds. Past ~70 each point buys a fraction of a
        /// VMAF for a multiple of the bytes (q70 ≈ 17.5 Mbps, q80 ≈ 38 Mbps at 1080p30), and
        /// below 15 the output is unusable; crf 15 and better all land on 70.</summary>
        public const int VideoToolboxQualityMin = 15, VideoToolboxQualityMax = 70;

        /// <summary>Intel-Mac average-bitrate bounds (kbps): enough for a screen recording at the
        /// common sizes, and a hard ceiling so a 4K60 render cannot balloon.</summary>
        public const int VideoToolboxBitrateMinKbps = 1500, VideoToolboxBitrateMaxKbps = 6000;

        private H264EncoderSettings(VideoEncoder encoder, string codecName,
            IReadOnlyList<KeyValuePair<string, string>> privateOptions, int gopSize, int maxBFrames,
            long bitRate, long maxRate, bool constantQuality, int globalQuality, string description,
            H264EncoderSettings bitrateFallback)
        {
            Encoder = encoder;
            CodecName = codecName;
            PrivateOptions = privateOptions;
            GopSize = gopSize;
            MaxBFrames = maxBFrames;
            BitRate = bitRate;
            MaxRate = maxRate;
            ConstantQuality = constantQuality;
            GlobalQuality = globalQuality;
            Description = description;
            BitrateFallback = bitrateFallback;
        }

        /// <summary>The concrete encoder (never <see cref="VideoEncoder.Auto"/>).</summary>
        public VideoEncoder Encoder { get; }

        /// <summary>The FFmpeg encoder name (<c>avcodec_find_encoder_by_name</c>).</summary>
        public string CodecName { get; }

        /// <summary>Private options, applied in order with <c>av_opt_set</c> on the context's
        /// <c>priv_data</c>. Every name is one the bundled FFmpeg 7.1 declares for that encoder
        /// (verified against its option table); an unknown name fails the open.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> PrivateOptions { get; }

        /// <summary><c>AVCodecContext.gop_size</c>.</summary>
        public int GopSize { get; }

        /// <summary><c>AVCodecContext.max_b_frames</c>.</summary>
        public int MaxBFrames { get; }

        /// <summary><c>AVCodecContext.bit_rate</c> in bits/s. 0 for every quality-driven mode:
        /// the context's own default is 200 kb/s, which NVENC would otherwise take as its VBR
        /// average.</summary>
        public long BitRate { get; }

        /// <summary><c>AVCodecContext.rc_max_rate</c>; 0 = no ceiling.</summary>
        public long MaxRate { get; }

        /// <summary>Set <c>AV_CODEC_FLAG_QSCALE</c> and <see cref="GlobalQuality"/> — the API
        /// form of <c>-q:v</c>, which is how VideoToolbox takes its quality slider.</summary>
        public bool ConstantQuality { get; }

        /// <summary><c>AVCodecContext.global_quality</c>, in <c>FF_QP2LAMBDA</c> units (the
        /// encoder divides it back out).</summary>
        public int GlobalQuality { get; }

        /// <summary>Human-readable summary of the effective rate control and quality — the
        /// counterpart of obs-express's <c>describe_settings</c>, e.g.
        /// <c>NVENC vbr cq=25 preset=p6 …</c> — for the diagnostic line that names the encoder
        /// a render actually got.</summary>
        public string Description { get; }

        /// <summary>The configuration to try when this one fails to open: VideoToolbox's
        /// average-bitrate variant for Intel Macs, which reject the quality mode at open. Null
        /// for every other encoder.</summary>
        public H264EncoderSettings BitrateFallback { get; }

        /// <summary>The FFmpeg encoder name of a concrete <see cref="VideoEncoder"/>.</summary>
        public static string CodecNameOf(VideoEncoder encoder) => encoder switch
        {
            VideoEncoder.Software => "libx264",
            VideoEncoder.Nvenc => "h264_nvenc",
            VideoEncoder.Amf => "h264_amf",
            VideoEncoder.VideoToolbox => "h264_videotoolbox",
            VideoEncoder.Auto => throw new ArgumentException("Auto is not a concrete encoder; resolve it first.", nameof(encoder)),
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder."),
        };

        /// <summary>
        /// Builds the settings for <paramref name="encoder"/> at <paramref name="crf"/> for a
        /// <paramref name="width"/> x <paramref name="height"/> output at
        /// <paramref name="fpsNum"/>/<paramref name="fpsDen"/> frames per second (the size and
        /// rate only matter to the GOP length and to VideoToolbox's bitrate fallback).
        /// </summary>
        public static H264EncoderSettings For(VideoEncoder encoder, int crf, int width, int height, int fpsNum, int fpsDen)
        {
            if (crf < 0 || crf > MaxCrf)
                throw new ArgumentOutOfRangeException(nameof(crf), crf, $"crf must be 0-{MaxCrf}.");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Output size {width}x{height} is not positive.");
            if (fpsNum <= 0 || fpsDen <= 0)
                throw new ArgumentOutOfRangeException(nameof(fpsNum), $"Frame rate {fpsNum}/{fpsDen} components must be positive.");

            string codec = CodecNameOf(encoder);
            int gop = GopFrames(fpsNum, fpsDen);
            string common = $"profile=high bf={BFrames} gop={gop}";
            switch (encoder)
            {
                case VideoEncoder.Software:
                    if (crf == LosslessCrf)
                    {
                        // No profile option: x264 rejects High for a lossless encode and selects
                        // High 4:4:4 Predictive on its own (the only H.264 profile with lossless).
                        return new H264EncoderSettings(encoder, codec,
                            Options(("preset", X264Preset), ("crf", I(crf))),
                            gop, BFrames, 0, 0, false, 0,
                            $"x264 crf={crf} (lossless) preset={X264Preset} profile=high444 bf={BFrames} gop={gop}", null);
                    }
                    return new H264EncoderSettings(encoder, codec,
                        Options(("preset", X264Preset), ("crf", I(crf)), ("profile", "high")),
                        gop, BFrames, 0, 0, false, 0,
                        $"x264 crf={crf} preset={X264Preset} {common}", null);

                case VideoEncoder.Nvenc:
                {
                    int cq = NvencCq(crf);
                    return new H264EncoderSettings(encoder, codec,
                        Options(("rc", "vbr"), ("cq", I(cq)), ("preset", "p6"), ("tune", "hq"),
                            ("multipass", "qres"), ("rc-lookahead", "8"), ("spatial-aq", "1"), ("profile", "high")),
                        gop, BFrames, 0, 0, false, 0,
                        $"NVENC vbr cq={cq} preset=p6 tune=hq multipass=qres rc-lookahead=8 spatial-aq=1 {common}", null);
                }

                case VideoEncoder.Amf:
                    // AMF only applies its B-picture pattern (bf) when the consecutive-B cap
                    // (max_b_frames) is set as well (amfenc_h264.c), so both go in; hardware
                    // without B-frame support logs a warning and encodes without them.
                    return new H264EncoderSettings(encoder, codec,
                        Options(("rc", "cqp"), ("qp_i", I(crf)), ("qp_p", I(crf)), ("qp_b", I(crf)),
                            ("quality", "quality"), ("profile", "high"),
                            ("max_b_frames", I(BFrames)), ("bf", I(BFrames))),
                        gop, BFrames, 0, 0, false, 0,
                        $"AMF cqp qp={crf} quality=quality {common}", null);

                case VideoEncoder.VideoToolbox:
                {
                    // allow_sw=0 is the encoder default, stated so the intent survives an FFmpeg
                    // bump: a software VideoToolbox session would be slower than x264 fast.
                    var options = Options(("profile", "high"), ("allow_sw", "0"));
                    int kbps = VideoToolboxBitrateKbps(width, height, fpsNum, fpsDen);
                    var fallback = new H264EncoderSettings(encoder, codec, options,
                        gop, BFrames, kbps * 1000L, 0, false, 0,
                        $"VideoToolbox b:v={kbps}k (average bitrate: no quality mode on this Mac) {common}", null);
                    int q = VideoToolboxQuality(crf);
                    return new H264EncoderSettings(encoder, codec, options,
                        gop, BFrames, 0, 0, true, q * ffmpeg.FF_QP2LAMBDA,
                        $"VideoToolbox q:v={q} {common}", fallback);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder.");
            }
        }

        /// <summary>Frames per <see cref="GopSeconds"/> at the given rate, rounded, never below
        /// 1 (60 fps → 600; 30000/1001 → 300).</summary>
        public static int GopFrames(int fpsNum, int fpsDen)
        {
            if (fpsNum <= 0 || fpsDen <= 0)
                throw new ArgumentOutOfRangeException(nameof(fpsNum), $"Frame rate {fpsNum}/{fpsDen} components must be positive.");
            return Math.Max(1, (int)Math.Round(GopSeconds * (double)fpsNum / fpsDen));
        }

        /// <summary>NVENC's constant-quality target for an x264-style crf: <c>crf + 4</c>, clamped
        /// to 1–51 (0 would mean "automatic" to NVENC, not lossless).</summary>
        public static int NvencCq(int crf) => Math.Clamp(crf + NvencCqOffset, 1, MaxCrf);

        /// <summary>
        /// VideoToolbox's quality slider (0 worst – 100 best) for an x264-style crf:
        /// <c>(51 - crf) * 100 / 51</c> rounded, then clamped to
        /// <see cref="VideoToolboxQualityMin"/>–<see cref="VideoToolboxQualityMax"/>. The linear
        /// slope tracked x264 veryfast at the same crf to within ~0.6 VMAF across crf 16–24 on an
        /// M2 Pro; the clamp is because the slider has no sane top end.
        /// </summary>
        public static int VideoToolboxQuality(int crf)
        {
            int q = (int)Math.Round(Math.Max(0, MaxCrf - crf) * 100.0 / MaxCrf, MidpointRounding.AwayFromZero);
            return Math.Clamp(q, VideoToolboxQualityMin, VideoToolboxQualityMax);
        }

        /// <summary>
        /// Intel-Mac VideoToolbox has no quality mode, so it encodes at an average bitrate: a
        /// 4 Mbps-at-3440x1440@30 budget scaled by pixel rate and clamped to
        /// <see cref="VideoToolboxBitrateMinKbps"/>–<see cref="VideoToolboxBitrateMaxKbps"/>.
        /// A size compromise by design, not an equivalent of the crf tiers.
        /// </summary>
        public static int VideoToolboxBitrateKbps(int width, int height, int fpsNum, int fpsDen)
        {
            const double referencePixels = 3440.0 * 1440.0;
            double fps = fpsDen > 0 ? (double)fpsNum / fpsDen : 30.0;
            double pixelRate = (width * (double)height / referencePixels) * (Math.Max(1.0, fps) / 30.0);
            double kbps = Math.Round(4000.0 * pixelRate, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(kbps, VideoToolboxBitrateMinKbps, VideoToolboxBitrateMaxKbps);
        }

        private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static IReadOnlyList<KeyValuePair<string, string>> Options(params (string Name, string Value)[] options)
        {
            var list = new List<KeyValuePair<string, string>>(options.Length);
            foreach (var (name, value) in options)
                list.Add(new KeyValuePair<string, string>(name, value));
            return list;
        }
    }
}
