using System;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// Which H.264 encoder writes a render's video stream. The output is always H.264 in an mp4
    /// (the project owner's decision: no H.265, container stays mp4); the choice is only which
    /// implementation produces it — the software x264 that works everywhere, or a GPU encoder
    /// that takes the work off the CPU. Quality is the one CRF-style knob whichever runs:
    /// <see cref="H264EncoderSettings"/> maps it onto each encoder's own scale so a given crf
    /// lands on comparable bytes and quality.
    /// </summary>
    public enum VideoEncoder
    {
        /// <summary>Probe for a working hardware encoder — NVENC, then AMF, then VideoToolbox
        /// (macOS) — by actually opening each on a small context, and use the first that opens;
        /// x264 when none does (<see cref="H264EncoderProbe"/>). A hardware encoder that then
        /// fails at the real output size falls back to x264 too, with a diagnostic line.</summary>
        Auto,

        /// <summary>libx264, preset <c>fast</c>. Always available and deterministic: identical
        /// input produces identical bytes, which the byte-for-byte pipeline tests rely on.</summary>
        Software,

        /// <summary>NVIDIA NVENC (<c>h264_nvenc</c>).</summary>
        Nvenc,

        /// <summary>AMD AMF (<c>h264_amf</c>).</summary>
        Amf,

        /// <summary>Apple VideoToolbox (<c>h264_videotoolbox</c>); macOS only.</summary>
        VideoToolbox,
    }

    /// <summary>
    /// The wire spellings of <see cref="VideoEncoder"/> — the <c>"encoder"</c> sibling of the job
    /// file <see cref="Editing.ProjectFileWriter"/> writes and <c>Clowd.VideoRender</c> reads.
    /// Lower-case, like the file's other siblings (<c>output</c>, <c>crf</c>); parsing ignores
    /// case so a hand-edited file is not rejected for a capital, but nothing else is forgiven —
    /// an unknown value is a loader error, not a silent default.
    /// </summary>
    public static class VideoEncoderNames
    {
        public const string Auto = "auto";
        public const string Software = "software";
        public const string Nvenc = "nvenc";
        public const string Amf = "amf";
        public const string VideoToolbox = "videotoolbox";

        /// <summary>Every accepted spelling, for error messages.</summary>
        public const string All = Auto + "|" + Software + "|" + Nvenc + "|" + Amf + "|" + VideoToolbox;

        /// <summary>The job-file spelling of <paramref name="encoder"/>.</summary>
        public static string Of(VideoEncoder encoder) => encoder switch
        {
            VideoEncoder.Auto => Auto,
            VideoEncoder.Software => Software,
            VideoEncoder.Nvenc => Nvenc,
            VideoEncoder.Amf => Amf,
            VideoEncoder.VideoToolbox => VideoToolbox,
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder."),
        };

        /// <summary>Parses a job-file spelling; false for anything that is not one of
        /// <see cref="All"/> (including null and empty).</summary>
        public static bool TryParse(string name, out VideoEncoder encoder)
        {
            foreach (var candidate in (VideoEncoder[])Enum.GetValues(typeof(VideoEncoder)))
            {
                if (string.Equals(name, Of(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    encoder = candidate;
                    return true;
                }
            }

            encoder = VideoEncoder.Auto;
            return false;
        }
    }
}
