using System;

namespace Prisc.Interop
{
    /// <summary>
    /// Reads the screen as it is right now, through the grab every Prisc reader of screen pixels
    /// shares (<c>prisc_rust_core::desktop::grab</c>). Coordinates are <see cref="NativeScreens"/>'
    /// capture space. Any thread.
    /// </summary>
    /// <remarks>On macOS both reads refuse with <see cref="InteropStatus.Permission"/> when Screen
    /// Recording is missing (an unpermitted read would silently show the desktop picture instead
    /// of the windows). On a platform without a screen grab they throw with
    /// <see cref="InteropStatus.Unsupported"/>.</remarks>
    public static unsafe class ScreenCapture
    {
        /// <summary>The colour of the screen pixel at the point as <c>0x00RRGGBB</c>, or null when
        /// the point is on no screen. Throws <see cref="InteropException"/> otherwise.</summary>
        public static uint? ReadPixel(int x, int y)
        {
            InteropLibrary.Require();
            uint rgb;
            var status = Native.Check(NativeMethods.prisc_screen_read_pixel(x, y, &rgb), "prisc_screen_read_pixel");
            return status == InteropStatus.Ok ? rgb : null;
        }

        /// <summary>The screen inside the rectangle (layered windows included, opaque). Dispose the
        /// image to free its pixels. Throws <see cref="InteropException"/> on failure, including
        /// <see cref="InteropStatus.InvalidArg"/> for a rectangle without area.</summary>
        public static ScreenImage Grab(int x, int y, int width, int height)
        {
            InteropLibrary.Require();
            var rect = new PriscRect { x = x, y = y, width = width, height = height };
            PriscScreenImage image;
            Native.Check(NativeMethods.prisc_screen_grab(&rect, &image), "prisc_screen_grab");
            return new ScreenImage(image);
        }
    }

    /// <summary>A grabbed picture: BGRA8, top-down, opaque, its pixels owned by the native library
    /// until <see cref="Dispose"/>.</summary>
    public sealed unsafe class ScreenImage : IDisposable
    {
        private PriscScreenImage _image;

        internal ScreenImage(PriscScreenImage image)
        {
            _image = image;
        }

        ~ScreenImage() => Free();

        /// <summary>Width in pixels. On macOS the display's backing pixels, so a Retina grab is
        /// twice its rectangle in points.</summary>
        public int Width => (int)_image.width;

        /// <summary>Height in pixels.</summary>
        public int Height => (int)_image.height;

        /// <summary>Bytes per row (always <see cref="Width"/> * 4).</summary>
        public int Stride => (int)_image.stride;

        /// <summary>Image pixels per DIP: the backing scale on macOS, the monitor's effective DPI /
        /// 96 on Windows. A bitmap of <c>96 * DipScale</c> DPI has the rectangle's DIP size.</summary>
        public double DipScale => _image.dip_scale;

        /// <summary>The pixels, valid until <see cref="Dispose"/>.</summary>
        public ReadOnlySpan<byte> Pixels
        {
            get
            {
                ObjectDisposedException.ThrowIf(_image.pixels == null, this);
                return new ReadOnlySpan<byte>(_image.pixels, checked((int)_image.len));
            }
        }

        /// <summary>Copies the rows into a buffer whose rows are <paramref name="destinationStride"/>
        /// bytes apart (a locked bitmap's address and row bytes), which must hold <see cref="Height"/>
        /// rows of at least <see cref="Width"/> * 4 bytes.</summary>
        public void CopyTo(IntPtr destination, int destinationStride)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, Width * 4);
            if (destination == IntPtr.Zero)
                throw new ArgumentNullException(nameof(destination));

            var source = Pixels;
            int row = Width * 4;
            for (int y = 0; y < Height; y++)
            {
                var target = new Span<byte>((byte*)destination + (long)y * destinationStride, row);
                source.Slice(y * Stride, row).CopyTo(target);
            }
        }

        /// <summary>Frees the pixels; a second call does nothing.</summary>
        public void Dispose()
        {
            Free();
            GC.SuppressFinalize(this);
        }

        private void Free()
        {
            if (_image.pixels == null || !InteropLibrary.IsAvailable)
                return;
            fixed (PriscScreenImage* image = &_image)
            {
                NativeMethods.prisc_screen_image_free(image);
            }
        }
    }
}
