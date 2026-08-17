using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.VideoSDK.Composition;
using SkiaSharp;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// One cursor style's actual arrow, in one of its colourways, drawn from the same
    /// <see cref="CursorAssets"/> layers the compositor paints — the style tiles show the glyph the
    /// render will produce rather than a stand-in icon, and the colourway tiles below them differ
    /// only in the palette they pass. The <c>native</c> style has no artwork of its own (it draws the
    /// cursor sprites the recorder rasterized into <see cref="CapturePath"/>), so its tile shows one
    /// of those sprites: the very pixels the render will composite, rather than a picture of what a
    /// cursor usually looks like. Where none can be had — no capture file, or a recording that
    /// captured no sprite — it falls back to the default style's arrow drawn as a bare monochrome
    /// outline.
    /// </summary>
    /// <remarks>
    /// The layer painting order is <c>CursorCompose</c>'s: each layer's halo stroke, then that
    /// layer's fill on top, because a halo is a <i>centred</i> stroke and would otherwise eat half
    /// of its own layer's ink. The glyph is fitted by its ink bounds rather than its viewBox — the
    /// artwork families leave very different margins, and a picker wants the arrows to read at one
    /// size. The sprite is fitted the same way, by its own bounds.
    /// </remarks>
    public sealed class CursorStylePreview : Control
    {
        /// <summary>The <c>CursorContent.Style</c> wire name to draw.</summary>
        public static readonly StyledProperty<string> StyleNameProperty =
            AvaloniaProperty.Register<CursorStylePreview, string>(nameof(StyleName));

        /// <summary>The <c>CursorContent.Variant</c> colourway to draw it in, or null for the
        /// style's default — which is all a style with one colourway ever has.</summary>
        public static readonly StyledProperty<string> VariantNameProperty =
            AvaloniaProperty.Register<CursorStylePreview, string>(nameof(VariantName));

        /// <summary>The ink the <c>native</c> outline is drawn in; unused by the themed styles,
        /// which carry their own colours.</summary>
        public static readonly StyledProperty<IBrush> OutlineBrushProperty =
            AvaloniaProperty.Register<CursorStylePreview, IBrush>(nameof(OutlineBrush),
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));

        /// <summary>The recording's input-capture sidecar (<c>Source.InputCapturePath</c>) the
        /// <c>native</c> tile takes its sprite from; unused by the themed styles, and null for a
        /// recording that has no capture file at all.</summary>
        public static readonly StyledProperty<string> CapturePathProperty =
            AvaloniaProperty.Register<CursorStylePreview, string>(nameof(CapturePath));

        static CursorStylePreview()
        {
            AffectsRender<CursorStylePreview>(
                StyleNameProperty, VariantNameProperty, OutlineBrushProperty, CapturePathProperty);
        }

        public string StyleName
        {
            get => GetValue(StyleNameProperty);
            set => SetValue(StyleNameProperty, value);
        }

        public string VariantName
        {
            get => GetValue(VariantNameProperty);
            set => SetValue(VariantNameProperty, value);
        }

        public IBrush OutlineBrush
        {
            get => GetValue(OutlineBrushProperty);
            set => SetValue(OutlineBrushProperty, value);
        }

        public string CapturePath
        {
            get => GetValue(CapturePathProperty);
            set => SetValue(CapturePathProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            // "none" is the absence of a cursor, so no artwork could stand for it — the tile
            // shows the universal ban sign instead, in the same ink as the outline fallback
            if (string.Equals(StyleName, CursorAssets.NoneStyle, StringComparison.OrdinalIgnoreCase))
            {
                DrawNone(context);
                return;
            }

            // no artwork = native (or a style name from a newer editor): show the recorded cursor,
            // or failing that the default theme's arrow as an outline
            var glyph = CursorAssets.TryGet(StyleName, VariantName, CursorAssets.KindArrow);
            bool outlineOnly = glyph == null;
            if (outlineOnly && TryDrawRecordedSprite(context))
                return;

            glyph ??= CursorAssets.TryGet(CursorAssets.DefaultStyle, CursorAssets.KindArrow);
            if (glyph == null)
                return;

            var layers = LayersOf(glyph);
            if (layers.Length == 0)
                return;

            var ink = InkBounds(layers);
            if (ink.Width <= 0 || ink.Height <= 0)
                return;

            double scale = Math.Min(Bounds.Width / ink.Width, Bounds.Height / ink.Height);
            var offset = new Point(
                (Bounds.Width - ink.Width * scale) / 2 - ink.X * scale,
                (Bounds.Height - ink.Height * scale) / 2 - ink.Y * scale);

            using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                         Matrix.CreateTranslation(offset.X, offset.Y)))
            {
                if (outlineOnly)
                {
                    var pen = new Pen(OutlineBrush, glyph.ViewBox / 20.0);
                    foreach (var layer in layers)
                        context.DrawGeometry(null, pen, layer.Geometry);
                    return;
                }

                foreach (var layer in layers)
                {
                    if (layer.Halo != null)
                        context.DrawGeometry(null, layer.Halo, layer.Geometry);
                    context.DrawGeometry(layer.Fill, null, layer.Geometry);
                }
            }
        }

        /// <summary>The ban sign — a circle with a diagonal slash — sized to the glyph tiles'
        /// visual weight and drawn in <see cref="OutlineBrush"/>, matching the outline fallback
        /// it sits beside.</summary>
        private void DrawNone(DrawingContext context)
        {
            double extent = Math.Min(Bounds.Width, Bounds.Height);
            double thickness = extent / 11.0;
            double radius = extent / 2 - thickness;
            if (radius <= 0)
                return;

            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var pen = new Pen(OutlineBrush, thickness, lineCap: PenLineCap.Round);
            context.DrawEllipse(null, pen, centre, radius, radius);
            double reach = radius / Math.Sqrt(2);
            context.DrawLine(pen,
                new Point(centre.X - reach, centre.Y - reach),
                new Point(centre.X + reach, centre.Y + reach));
        }

        /// <summary>
        /// Draws a sprite out of the recording's own capture file, centred and scaled to fit without
        /// distortion, and reports whether it drew anything. The pixels are the ones the composer
        /// paints for the <c>native</c> style, so the tile is a preview rather than a likeness — and
        /// nothing is drawn at all when the recording carries no sprites (no capture file, a v1
        /// file, a degraded capture), which leaves the outline fallback. A sprite carrying an XOR
        /// mask shows its colour plane alone: the inversion the mask describes is only defined
        /// against the pixels underneath the cursor, and a tile has none.
        /// </summary>
        private bool TryDrawRecordedSprite(DrawingContext context)
        {
            if (!InputCapture.Get(CapturePath).TryGetPreviewSprite(out var sprite))
                return false;

            var bitmap = BitmapOf(sprite);
            if (bitmap == null)
                return false;

            var source = bitmap.Size;
            if (source.Width <= 0 || source.Height <= 0)
                return false;

            double fit = Math.Min(Bounds.Width / source.Width, Bounds.Height / source.Height);
            var size = new Size(source.Width * fit, source.Height * fit);
            var origin = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
            context.DrawImage(bitmap, new Rect(origin, size));
            return true;
        }

        /// <summary>The union of the layers' ink, grown by half of the widest halo — a centred
        /// stroke spills outside the path it outlines, and the tiles are tight.</summary>
        private static Rect InkBounds(Layer[] layers)
        {
            var bounds = layers[0].Geometry.Bounds;
            double halo = 0;
            foreach (var layer in layers)
            {
                bounds = bounds.Union(layer.Geometry.Bounds);
                halo = Math.Max(halo, layer.Halo?.Thickness ?? 0);
            }
            return bounds.Inflate(halo / 2);
        }

        private readonly struct Layer
        {
            public Layer(Geometry geometry, IBrush fill, IPen halo)
            {
                Geometry = geometry;
                Fill = fill;
                Halo = halo;
            }

            public Geometry Geometry { get; }

            public IBrush Fill { get; }

            /// <summary>The contrast halo's pen, or null for a layer that has none.</summary>
            public IPen Halo { get; }
        }

        // The glyphs are immutable and process-wide (CursorAssets' own contract), so their parsed
        // Avalonia geometry is too — parse each one once, however many tiles show it. Unsynchronised
        // deliberately: rendering is the UI thread's alone.
        private static readonly Dictionary<CursorGlyph, Layer[]> LayerCache =
            new Dictionary<CursorGlyph, Layer[]>();

        private static Layer[] LayersOf(CursorGlyph glyph)
        {
            if (LayerCache.TryGetValue(glyph, out var cached))
                return cached;

            var layers = new List<Layer>(glyph.Paths.Count);
            foreach (var path in glyph.Paths)
            {
                Geometry geometry;
                try
                {
                    // "F1" = the nonzero fill rule, which is SVG's default (and Skia's) but not
                    // the path-markup parser's — without it every glyph with a hole inverts.
                    geometry = StreamGeometry.Parse("F1 " + path.PathData);
                }
                catch (Exception e) when (e is FormatException or InvalidDataException)
                {
                    // a layer this parser cannot read simply does not draw: the artwork is written
                    // for Skia's parser, and a tile is not worth throwing out of a render over
                    continue;
                }

                // round joins/caps for the same reason CursorCompose uses them: the halo stands in
                // for an outside stroke, which rounds a corner rather than mitering past it
                var halo = path.HasStroke
                    ? new Pen(new SolidColorBrush(Color.FromUInt32(path.StrokeArgb)), path.StrokeWidth,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
                    : null;
                layers.Add(new Layer(geometry,
                    new SolidColorBrush(Color.FromUInt32(path.FillArgb)), halo));
            }

            var built = layers.ToArray();
            LayerCache[glyph] = built;
            return built;
        }

        // Sprites are immutable and process-wide too (they live in InputCapture's own load cache),
        // so the same contract as LayerCache holds: decode each one once, however many tiles show
        // it, and unsynchronised because rendering is the UI thread's alone.
        private static readonly Dictionary<CursorSprite, Bitmap> SpriteCache =
            new Dictionary<CursorSprite, Bitmap>();

        /// <summary>The sprite's colour plane as an Avalonia bitmap, decoded from the PNG bytes the
        /// capture file carries and trimmed to its ink. The trim is what makes the tile read: a
        /// cursor bitmap is mostly transparent border (a small arrow parked in a 32-or-larger
        /// square), and fitting the untrimmed square would render the arrow at a fraction of the
        /// tile. Null when the bytes do not decode or hold no visible pixel — cached like a
        /// success, so a corrupt sprite costs one attempt rather than one per render.</summary>
        private static Bitmap BitmapOf(CursorSprite sprite)
        {
            if (SpriteCache.TryGetValue(sprite, out var cached))
                return cached;

            Bitmap bitmap = null;
            try
            {
                bitmap = DecodeTrimmed(sprite.Bmp);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or NotSupportedException)
            {
                // PNG bytes a decoder cannot read leave the tile to the outline fallback: a
                // picker tile is not worth throwing out of a render over
            }

            SpriteCache[sprite] = bitmap;
            return bitmap;
        }

        private static Bitmap DecodeTrimmed(byte[] png)
        {
            using var decoded = SKBitmap.Decode(png);
            if (decoded == null)
                return null;

            int left = decoded.Width, top = decoded.Height, right = -1, bottom = -1;
            for (int y = 0; y < decoded.Height; y++)
            for (int x = 0; x < decoded.Width; x++)
            {
                if (decoded.GetPixel(x, y).Alpha == 0)
                    continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }

            if (right < 0)
                return null; // fully transparent — the outline fallback beats an empty tile

            var ink = SKRectI.Create(left, top, right - left + 1, bottom - top + 1);
            using var image = SKImage.FromBitmap(decoded);
            using var subset = image.Subset(ink);
            using var data = subset.Encode(SKEncodedImageFormat.Png, 100);
            return new Bitmap(data.AsStream());
        }
    }
}
