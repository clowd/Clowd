using System;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// Encoder input frames that live on the GPU: the FFmpeg hardware device and frames contexts
    /// (<c>AV_HWDEVICE_TYPE_D3D11VA</c>, <c>AV_PIX_FMT_D3D11</c> with an NV12 layout) over a
    /// Direct3D 11 device, plus the pool of NV12 textures the frames point at. This is what
    /// <see cref="Mp4WriterOptions.HardwareFrames"/> hands the writer so NVENC or AMF read the
    /// composed picture straight from video memory — the zero-copy encode path — instead of a
    /// system-memory <see cref="Nv12Frame"/>. Built only by the Direct3D 11 bridge in the
    /// composition layer, which also fills the textures; the writer just submits the frames.
    ///
    /// <para>Why NV12 rather than the BGRA the compositor draws: AMF's FFmpeg 7.1 encoder takes
    /// no RGB layout from a D3D11 frame at all, and NVENC given RGB converts it in the driver
    /// with a fixed BT.601-limited matrix and tags the stream so — a colour policy the
    /// system-memory path does not share (the pipeline converts with swscale's BT.601 default
    /// and leaves streams untagged). Converting to NV12 on the GPU under our own matrix keeps
    /// every encoder, and the zero-copy and readback paths, on one policy, and NV12 is the
    /// hardware encoders' native layout besides (measured 5% faster than BGRA on NVENC).</para>
    /// </summary>
    public sealed unsafe class HardwareFrames : IDisposable
    {
        // D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE — what the pool's textures
        // carry; recorded on the frames context for consumers that read it (nothing here
        // allocates through the context's own pool).
        private const uint PoolBindFlags = 0x20 | 0x8;

        private AVBufferRef* _device; // AVHWDeviceContext (D3D11VA)
        private AVBufferRef* _frames; // AVHWFramesContext (D3D11 / NV12)
        private EncoderTexturePool _pool;
        private bool _disposed;

        /// <summary>Builds the contexts over <paramref name="d3d11Device"/> (an <c>ID3D11Device*</c>;
        /// one reference is taken and released with the device context) for
        /// <paramref name="width"/> x <paramref name="height"/> NV12 frames whose textures the
        /// two delegates create and destroy. Throws <see cref="InvalidOperationException"/> when
        /// FFmpeg refuses the device or the frame format.</summary>
        internal HardwareFrames(IntPtr d3d11Device, int width, int height,
            Func<IntPtr> allocateTexture, Action<IntPtr> releaseTexture, string description)
        {
            FFmpegLoader.EnsureInitialized();
            if (d3d11Device == IntPtr.Zero)
                throw new ArgumentNullException(nameof(d3d11Device));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Frame size {width}x{height} is not positive.");
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Frame size {width}x{height} must be even (4:2:0 chroma).");
            ArgumentNullException.ThrowIfNull(allocateTexture);
            ArgumentNullException.ThrowIfNull(releaseTexture);

            Width = width;
            Height = height;
            Description = description ?? "Direct3D 11 NV12 frames";
            try
            {
                _device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (_device == null)
                    throw new InvalidOperationException("Could not allocate the D3D11VA device context (not in this FFmpeg build?).");
                var hwctx = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)_device->data)->hwctx;
                // The device context releases the device once when it is freed (on every path,
                // including a failed init), so it gets a reference of its own.
                Composition.D3D12Backend.AddRef(d3d11Device);
                hwctx->device = (ID3D11Device*)d3d11Device;
                Check(ffmpeg.av_hwdevice_ctx_init(_device), "av_hwdevice_ctx_init(D3D11VA)");

                _frames = ffmpeg.av_hwframe_ctx_alloc(_device);
                if (_frames == null)
                    throw new InvalidOperationException("Could not allocate the D3D11 frames context.");
                var fc = (AVHWFramesContext*)_frames->data;
                fc->format = AVPixelFormat.AV_PIX_FMT_D3D11;
                fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                fc->width = width;
                fc->height = height;
                fc->initial_pool_size = 0; // the frames come from our pool, never from av_hwframe_get_buffer
                ((AVD3D11VAFramesContext*)fc->hwctx)->BindFlags = PoolBindFlags;
                Check(ffmpeg.av_hwframe_ctx_init(_frames), "av_hwframe_ctx_init(D3D11/NV12)");

                _pool = new EncoderTexturePool(allocateTexture, releaseTexture);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Human-readable summary for the diagnostic line that names the encode path.</summary>
        public string Description { get; }

        /// <summary>Textures the pool has allocated so far.</summary>
        public int PoolTextures => _pool?.Count ?? 0;

        /// <summary>Textures currently lent to frames the encoder (or a caller) still holds.</summary>
        public int PoolInUse => _pool?.InUse ?? 0;

        /// <summary>The most textures ever lent out at once — the encoder's in-flight depth.</summary>
        public int PoolPeakInUse => _pool?.PeakInUse ?? 0;

        /// <summary>The <c>AVHWFramesContext</c> reference an encoder context is opened with
        /// (<c>hw_frames_ctx</c>); null once disposed.</summary>
        internal AVBufferRef* FramesContext => _frames;

        /// <summary>The frames' <c>AVCodecContext.pix_fmt</c>.</summary>
        internal AVPixelFormat PixelFormat => AVPixelFormat.AV_PIX_FMT_D3D11;

        /// <summary>The frames' data layout (<c>sw_format</c> / <c>sw_pix_fmt</c>).</summary>
        internal AVPixelFormat SoftwareFormat => AVPixelFormat.AV_PIX_FMT_NV12;

        /// <summary>A free pool texture for the caller to fill (allocating one when none is).
        /// Follow with <see cref="Wrap"/>, or <see cref="ReturnTexture"/> if it goes unused.</summary>
        internal IntPtr RentTexture()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pool.Rent();
        }

        /// <summary>Gives back a rented texture that was not wrapped.</summary>
        internal void ReturnTexture(IntPtr texture)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pool.Return(texture);
        }

        /// <summary>
        /// The <see cref="HardwareFrame"/> for a rented texture: an <c>AVFrame</c> with
        /// <c>data[0]</c> = the texture, <c>data[1]</c> = array slice 0, a
        /// <see cref="EncoderTexturePool"/> reference in <c>buf[0]</c> and this frames context in
        /// <c>hw_frames_ctx</c>. The texture returns to the pool when the last reference goes —
        /// the caller's, dropped by <see cref="HardwareFrame.Dispose"/>, and the encoder's, once
        /// it has finished reading the picture.
        /// </summary>
        internal HardwareFrame Wrap(IntPtr texture)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                _pool.Return(texture);
                throw new InvalidOperationException("Could not allocate the hardware frame.");
            }

            try
            {
                frame->buf[0] = _pool.CreateReference(texture); // ownership of the rent moves to the reference
                frame->data[0] = (byte*)texture;
                frame->data[1] = null; // array slice 0
                frame->format = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
                frame->width = Width;
                frame->height = Height;
                frame->hw_frames_ctx = ffmpeg.av_buffer_ref(_frames);
                if (frame->hw_frames_ctx == null)
                    throw new InvalidOperationException("Could not reference the frames context.");
                return new HardwareFrame(frame, texture, Width, Height);
            }
            catch
            {
                if (frame->buf[0] == null)
                    _pool.Return(texture);
                ffmpeg.av_frame_free(&frame); // drops the reference (returning the texture) when one was made
                throw;
            }
        }

        private static void Check(int error, string what)
        {
            if (error < 0)
                throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorToString(error)}");
        }

        /// <summary>Releases the contexts and destroys the pool's textures. Every
        /// <see cref="HardwareFrame"/> and the encoder context opened over these frames must be
        /// gone first (<see cref="PoolInUse"/> is 0 then).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _pool?.Dispose();
            _pool = null;
            if (_frames != null)
            {
                var f = _frames;
                ffmpeg.av_buffer_unref(&f);
                _frames = null;
            }
            if (_device != null)
            {
                var d = _device;
                ffmpeg.av_buffer_unref(&d);
                _device = null;
            }
        }
    }

    /// <summary>One GPU-resident frame from <see cref="HardwareFrames"/>, ready for
    /// <see cref="Mp4Writer.SubmitVideoFrame(HardwareFrame, long)"/>. Dispose it after
    /// submitting: the encoder keeps its own reference for as long as it reads the texture, and
    /// the texture returns to the pool when the last reference is dropped.</summary>
    public sealed unsafe class HardwareFrame : IDisposable
    {
        private static int _live;
        private AVFrame* _frame;

        internal HardwareFrame(AVFrame* frame, IntPtr texture, int width, int height)
        {
            _frame = frame;
            Texture = texture;
            Width = width;
            Height = height;
            System.Threading.Interlocked.Increment(ref _live);
        }

        /// <summary>Frames created and not yet disposed, process-wide — there is no finalizer,
        /// so one that is dropped keeps its texture out of the pool and the frames and device
        /// contexts alive for good; tests check this returns to where it started.</summary>
        public static int LiveCount => System.Threading.Volatile.Read(ref _live);

        public int Width { get; }

        public int Height { get; }

        /// <summary>The <c>ID3D11Texture2D*</c> the frame reads (for the self-check and tests).</summary>
        internal IntPtr Texture { get; }

        /// <summary>The frame itself; null once disposed.</summary>
        internal AVFrame* Frame => _frame;

        public void Dispose()
        {
            if (_frame == null)
                return;
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
            System.Threading.Interlocked.Decrement(ref _live);
        }
    }
}
