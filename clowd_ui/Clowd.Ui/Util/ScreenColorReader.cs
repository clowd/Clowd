using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Clowd.UI;

namespace Clowd.Util
{
    /// <summary>
    /// Reads the color of a single pixel anywhere on the virtual desktop, including outside any
    /// Clowd window — the sampling primitive behind the color pickers' eyedropper.
    /// </summary>
    /// <remarks>
    /// Windows samples the screen DC with GDI; macOS captures a 1x1 rect with CoreGraphics, which
    /// needs the same Screen Recording permission the full capturer already requires. Linux has no
    /// equivalent that works without a compositor-specific portal, so <see cref="IsSupported"/> is
    /// false there and callers hide the eyedropper rather than offering a control that cannot work.
    ///
    /// The two reasons an eyedropper cannot sample are kept apart, because they call for different
    /// UI: <see cref="IsSupported"/> is a platform verdict that will never change, while
    /// <see cref="IsPermitted"/> is something the user can fix in System Settings. Missing
    /// permission is worse than useless rather than merely unavailable — an unpermitted
    /// <c>CGWindowListCreateImage</c> succeeds and hands back the desktop picture instead of the
    /// window in front of it, so it would silently sample the wrong pixel. Callers must gate on
    /// <see cref="IsAvailable"/>, not on <see cref="IsSupported"/> alone.
    /// </remarks>
    public static class ScreenColorReader
    {
        /// <summary>Whether this platform can sample the screen at all. False on Linux; no user
        /// action can change it.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>Whether the OS currently permits the read — macOS Screen Recording. Always true
        /// where no such permission exists.</summary>
        public static bool IsPermitted => MacPermissions.HasScreenRecording;

        /// <summary>Whether a sample taken right now would be trustworthy.</summary>
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

            private const uint CLR_INVALID = 0xFFFFFFFF;

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll")]
            private static extern uint GetPixel(IntPtr hDC, int x, int y);
        }

        private static class MacOS
        {
            public static Color? GetColorAt(PixelPoint point)
            {
                // CoreGraphics global display space is top-left origin in points, which is exactly
                // what Avalonia's macOS screen coordinates are, so the point needs no conversion.
                // A 1x1 request still yields a 2x2 image on a Retina display; the first pixel is
                // the one under the cursor either way.
                var image = CGWindowListCreateImage(new CGRect(point.X, point.Y, 1, 1),
                                                    kCGWindowListOptionOnScreenOnly, kCGNullWindowID, kCGWindowImageDefault);
                if (image == IntPtr.Zero)
                    return null;

                IntPtr data = IntPtr.Zero;
                try
                {
                    if (CGImageGetWidth(image) == 0 || CGImageGetHeight(image) == 0)
                        return null;

                    var provider = CGImageGetDataProvider(image);
                    if (provider == IntPtr.Zero)
                        return null;

                    data = CGDataProviderCopyData(provider);
                    if (data == IntPtr.Zero)
                        return null;

                    var bitsPerPixel = (int)CGImageGetBitsPerPixel(image);
                    var bytesPerPixel = bitsPerPixel / 8;
                    if (bytesPerPixel is not 3 and not 4 || CFDataGetLength(data).ToInt64() < bytesPerPixel)
                        return null;

                    var bytes = new byte[bytesPerPixel];
                    Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytesPerPixel);

                    // Screen captures are 32bpp little-endian (BGRA in memory). Handle the
                    // big-endian and 24bpp spellings too rather than assuming.
                    var littleEndian = (CGImageGetBitmapInfo(image) & kCGBitmapByteOrderMask) == kCGBitmapByteOrder32Little;

                    if (bytesPerPixel == 4)
                        return littleEndian
                            ? Color.FromRgb(bytes[2], bytes[1], bytes[0])  // B G R A
                            : Color.FromRgb(bytes[1], bytes[2], bytes[3]); // A R G B

                    return Color.FromRgb(bytes[0], bytes[1], bytes[2]);
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
