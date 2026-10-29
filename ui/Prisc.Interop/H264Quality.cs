using System;

namespace Prisc.Interop
{
    /// <summary>
    /// One x264-style crf (0 best to 51 worst) mapped onto every H.264 encoder's own scale
    /// (<c>prisc_encoding_h264_quality</c>, over <c>prisc_rust_core::encoding</c>, the mapping the
    /// recorder applies too): <see cref="Crf"/> for x264 and AMF, NVENC's constant-quality target,
    /// VideoToolbox's quality slider, and the average bitrate an Intel Mac's VideoToolbox encodes at
    /// instead.
    /// </summary>
    public readonly record struct H264Quality(int Crf, int NvencCq, int VideoToolboxQuality, int VideoToolboxBitrateKbps)
    {
        /// <summary>The mapping of <paramref name="crf"/> for a <paramref name="width"/> x
        /// <paramref name="height"/> output at <paramref name="fpsNum"/>/<paramref name="fpsDen"/>
        /// frames per second (the size and rate only matter to the bitrate). Throws
        /// <see cref="ArgumentOutOfRangeException"/> for a crf outside 0 to 51 or a size or rate
        /// component that is not positive. Any thread.</summary>
        public static unsafe H264Quality For(int crf, int width, int height, int fpsNum, int fpsDen)
        {
            InteropLibrary.Require();
            PriscH264Quality q;
            int status = NativeMethods.prisc_encoding_h264_quality(crf, width, height, fpsNum, fpsDen, &q);
            if (status == (int)InteropStatus.InvalidArg)
            {
                // the message is Rust's; the parameter named is one that is out of range (the
                // size or rate when either is, as H264EncoderSettings has always named them, and
                // otherwise the crf), without restating the crf range here
                var param = width <= 0 || height <= 0 ? nameof(width)
                    : fpsNum <= 0 || fpsDen <= 0 ? nameof(fpsNum)
                    : nameof(crf);
                throw new ArgumentOutOfRangeException(param, Native.LastErrorMessage());
            }
            Native.Check(status, "prisc_encoding_h264_quality");
            return new H264Quality(q.crf, q.nvenc_cq, q.vt_quality, q.vt_bitrate_kbps);
        }
    }
}
