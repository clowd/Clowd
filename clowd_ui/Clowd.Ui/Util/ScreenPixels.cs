using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.UI;

namespace Clowd.Util
{
    /// <summary>
    /// Reads the screen as it is right now, anywhere on the virtual desktop and outside any Clowd
    /// window, with no overlay and no process of its own. Two reads, each on the cheapest path for
    /// its caller: <see cref="GetColorAt"/>, one pixel, for the color pickers' eyedropper (called on
    /// every mouse move of a drag); and <see cref="Grab"/>, a whole monitor, for the frozen backdrop
    /// behind Draw on Instant Screenshot (taken before anything of ours is on screen). Not the
    /// capturer's job: that is an interactive overlay.
    /// </summary>
    /// <remarks>
    /// Windows reads the screen DC with GDI: <c>GetPixel</c> for a sample, <c>BitBlt</c> into a DIB
    /// for a grab (windows marked WDA_EXCLUDEFROMCAPTURE stay out of it, as for every capture API).
    /// macOS copies the rect out of a <c>CGWindowListCreateImage</c> either way, which needs the same
    /// Screen Recording permission the full capturer requires. Linux has no equivalent that works
    /// without a compositor-specific portal, so <see cref="IsSupported"/> is false there and callers
    /// hide what they would have offered. The cursor is never in the picture.
    ///
    /// The two reasons a read cannot happen are kept apart, because they call for different UI:
    /// <see cref="IsSupported"/> is a platform verdict that will never change, while
    /// <see cref="IsPermitted"/> is something the user can fix in System Settings. Missing
    /// permission is worse than useless rather than merely unavailable — an unpermitted
    /// <c>CGWindowListCreateImage</c> succeeds and hands back the desktop picture instead of the
    /// windows in front of it, so it would silently read the wrong pixels. Callers must gate on
    /// <see cref="IsAvailable"/>, not on <see cref="IsSupported"/> alone.
    /// </remarks>
    internal static class ScreenPixels
    {
        /// <summary>Whether this platform can read the screen at all. False on Linux; no user
        /// action can change it.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>Whether the OS currently permits the read — macOS Screen Recording. Always true
        /// where no such permission exists.</summary>
        public static bool IsPermitted => MacPermissions.HasScreenRecording;

        /// <summary>Whether a read taken right now would be trustworthy.</summary>
        public static bool IsAvailable => IsSupported && IsPermitted;

        /// <summary>
        /// Samples the pixel at <paramref name="point"/> (screen coordinates, as produced by
        /// <see cref="TopLevel.PointToScreen"/>). Returns null when unsupported, when macOS Screen
        /// Recording permission has not been granted, or when the point is on no display.
        /// </summary>
        public static Color? GetColorAt(PixelPoint point)
        {
            if (!IsPermitted)
                return null;

            if (OperatingSystem.IsWindows())
                return Win32.GetColorAt(point);

            if (OperatingSystem.IsMacOS())
                return MacOS.GetColorAt(point);

            return null;
        }

        /// <summary>
        /// The pixels inside <paramref name="bounds"/> (capture space: physical virtual-desktop
        /// pixels on Windows, CG points on macOS), as an opaque bitmap whose DPI makes its DIP size
        /// the monitor's. <paramref name="scaling"/> is the monitor's on Windows; macOS works it out
        /// from the image. Null when unsupported, unpermitted, or the grab fails.
        /// </summary>
        public static WriteableBitmap Grab(PixelRect bounds, double scaling)
        {
            if (!IsPermitted || bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            if (OperatingSystem.IsWindows())
                return Win32.Grab(bounds, scaling);

            if (OperatingSystem.IsMacOS())
                return MacOS.Grab(bounds);

            return null;
        }

        /// <summary>Copies <paramref name="height"/> rows of 32bpp BGRA into a new opaque bitmap.</summary>
        private static WriteableBitmap Wrap(IntPtr source, int stride, int width, int height, double dpiScale)
        {
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * dpiScale, 96 * dpiScale),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);

