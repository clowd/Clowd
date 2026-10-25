using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// One shape-picker tile's picture: its kind drawn by <see cref="ShapeRenderer.Draw"/>, the very
    /// call <c>FrameComposer</c> makes, in the selected shape's own colors, dash, fill and dials, so
    /// every tile previews "this kind, in your style" and none can drift from what the pick will
    /// draw. What the tile draws is decided by <see cref="ShapeKindTile"/>; this control only
    /// lays down the plate and hands the shape to Avalonia's Skia lease, exactly as
    /// <see cref="BackgroundStylePreview"/> does. On a backend that is not Skia the tile shows the
    /// bare plate.
    /// </summary>
    /// <remarks>
    /// <b>The plate is what makes this readable</b>, as it is for <see cref="SurroundPreview"/>: a
    /// shape can be black, white or red, and the sidebar is near-black in one chrome and near-white
    /// in the other, so every tile draws on the same mid gray.
    /// </remarks>
    public sealed class ShapeKindPreview : Control
    {
        private const double PlateRadius = 4;

        /// <summary>The notional canvas behind the shape: <see cref="SurroundPreview"/>'s
        /// gray.</summary>
        private static readonly IBrush PlateBrush = new SolidColorBrush(Color.FromRgb(0x74, 0x74, 0x74));

        public static readonly StyledProperty<ShapeKind> KindProperty =
            AvaloniaProperty.Register<ShapeKindPreview, ShapeKind>(nameof(Kind));

        public static readonly StyledProperty<string> StrokeColorProperty =
            AvaloniaProperty.Register<ShapeKindPreview, string>(nameof(StrokeColor), ShapeContent.DefaultStrokeColor);

        /// <summary>The shape's real width in output pixels. Only whether it is zero matters to
        /// the tile (see <see cref="ShapeKindTile.Shape"/>).</summary>
        public static readonly StyledProperty<double> StrokeWidthProperty =
            AvaloniaProperty.Register<ShapeKindPreview, double>(nameof(StrokeWidth), ShapeContent.DefaultStrokeWidth);

        public static readonly StyledProperty<string> FillColorProperty =
            AvaloniaProperty.Register<ShapeKindPreview, string>(nameof(FillColor), ShapeContent.DefaultFillColor);

        public static readonly StyledProperty<bool> FillProperty =
            AvaloniaProperty.Register<ShapeKindPreview, bool>(nameof(Fill));

        public static readonly StyledProperty<ShapeDash> DashProperty =
            AvaloniaProperty.Register<ShapeKindPreview, ShapeDash>(nameof(Dash));

        public static readonly StyledProperty<double> CornerRadiusProperty =
            AvaloniaProperty.Register<ShapeKindPreview, double>(nameof(CornerRadius));

        // doubles, like the inspector's spinners they are bound from; the model's whole numbers
        // are the rounded values
        public static readonly StyledProperty<double> PolygonSidesProperty =
            AvaloniaProperty.Register<ShapeKindPreview, double>(nameof(PolygonSides), ShapeContent.DefaultPolygonSides);

        public static readonly StyledProperty<double> StarPointsProperty =
            AvaloniaProperty.Register<ShapeKindPreview, double>(nameof(StarPoints), ShapeContent.DefaultStarPoints);

        public static readonly StyledProperty<double> StarInnerRatioProperty =
            AvaloniaProperty.Register<ShapeKindPreview, double>(nameof(StarInnerRatio), ShapeContent.DefaultStarInnerRatio);

        static ShapeKindPreview()
        {
            AffectsRender<ShapeKindPreview>(KindProperty, StrokeColorProperty, StrokeWidthProperty,
                FillColorProperty, FillProperty, DashProperty, CornerRadiusProperty, PolygonSidesProperty,
                StarPointsProperty, StarInnerRatioProperty);
        }

        public ShapeKind Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public string StrokeColor
        {
            get => GetValue(StrokeColorProperty);
            set => SetValue(StrokeColorProperty, value);
        }

        public double StrokeWidth
        {
            get => GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public string FillColor
        {
            get => GetValue(FillColorProperty);
            set => SetValue(FillColorProperty, value);
        }

        public bool Fill
        {
            get => GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public ShapeDash Dash
        {
            get => GetValue(DashProperty);
            set => SetValue(DashProperty, value);
        }

        public double CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public double PolygonSides
        {
            get => GetValue(PolygonSidesProperty);
            set => SetValue(PolygonSidesProperty, value);
        }

        public double StarPoints
        {
            get => GetValue(StarPointsProperty);
            set => SetValue(StarPointsProperty, value);
        }

        public double StarInnerRatio
        {
            get => GetValue(StarInnerRatioProperty);
            set => SetValue(StarInnerRatioProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width, height = Bounds.Height;
            if (width <= 0 || height <= 0)
                return;

            var plate = new Rect(0, 0, width, height);
            using (context.PushClip(new RoundedRect(plate, PlateRadius)))
            {
                context.DrawRectangle(PlateBrush, null, plate, PlateRadius, PlateRadius);

                var shape = ShapeKindTile.Shape(Kind, StrokeColor, StrokeWidth, FillColor, Fill, Dash,
                    CornerRadius, PolygonSides, StarPoints, StarInnerRatio);
                var box = ShapeKindTile.Box(shape.Kind, width, height);
                if (box.Width > 0 && box.Height > 0)
                    context.Custom(new TileDrawOperation(plate, box, shape));
            }
        }

        /// <summary>
        /// The draw itself, on Avalonia's own leased Skia canvas. The shape is a fresh object built
        /// on the UI thread for this pass alone, so nothing here can change under the render
        /// thread, and there is nothing to dispose.
        /// </summary>
        private sealed class TileDrawOperation : ICustomDrawOperation
        {
            private readonly SKRect _box;
            private readonly ShapeContent _shape;

            public TileDrawOperation(Rect bounds, SKRect box, ShapeContent shape)
            {
                Bounds = bounds;
                _box = box;
                _shape = shape;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            /// <summary>Never equal, so Avalonia never keeps a previous pass's operation in place of
            /// this one: the same answer <see cref="BackgroundStylePreview"/>'s tiles give.</summary>
            public bool Equals(ICustomDrawOperation other) => false;

            public void Dispose()
            {
            }

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature == null)
                    return; // not the Skia backend: the plate alone

                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;
                if (canvas == null)
                    return;

                // the leased canvas is already in the control's coordinate space and inside the
                // plate's rounded clip, so the box goes in as it is
                ShapeKindTile.Draw(canvas, _box, _shape);
            }
        }
    }

    /// <summary>
    /// What a <see cref="ShapeKindPreview"/> tile draws and where, free of Avalonia so a test can
    /// render the very tiles the picker shows.
    /// </summary>
    /// <remarks>
    /// <b>It exaggerates, deliberately.</b> An outline a few output pixels wide is invisible at
    /// tile size, so the tile draws any visible outline 2 tile pixels wide and scales the corner
    /// radius down to what a 16 pixel shape can show; the colors, the dash style and the kind's own
    /// dials are the user's. A line kind is drawn corner to corner across the tile, straight, so
    /// the three line tiles differ only by their heads. A tile that would draw nothing at all (no
    /// outline and no visible fill) draws a white outline instead: the tile is a picker first.
    /// </remarks>
    public static class ShapeKindTile
    {
        /// <summary>Tile pixels per output pixel, as <see cref="ShapeRenderer.Draw"/> takes it: the
        /// tile's stand-in outline (<see cref="StrokeWidth"/>) and the arrow's head floor come out
        /// at half their output size.</summary>
        public const double PixelScale = 0.5;

        /// <summary>The width every visible outline is drawn at, in output pixels: 2 tile
        /// pixels.</summary>
        public const double StrokeWidth = 4;

        /// <summary>The gap between the plate's edge and the shape's box, in tile pixels.</summary>
        public const double Inset = 3;

        /// <summary>How much of a corner radius (output pixels) the tile keeps, and the most it
        /// keeps: a 16 pixel square cannot show a radius sized for a 270 pixel one.</summary>
        private const double CornerFactor = 0.08;

        private const double MaxCornerRadius = 8;

        // a line kind's endpoints, as fractions of its box: bottom left to top right, like a fresh
        // arrow, pulled in so the heads stay on the plate
        private const double LineStartX = 0.12, LineStartY = 0.85, LineEndX = 0.88, LineEndY = 0.15;

        /// <summary>The shape a tile draws: the picked style with the tile's exaggerations (see the
        /// class remarks).</summary>
        public static ShapeContent Shape(ShapeKind kind, string strokeColor, double strokeWidth,
            string fillColor, bool fill, ShapeDash dash, double cornerRadius, double polygonSides,
            double starPoints, double starInnerRatio)
        {
            var line = ShapeContent.IsLineKind(kind);
            // a line kind always draws at least 1 px wide, so only its color can hide it
            var strokeShows = (line || strokeWidth > 0) && AlphaOf(strokeColor, ShapeContent.DefaultStrokeColor) > 0;
            var fillShows = !line && fill && AlphaOf(fillColor, ShapeContent.DefaultFillColor) > 0;

            var shape = new ShapeContent
            {
                Kind = kind,
                StrokeColor = strokeColor,
                StrokeWidth = strokeShows ? StrokeWidth : 0,
                Dash = dash,
                Fill = fill,
                FillColor = fillColor,
                CornerRadius = Double.IsNaN(cornerRadius)
                    ? 0
                    : Math.Clamp(cornerRadius * CornerFactor, 0, MaxCornerRadius),
                PolygonSides = WholeNumber(polygonSides, ShapeContent.DefaultPolygonSides),
                StarPoints = WholeNumber(starPoints, ShapeContent.DefaultStarPoints),
                StarInnerRatio = Double.IsNaN(starInnerRatio) ? ShapeContent.DefaultStarInnerRatio : starInnerRatio,
            };

            if (line)
            {
                shape.StartX = LineStartX;
                shape.StartY = LineStartY;
                shape.EndX = LineEndX;
                shape.EndY = LineEndY;
            }

            if (!strokeShows && !fillShows)
            {
                // a tile that draws nothing cannot be picked by eye
                shape.StrokeColor = "#FFFFFFFF";
                shape.StrokeWidth = StrokeWidth;
            }

            return shape;
        }

        /// <summary>Where a tile of <paramref name="width"/> by <paramref name="height"/> draws the
        /// shape: the plate inset by <see cref="Inset"/>, as a centered square for a closed kind (a
        /// circle reads as a circle, a star as a star) and whole for a line kind, which runs corner
        /// to corner across it.</summary>
        public static SKRect Box(ShapeKind kind, double width, double height)
        {
            double w = width - 2 * Inset, h = height - 2 * Inset;
            if (!(w > 0) || !(h > 0))
                return SKRect.Empty;

            if (ShapeContent.IsLineKind(kind))
                return SKRect.Create((float)Inset, (float)Inset, (float)w, (float)h);

            double side = Math.Min(w, h);
            return SKRect.Create((float)((width - side) / 2), (float)((height - side) / 2), (float)side, (float)side);
        }

        /// <summary>Draws a tile's shape onto <paramref name="canvas"/>, already set up in the
        /// tile's own coordinates: everything the picker draws but the plate.</summary>
        public static void Draw(SKCanvas canvas, SKRect box, ShapeContent shape) =>
            ShapeRenderer.Draw(canvas, box, shape, PixelScale);

        private static int WholeNumber(double value, int fallback) =>
            Double.IsNaN(value) ? fallback : (int)Math.Round(value);

        /// <summary>A model color's alpha, read the way the composer reads it: a value that does
        /// not parse draws in <paramref name="fallback"/>.</summary>
        private static byte AlphaOf(string color, string fallback) =>
            (!String.IsNullOrWhiteSpace(color) && SKColor.TryParse(color, out var parsed)
                ? parsed
                : SKColor.Parse(fallback)).Alpha;
    }
}
