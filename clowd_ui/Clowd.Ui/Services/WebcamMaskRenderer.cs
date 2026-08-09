using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.Video;

namespace Clowd.UI.Services
{
    /// <summary>
    /// Rasterizes the webcam overlay mask the render tool multiplies the camera track by: an
    /// opaque PNG exactly the size of the overlay rectangle, black where the camera is hidden and
    /// white where it shows through, with an antialiased edge so a circular overlay does not come
    /// out with staircase edges. It lives in Clowd.Ui rather than beside the document model
    /// because it needs a renderer (Avalonia <see cref="RenderTargetBitmap"/>).
    ///
    /// Greyscale-by-construction rather than by pixel format: the PNG is written as RGBA (that is
    /// all Avalonia's encoder emits) with R=G=B and alpha 255, which is what the tool reads as a
    /// luminance mask.
    /// </summary>
    internal static class WebcamMaskRenderer
    {
        /// <summary>Writes the mask for <paramref name="overlay"/> at exactly
        /// <paramref name="width"/> x <paramref name="height"/> pixels. Must be called on the UI
        /// thread (Avalonia's rendering stack is not free-threaded). Throws whatever the renderer
        /// or the file system throws — the caller turns that into a failed render.</summary>
        public static void WriteMask(string path, int width, int height, WebcamOverlay overlay)
        {
            if (overlay == null)
                throw new ArgumentNullException(nameof(overlay));

            WriteMask(path, width, height, overlay.Shape, overlay.CornerRadius);
        }

        /// <summary>Shape-and-radius overload; <paramref name="cornerRadiusFraction"/> is a
        /// fraction of the mask height (0-0.5) and is ignored for
        /// <see cref="WebcamOverlayShape.Circle"/>.</summary>
        public static void WriteMask(string path, int width, int height, WebcamOverlayShape shape, double cornerRadiusFraction)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            // a zero-sized mask is not a file the tool can read; the caller clamps the rect, this
            // is the backstop.
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 96 DPI so one device-independent unit is one pixel: the rect below is in mask pixels.
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

            using (var ctx = bitmap.CreateDrawingContext(false))
            {
                var bounds = new Rect(0, 0, width, height);

                // start fully masked out, then punch the visible shape in white.
                ctx.FillRectangle(Brushes.Black, bounds);

                if (shape == WebcamOverlayShape.Circle)
                {
                    // an ellipse inscribed in the rect: for the square-ish rect the overlay
                    // normally uses this is the circle the user asked for, and for a wide camera
                    // frame it is the same shape stretched, which is what the preview draws too.
                    ctx.DrawEllipse(Brushes.White, null, bounds);
                }
                else
                {
                    // the radius is a fraction of the *height*, so a wide overlay keeps the same
                    // visual corner rounding as a tall one.
                    var radius = Math.Clamp(cornerRadiusFraction, 0, 0.5) * height;
                    // never let the corners exceed half the shorter side, or the rounded rect
                    // degenerates (Avalonia clamps too, but silently).
                    radius = Math.Min(radius, Math.Min(width, height) / 2.0);
                    ctx.DrawRectangle(Brushes.White, null, new RoundedRect(bounds, radius), default);
                }
            }

            // the explicit PNG options overload; the int? one is obsolete in Avalonia 12.
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
    }
}