            var row = new byte[width * 4];
            using (var buffer = bitmap.Lock())
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(source, y * stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(buffer.Address, y * buffer.RowBytes), row.Length);
                }
            }

            return bitmap;
        }

        private static class Win32
        {
            public static Color? GetColorAt(PixelPoint point)
            {
                var hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                    return null;

                try
                {
                    var value = GetPixel(hdc, point.X, point.Y);

                    // GetPixel answers CLR_INVALID for coordinates outside the DC
                    if (value == CLR_INVALID)
                        return null;

                    // COLORREF is 0x00BBGGRR
                    return Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }

            public static WriteableBitmap Grab(PixelRect bounds, double scaling)
            {
                var screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero)
                    return null;

                var memory = IntPtr.Zero;
                var dib = IntPtr.Zero;
                var previous = IntPtr.Zero;
                try
                {
                    memory = CreateCompatibleDC(screen);
                    if (memory == IntPtr.Zero)
                        return null;

                    // top-down (negative height), so row 0 is the top of the screen
                    var info = new BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = bounds.Width,
                        biHeight = -bounds.Height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    };
                    dib = CreateDIBSection(screen, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                        return null;

                    previous = SelectObject(memory, dib);
                    if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT))
                        return null;
                    GdiFlush();

                    // GDI leaves the fourth byte undefined; the bitmap is opaque, so it is never read
                    return Wrap(bits, bounds.Width * 4, bounds.Width, bounds.Height, scaling);
                }
                finally
                {
                    if (previous != IntPtr.Zero)
                        SelectObject(memory, previous);
                    if (dib != IntPtr.Zero)
                        DeleteObject(dib);
                    if (memory != IntPtr.Zero)
                        DeleteDC(memory);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }

            private const uint CLR_INVALID = 0xFFFFFFFF;
            private const int BI_RGB = 0;
            private const uint DIB_RGB_COLORS = 0;
            private const uint SRCCOPY = 0x00CC0020;
            private const uint CAPTUREBLT = 0x40000000;

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public int biSize;
                public int biWidth;
                public int biHeight;
                public short biPlanes;
                public short biBitCount;
                public int biCompression;
                public int biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public int biClrUsed;
                public int biClrImportant;
            }

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll")]
            private static extern uint GetPixel(IntPtr hDC, int x, int y);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteDC(IntPtr hdc);

            // the header alone stands in for BITMAPINFO: a 32bpp BI_RGB bitmap has no colour table
            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

            [DllImport("gdi32.dll")]
            private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr ho);

            [DllImport("gdi32.dll")]
            private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

            [DllImport("gdi32.dll")]
            private static extern bool GdiFlush();
        }

        private static class MacOS
        {
            public static Color? GetColorAt(PixelPoint point)
            {
                // A 1x1 request still yields a 2x2 image on a Retina display; the first pixel is
                // the one under the cursor either way.
                return Read(point.X, point.Y, 1, 1, image =>
                {
                    var bytesPerPixel = image.BitsPerPixel / 8;
                    if (bytesPerPixel is not 3 and not 4 || image.Length < bytesPerPixel)
                        return (Color?)null;

                    var bytes = new byte[bytesPerPixel];
                    Marshal.Copy(image.Data, bytes, 0, bytesPerPixel);

                    if (bytesPerPixel == 4)
                        return image.IsLittleEndian
                            ? Color.FromRgb(bytes[2], bytes[1], bytes[0])  // B G R A
                            : Color.FromRgb(bytes[1], bytes[2], bytes[3]); // A R G B

                    return Color.FromRgb(bytes[0], bytes[1], bytes[2]);
                });
            }

            // TODO(mac): unverified, written blind like the rest of Draw on Screen's macOS paths
            public static WriteableBitmap Grab(PixelRect bounds)
            {
                return Read(bounds.X, bounds.Y, bounds.Width, bounds.Height, image =>
                {
                    // Wrap takes BGRA; anything else is refused rather than shown with its
                    // channels crossed
                    if (image.BitsPerPixel != 32 || !image.IsLittleEndian
                        || image.Length < (long)image.Stride * image.Height)
                        return null;

                    // pixels per point: 2 on a Retina display
                    return Wrap(image.Data, image.Stride, image.Width, image.Height, (double)image.Width / bounds.Width);
                });
            }

            /// <summary>One CoreGraphics image's pixels, valid only inside <see cref="Read{T}"/>'s callback.</summary>
            private readonly record struct ImageData(IntPtr Data, long Length, int Width, int Height, int Stride,
                int BitsPerPixel, bool IsLittleEndian);

            /// <summary>
            /// Copies the screen inside the rect (CoreGraphics global display space: top-left origin in
            /// points, exactly what Avalonia's macOS screen coordinates and DesktopScreen's bounds are)
            /// and hands its pixels to <paramref name="read"/>. Default when the copy fails or is empty.
            /// Screen captures are 32bpp little-endian (BGRA in memory), but the bitmap info is passed
            /// on rather than assumed.
            /// </summary>
            private static T Read<T>(double x, double y, double width, double height, Func<ImageData, T> read)
            {
                var image = CGWindowListCreateImage(new CGRect(x, y, width, height),
                                                    kCGWindowListOptionOnScreenOnly, kCGNullWindowID, kCGWindowImageDefault);
                if (image == IntPtr.Zero)
                    return default;

                var data = IntPtr.Zero;
                try
                {
                    var w = (int)CGImageGetWidth(image);
                    var h = (int)CGImageGetHeight(image);
                    if (w == 0 || h == 0)
                        return default;

                    var provider = CGImageGetDataProvider(image);
                    if (provider == IntPtr.Zero)
                        return default;

                    data = CGDataProviderCopyData(provider);
                    if (data == IntPtr.Zero)
                        return default;

                    var littleEndian = (CGImageGetBitmapInfo(image) & kCGBitmapByteOrderMask) == kCGBitmapByteOrder32Little;
                    return read(new ImageData(CFDataGetBytePtr(data), CFDataGetLength(data).ToInt64(), w, h,
                        (int)CGImageGetBytesPerRow(image), (int)CGImageGetBitsPerPixel(image), littleEndian));
                }
                finally
                {
                    if (data != IntPtr.Zero)
                        CFRelease(data);
                    CFRelease(image);
                }
            }

            private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            private const uint kCGWindowListOptionOnScreenOnly = 1;
            private const uint kCGNullWindowID = 0;
            private const uint kCGWindowImageDefault = 0;
            private const uint kCGBitmapByteOrderMask = 0x7000;
            private const uint kCGBitmapByteOrder32Little = 0x2000;

            [StructLayout(LayoutKind.Sequential)]
            private readonly struct CGRect
            {
                private readonly double _x;
                private readonly double _y;
                private readonly double _width;
                private readonly double _height;

                public CGRect(double x, double y, double width, double height)
                {
                    _x = x;
                    _y = y;
                    _width = width;
                    _height = height;
                }
            }

            [DllImport(CoreGraphics)]
            private static extern IntPtr CGWindowListCreateImage(CGRect screenBounds, uint listOption, uint windowId, uint imageOption);

            [DllImport(CoreGraphics)]
            private static extern IntPtr CGImageGetDataProvider(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern nuint CGImageGetWidth(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern nuint CGImageGetHeight(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern nuint CGImageGetBytesPerRow(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern nuint CGImageGetBitsPerPixel(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern uint CGImageGetBitmapInfo(IntPtr image);

            [DllImport(CoreGraphics)]
            private static extern IntPtr CGDataProviderCopyData(IntPtr provider);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFDataGetBytePtr(IntPtr data);

            [DllImport(CoreFoundation)]
            private static extern IntPtr CFDataGetLength(IntPtr data);

            [DllImport(CoreFoundation)]
            private static extern void CFRelease(IntPtr cf);
        }
    }
}
