using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.VideoSDK.Media
{
    /// <summary>
    /// BGRA → NV12 conversion (with scaling when the sizes differ) on a slice-threaded swscale
    /// context, the render pipeline's convert stage. Threading only works through
    /// <c>sws_scale_frame</c> — legacy <c>sws_scale</c> ignores the context's <c>threads</c>
    /// option — and <c>sws_scale_frame</c> deep-copies a source frame that is not reference
    /// counted, so the caller's BGRA memory is wrapped in a read-only <c>AVBufferRef</c> with a
    /// no-op free for the duration of the call. Measured on the 14700K for 1240x1166: 1.9 ms
    /// single-threaded, 0.43 ms with 8 threads (7.8 → 1.6 ms at 2480x2332). Output is
    /// byte-identical to legacy <c>sws_scale</c> with the same flags — every destination row is
    /// computed from its own source rows, so the slice split cannot change a value.
    ///
    /// <para>
    /// The conversion keeps swscale's default matrix (BT.601, limited range) and the streams
    /// stay untagged, exactly as the writer always did: the decoder converts sources to BGRA with
    /// the same default, so the round trip reproduces the source's YUV values whatever matrix it
    /// was tagged with, and players keep applying the source's convention. Moving the pipeline to
    /// BT.709 needs the decode side to convert with the source's matrix first; that is a
    /// follow-up spanning both ends, not a per-encoder setting.
    /// </para>
    /// Not thread-safe: one converter per stage.
    /// </summary>
    public sealed unsafe class Nv12Converter : IDisposable
    {
        /// <summary>Half the logical processors, 1..8: the swscale slices share the machine with
        /// x264's own thread pool, and past 8 the measured gain is nil.</summary>
        public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        private SwsContext* _sws;
        private AVFrame* _src; // reusable wrapper over the caller's memory (no planes of its own)

        /// <summary>Builds the scaler for <paramref name="srcWidth"/> x <paramref name="srcHeight"/>
        /// BGRA input to <paramref name="dstWidth"/> x <paramref name="dstHeight"/> NV12 output
        /// (bilinear when scaling, the same flag the writer always used).</summary>
        /// <param name="threads">Slice threads; 0 selects <see cref="DefaultThreads"/>.</param>
        public Nv12Converter(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int threads = 0)
        {
            FFmpegLoader.EnsureInitialized();
            if (srcWidth <= 0 || srcHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(srcWidth), $"Source size {srcWidth}x{srcHeight} is not positive.");
            if (dstWidth <= 0 || dstHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(dstWidth), $"Destination size {dstWidth}x{dstHeight} is not positive.");
            if ((dstWidth & 1) != 0 || (dstHeight & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(dstWidth), $"Destination size {dstWidth}x{dstHeight} must be even (4:2:0 chroma).");
            ArgumentOutOfRangeException.ThrowIfNegative(threads);
            if (threads == 0)
                threads = DefaultThreads;

            SourceWidth = srcWidth;
            SourceHeight = srcHeight;
            Width = dstWidth;
            Height = dstHeight;
            Threads = threads;

            try
            {
                _sws = ffmpeg.sws_alloc_context();
                if (_sws == null)
                    throw new InvalidOperationException("Could not allocate the BGRA->NV12 scaler.");
                Check(ffmpeg.av_opt_set_int(_sws, "srcw", srcWidth, 0), "srcw");
                Check(ffmpeg.av_opt_set_int(_sws, "srch", srcHeight, 0), "srch");
                Check(ffmpeg.av_opt_set_int(_sws, "src_format", (int)AVPixelFormat.AV_PIX_FMT_BGRA, 0), "src_format");
                Check(ffmpeg.av_opt_set_int(_sws, "dstw", dstWidth, 0), "dstw");
                Check(ffmpeg.av_opt_set_int(_sws, "dsth", dstHeight, 0), "dsth");
                Check(ffmpeg.av_opt_set_int(_sws, "dst_format", (int)AVPixelFormat.AV_PIX_FMT_NV12, 0), "dst_format");
                Check(ffmpeg.av_opt_set_int(_sws, "sws_flags", ffmpeg.SWS_BILINEAR, 0), "sws_flags");
                Check(ffmpeg.av_opt_set_int(_sws, "threads", threads, 0), "threads");
                Check(ffmpeg.sws_init_context(_sws, null, null), "sws_init_context");

                _src = ffmpeg.av_frame_alloc();
                if (_src == null)
                    throw new InvalidOperationException("Could not allocate the scaler's source frame.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int SourceWidth { get; }

        public int SourceHeight { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Slice threads in use.</summary>
        public int Threads { get; }

        /// <summary>
        /// Converts one BGRA frame (<see cref="SourceWidth"/> x <see cref="SourceHeight"/>, rows
        /// of <paramref name="rowBytes"/> bytes) into <paramref name="dst"/>, which must be
        /// <see cref="Width"/> x <see cref="Height"/>. The source memory is only read, and only
        /// for the duration of the call.
        /// </summary>
        public void Convert(IntPtr bgra, int rowBytes, Nv12Frame dst)
        {
            ObjectDisposedException.ThrowIf(_sws == null, this);
            ArgumentNullException.ThrowIfNull(dst);
            if (bgra == IntPtr.Zero)
                throw new ArgumentNullException(nameof(bgra));
            if (rowBytes < SourceWidth * 4)
                throw new ArgumentOutOfRangeException(nameof(rowBytes), $"rowBytes {rowBytes} is smaller than {SourceWidth}*4.");
            if (dst.Width != Width || dst.Height != Height)
                throw new ArgumentException($"Destination frame is {dst.Width}x{dst.Height}, converter produces {Width}x{Height}.", nameof(dst));
            if (dst.Frame == null)
                throw new ObjectDisposedException(nameof(Nv12Frame));

            // If an encoder still held a reference to the previous contents this would swap in
            // fresh planes; every encoder here copies its input synchronously inside
            // avcodec_send_frame (libx264, nvenc.c, amfenc.c and videotoolboxenc.c all memcpy
            // system-memory frames), so in practice it is a refcount check.
            Check(ffmpeg.av_frame_make_writable(dst.Frame), "av_frame_make_writable");

            // The extent the rows actually occupy, not rowBytes * height: the last row carries
            // no pitch padding (a D3D12 readback footprint is RowPitch * (rows - 1) + rowSize,
            // and the mapped allocation ends there), so a wrapper that claimed the full pitch
            // would let any copying consumer read past the caller's memory.
            ulong size = (ulong)rowBytes * (ulong)(SourceHeight - 1) + (ulong)SourceWidth * 4;
            var wrapper = ffmpeg.av_buffer_create((byte*)bgra, size, NoopFree, null, ffmpeg.AV_BUFFER_FLAG_READONLY);
            if (wrapper == null)
                throw new InvalidOperationException("Could not wrap the source frame memory.");
            _src->buf[0] = wrapper;
            _src->data[0] = (byte*)bgra;
            _src->linesize[0] = rowBytes;
            _src->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            _src->width = SourceWidth;
            _src->height = SourceHeight;
            try
            {
                Check(ffmpeg.sws_scale_frame(_sws, dst.Frame, _src), "sws_scale_frame");
            }
            finally
            {
                ffmpeg.av_frame_unref(_src); // drops the wrapper; the caller's memory is untouched
            }
        }

        // The wrapper never owns the memory it points at.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void NoopFreeImpl(void* opaque, byte* data)
        {
        }

        private static readonly av_buffer_create_free_func NoopFree = new av_buffer_create_free_func
        {
            Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&NoopFreeImpl,
        };

        private static void Check(int error, string what)
        {
            if (error < 0)
                throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorToString(error)}");
        }

        public void Dispose()
        {
            if (_src != null)
            {
                var f = _src;
                ffmpeg.av_frame_free(&f);
                _src = null;
            }
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
        }
    }
}
