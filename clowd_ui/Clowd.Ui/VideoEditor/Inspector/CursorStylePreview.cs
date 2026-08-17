using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.Util;
using Clowd.VideoSDK.Composition;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// One cursor style's actual arrow, drawn from the same <see cref="CursorAssets"/> layers the
    /// compositor paints — the style tiles show the glyph the render will produce rather than a
    /// stand-in icon. The <c>native</c> style has no artwork of its own (it replays the recorded
    /// cursor box), so its tile shows the live OS arrow via <see cref="SystemCursorImage"/>: the
    /// cursor the user is looking at right now is the closest thing to a preview of "whatever the
    /// recording had". Where that cannot be had — every platform but Windows — it falls back to the
    /// default style's arrow drawn as a bare monochrome outline.
    /// </summary>
    /// <remarks>
    /// The layer painting order is <c>CursorCompose</c>'s: every halo stroke first, then
    /// every fill on top, because a halo is a <i>centred</i> stroke and would otherwise eat half of
    /// its own glyph's ink. The glyph is fitted by its ink bounds rather than its viewBox — the
    /// artwork families leave very different margins, and a picker wants the arrows to read at one
    /// size. The system bitmap is fitted the same way, by its own bounds.
    /// </remarks>
    public sealed class CursorStylePreview : Control
    {
        /// <summary>The <c>CursorContent.Style</c> wire name to draw.</summary>
        public static readonly StyledProperty<string> StyleNameProperty =
            AvaloniaProperty.Register<CursorStylePreview, string>(nameof(StyleName));

        /// <summary>The ink the <c>native</c> outline is drawn in; unused by the themed styles,
        /// which carry their own colours.</summary>
        public static readonly StyledProperty<IBrush> OutlineBrushProperty =
            AvaloniaProperty.Register<CursorStylePreview, IBrush>(nameof(OutlineBrush),
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));

        static CursorStylePreview()
        {
            AffectsRender<CursorStylePreview>(StyleNameProperty, OutlineBrushProperty);
        }

        public string StyleName
        {
            get => GetValue(StyleNameProperty);
            set => SetValue(StyleNameProperty, value);
        }

        public IBrush OutlineBrush
        {
            get => GetValue(OutlineBrushProperty);
            set => SetValue(OutlineBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            // no artwork = native (or a style name from a newer editor): show the real OS cursor,
            // or failing that the default theme's arrow as an outline
            var glyph = CursorAssets.TryGet(StyleName, CursorAssets.KindArrow);
            bool outlineOnly = glyph == null;
            if (outlineOnly && TryDrawSystemCursor(context))
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
                }
                foreach (var layer in layers)
                    context.DrawGeometry(layer.Fill, null, layer.Geometry);
            }
        }

        /// <summary>
        /// Draws the live OS arrow, centred and scaled to fit without distortion, and reports
        /// whether it drew anything. The bitmap is asked for at twice the nominal cursor size in
        /// this window's physical pixels: the trim (<c>SystemCursorImage</c> drops the transparent
        /// border) means the tile scales the ink up, and a cursor file carries larger authored
        /// frames (48, 64…), so asking big and drawing small stays crisp where asking exact would
        /// upscale. A scaling change simply asks for a different (also cached) size next render.
        /// </summary>
        private bool TryDrawSystemCursor(DrawingContext context)
        {
            if (!SystemCursorImage.IsSupported)
                return false;

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var bitmap = SystemCursorImage.Arrow((int)Math.Round(SystemCursorImage.BaseSizePx * scaling * 2));
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

                var halo = path.HasStroke
                    ? new Pen(new SolidColorBrush(Color.FromUInt32(path.StrokeArgb)), path.StrokeWidth)
                    : null;
                layers.Add(new Layer(geometry,
                    new SolidColorBrush(Color.FromUInt32(path.FillArgb)), halo));
            }

            var built = layers.ToArray();
            LayerCache[glyph] = built;
            return built;
        }
    }
}
