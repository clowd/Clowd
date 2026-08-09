using System;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// A locked destination surface for one video frame. The decode thread writes BGRA pixels
    /// directly into <see cref="Address"/> (sws_scale output) — no intermediate managed buffer.
    /// </summary>
    public readonly struct FrameTarget
    {
        public FrameTarget(IntPtr address, int rowBytes, int width, int height, object token)
        {
            Address = address;
            RowBytes = rowBytes;
            Width = width;
            Height = height;
            Token = token;
        }

        public IntPtr Address { get; }
        public int RowBytes { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Opaque sink-owned state (e.g. the locked framebuffer) passed back to
        /// <see cref="IFrameSink.CompleteFrame"/>.</summary>
        public object Token { get; }
    }

    /// <summary>
    /// Implemented UI-side (a triple-buffered WriteableBitmap pool). <see cref="BeginFrame"/> and
    /// <see cref="CompleteFrame"/> are called on the engine's present thread — BeginFrame may block
    /// briefly for a free buffer (natural backpressure when the UI thread stalls); CompleteFrame
    /// posts the image swap to the UI thread and returns immediately.
    /// </summary>
    public interface IFrameSink
    {
        FrameTarget BeginFrame(int width, int height);
        void CompleteFrame(in FrameTarget target, TimeSpan pts);
    }
}
