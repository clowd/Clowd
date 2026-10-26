using System;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// One NV12 video frame in encoder-ready memory: the <c>AVFrame</c> the render pipeline's
    /// convert stage fills (<see cref="Nv12Converter"/>) and the encode stage submits
    /// (<see cref="Mp4Writer.SubmitVideoFrame(Nv12Frame, long)"/>). A small pool of these
    /// circulates between the two stages; the planes are allocated once and reused.
    ///
    /// <para>
    /// NV12 (a full-resolution Y plane and one interleaved half-resolution UV plane) is the one
    /// system-memory format every H.264 encoder here accepts — libx264, NVENC, AMF and
    /// VideoToolbox — so the conversion is a single path whichever encoder runs. It is the
    /// hardware encoders' native layout (measured on an RTX 4070: NVENC took nv12 5% faster than
    /// yuv420p at both 1x and 2x and produced the same bytes), swscale produces it in the same
    /// time as yuv420p with byte-identical luma and chroma values, and x264 — which stores
    /// chroma interleaved internally — emits byte-identical packets from either.
    /// </para>
    /// </summary>
    public sealed unsafe class Nv12Frame : IDisposable
    {
        private AVFrame* _frame;

        /// <summary>Allocates the planes for a <paramref name="width"/> x <paramref name="height"/>
        /// frame (both must be even — 4:2:0 chroma).</summary>
        public Nv12Frame(int width, int height)
        {
            FFmpegLoader.EnsureInitialized();
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Frame size {width}x{height} is not positive.");
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Frame size {width}x{height} must be even (4:2:0 chroma).");

            Width = width;
            Height = height;
            _frame = ffmpeg.av_frame_alloc();
            if (_frame == null)
                throw new InvalidOperationException("Could not allocate the video frame.");
            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _frame->width = width;
            _frame->height = height;
            int err = ffmpeg.av_frame_get_buffer(_frame, 0);
            if (err < 0)
            {
                Dispose();
                throw new InvalidOperationException(
                    "Could not allocate video frame buffers: " + FFmpegLoader.ErrorToString(err));
            }
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The underlying frame (Y in <c>data[0]</c>, interleaved UV in <c>data[1]</c>,
        /// strides in <c>linesize[0..1]</c>). Null once disposed.</summary>
        public AVFrame* Frame => _frame;

        /// <summary>Address and stride of plane <paramref name="plane"/> (0 = Y, 1 = interleaved
        /// UV, half the rows) — for tests and for callers that want to read the converted pixels.</summary>
        public (IntPtr Address, int Stride) Plane(int plane)
        {
            ObjectDisposedException.ThrowIf(_frame == null, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)plane, 2u, nameof(plane));
            return ((IntPtr)_frame->data[(uint)plane], _frame->linesize[(uint)plane]);
        }

        /// <summary>Fills the frame with limited-range black (Y 16, U/V 128): a valid picture
        /// for an encoder probe, rather than whatever the allocator left in the planes.</summary>
        public void FillBlack()
        {
            ObjectDisposedException.ThrowIf(_frame == null, this);
            int err = ffmpeg.av_frame_make_writable(_frame);
            if (err < 0)
                throw new InvalidOperationException("Could not make the frame writable: " + FFmpegLoader.ErrorToString(err));
            for (int y = 0; y < Height; y++)
                new Span<byte>(_frame->data[0] + (long)y * _frame->linesize[0], Width).Fill(16);
            for (int y = 0; y < Height / 2; y++)
                new Span<byte>(_frame->data[1] + (long)y * _frame->linesize[1], Width).Fill(128);
        }

        public void Dispose()
        {
            if (_frame == null)
                return;
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
    }
}
