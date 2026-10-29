using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prisc.Interop;
using Prisc.UI;

namespace Prisc.Util
{
    /// <summary>
    /// Reads the screen as it is right now, anywhere on the virtual desktop and outside any Prisc
    /// window, with no overlay and no process of its own. Two reads, each on the cheapest path for
    /// its caller: <see cref="GetColorAt"/>, one pixel, for the color pickers' eyedropper (called on
    /// every mouse move of a drag); and <see cref="Grab"/>, a whole monitor, for the frozen backdrop
    /// behind Draw on Instant Screenshot (taken before anything of ours is on screen). Not the
    /// capturer's job: that is an interactive overlay.
    /// </summary>
    /// <remarks>
    /// Both go through <see cref="ScreenCapture"/>, the grab the capture overlay and the scrolling
    /// driver use too: a GDI blit on Windows (windows marked WDA_EXCLUDEFROMCAPTURE stay out of it,
    /// as for every capture API), a <c>CGWindowListCreateImage</c> on macOS, which needs the same
    /// Screen Recording permission the full capturer requires. Linux has no equivalent that works
    /// without a compositor-specific portal, so <see cref="IsSupported"/> is false there and callers
    /// hide what they would have offered. The cursor is never in the picture.
    ///
    /// The two reasons a read cannot happen are kept apart, because they call for different UI:
    /// <see cref="IsSupported"/> is a platform verdict that will never change, while
    /// <see cref="IsPermitted"/> is something the user can fix in System Settings. Missing
    /// permission is worse than useless rather than merely unavailable: an unpermitted
    /// <c>CGWindowListCreateImage</c> succeeds and hands back the desktop picture instead of the
    /// windows in front of it, so it would silently read the wrong pixels (the native reads refuse
    /// it too). Callers must gate on <see cref="IsAvailable"/>, not on <see cref="IsSupported"/>
    /// alone.
    /// </remarks>
    internal static class ScreenPixels
    {
        /// <summary>Whether this platform can read the screen at all. False on Linux; no user
        /// action can change it.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>Whether the OS currently permits the read: macOS Screen Recording. Always true
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
            if (!IsSupported || !IsPermitted)
                return null;

            try
            {
                return ScreenCapture.ReadPixel(point.X, point.Y) is { } rgb
                    ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                    : null;
            }
            catch (InteropException ex)
            {
                // the permission was revoked between the check and the read, or the OS refused
                Debug.WriteLine("ScreenPixels.GetColorAt: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The pixels inside <paramref name="bounds"/> (capture space: physical virtual-desktop
        /// pixels on Windows, CG points on macOS), as an opaque bitmap whose DPI makes its DIP size
        /// the monitor's. <paramref name="scaling"/> is no longer read: the native grab reports the
        /// image's own pixels per DIP (the monitor's DPI scale on Windows, the backing scale on
        /// macOS). Null when unsupported, unpermitted, or the grab fails.
        /// </summary>
        public static WriteableBitmap Grab(PixelRect bounds, double scaling)
        {
            _ = scaling;
            if (!IsSupported || !IsPermitted || bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            try
            {
                using var image = ScreenCapture.Grab(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                return Wrap(image);
            }
            catch (InteropException ex)
            {
                Debug.WriteLine($"ScreenPixels.Grab {bounds}: {ex.Message}");
                if (ex.Status != InteropStatus.Permission)
                    SentryConfig.CaptureHandled(ex, "pixels.grab");
                return null;
            }
        }

        /// <summary>The grab as an opaque BGRA bitmap whose DPI makes its DIP size the
        /// rectangle's.</summary>
        private static WriteableBitmap Wrap(ScreenImage image)
        {
            var dpi = 96 * image.DipScale;
            var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(dpi, dpi),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);

            using (var buffer = bitmap.Lock())
                image.CopyTo(buffer.Address, buffer.RowBytes);

            return bitmap;
        }
    }
}
