using System;

namespace Clowd.VideoSDK.Playback
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
        /// <summary>
        /// Hands back the buffer the present thread sws_scales into.
        ///
        /// <para>
        /// The returned <see cref="FrameTarget.RowBytes"/> MUST be at least
        /// <c>FrameBufferPool.BgraRowBytes(width)</c> — swscale's unscaled yuv-to-BGRA converter
        /// writes whole 16-pixel blocks against the stride it is handed, and at a tighter one it
        /// either drops the row's last columns or writes past its end. Sinks that wrap a
        /// foreign buffer whose stride they do not control (a locked bitmap, say) must scale
        /// through their own padded staging rather than hand that stride over.
        /// </para>
        /// </summary>
        FrameTarget BeginFrame(int width, int height);
        void CompleteFrame(in FrameTarget target, TimeSpan pts);
    }
}
