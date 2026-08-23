using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Util
{
    /// <summary>
    /// Code-generated tiled checker brushes replacing the WPF DrawingBrush resources
    /// (decision table #42). Tile sizes match the WPF Viewport sizes: Light = 10px
    /// (CheckeredLightGrayBackgroundBrush), Medium = 16px (CheckeredMediumGrayBackgroundBrush),
    /// Swatch = 13px (the ~13.4px mini color-popup tile, approximated per §6). Those three use the
    /// original #96969696 checker color over transparent; Canvas is the 50px #11FFFFFF checker the
    /// image editor draws behind the artwork (Clowd.Drawing.CheckeredBackground renders the same
    /// pattern procedurally, because there it also has to be pannable and hit-testable).
    /// </summary>
    public static class CheckerBrushes
    {
        private static readonly Color CheckerColor = Color.FromArgb(0x96, 0x96, 0x96, 0x96);
        private static readonly Color CanvasCheckerColor = Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF);
        private static readonly Dictionary<int, IBrush> _cache = new();
        private static IBrush _canvas;

        public static IBrush Light => GetChecker(10);
        public static IBrush Medium => GetChecker(16);
        public static IBrush Swatch => GetChecker(13);

        /// <summary>The canvas backdrop shared with the image editor: a 50px #11FFFFFF checker
        /// meant to sit over a dark background, not the light gray swatch checker above.</summary>
        public static IBrush Canvas
        {
            get
            {
                lock (_cache)
                {
                    return _canvas ??= CreateCheckerBrush(50, CanvasCheckerColor);
                }
            }
        }

        private static IBrush GetChecker(int tileSize)
        {
            lock (_cache)
            {
                if (!_cache.TryGetValue(tileSize, out var brush))
                {
                    brush = CreateCheckerBrush(tileSize, CheckerColor);
                    _cache[tileSize] = brush;
                }

                return brush;
            }
        }

        private static IBrush CreateCheckerBrush(int tileSize, Color color)
        {
            // One tile contains a 2x2 checker: top-left and bottom-right cells filled
            // (matches the WPF geometry "M0,0 H1 V1 H2 V2 H1 V1 H0Z" over a 2x2 viewbox).
            var bitmap = new WriteableBitmap(
                new PixelSize(tileSize, tileSize),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            // Premultiplied BGRA bytes for the checker color.
            byte a = color.A;
            byte b = (byte)((color.B * a + 127) / 255);
            byte g = (byte)((color.G * a + 127) / 255);
            byte r = (byte)((color.R * a + 127) / 255);

            using (var fb = bitmap.Lock())
            {
                int stride = fb.RowBytes;
                var pixels = new byte[stride * tileSize];
                int half = tileSize / 2;
                for (int y = 0; y < tileSize; y++)
                {
                    bool yTop = y < half;
                    for (int x = 0; x < tileSize; x++)
                    {
                        if ((x < half) == yTop)
                        {
                            int i = y * stride + x * 4;
                            pixels[i + 0] = b;
                            pixels[i + 1] = g;
                            pixels[i + 2] = r;
                            pixels[i + 3] = a;
                        }
                    }
                }

                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }

            return new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                SourceRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative),
                DestinationRect = new RelativeRect(0, 0, tileSize, tileSize, RelativeUnit.Absolute),
            };
        }
    }
}
