using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Single-line text drawn as a true vector outline: each glyph's geometry is stroked with a
    /// round-joined pen and then filled on top, so the outline is even the whole way around (no
    /// gaps on the cardinal edges the way an offset-copy fake outline leaves them). All strokes
    /// are drawn behind all fills, so only the outer half of the stroke is visible — the inner
    /// half is painted over by the fill — giving a clean <see cref="StrokeThickness"/>/2 outline
    /// that never eats into the letter body or a neighbouring glyph.
    /// </summary>
    public class OutlinedTextBlock : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<OutlinedTextBlock, string>(nameof(Text));

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner<OutlinedTextBlock>();

        public static readonly StyledProperty<double> FontSizeProperty =
            TextElement.FontSizeProperty.AddOwner<OutlinedTextBlock>();

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            TextElement.FontWeightProperty.AddOwner<OutlinedTextBlock>();

        public static readonly StyledProperty<IBrush> FillProperty =
            AvaloniaProperty.Register<OutlinedTextBlock, IBrush>(nameof(Fill), Brushes.White);

        public static readonly StyledProperty<IBrush> StrokeProperty =
            AvaloniaProperty.Register<OutlinedTextBlock, IBrush>(nameof(Stroke), Brushes.Black);

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<OutlinedTextBlock, double>(nameof(StrokeThickness), 2d);

        // Extra advance (logical px) inserted between glyphs to make room for the outline so
        // adjacent letters' strokes don't collide.
        public static readonly StyledProperty<double> LetterSpacingProperty =
            AvaloniaProperty.Register<OutlinedTextBlock, double>(nameof(LetterSpacing), 0d);

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public IBrush Fill
        {
            get => GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public IBrush Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public double LetterSpacing
        {
            get => GetValue(LetterSpacingProperty);
            set => SetValue(LetterSpacingProperty, value);
        }

        private readonly List<Geometry> _glyphs = new List<Geometry>();
        private double _geoWidth;
        private double _geoHeight;

        static OutlinedTextBlock()
        {
            AffectsMeasure<OutlinedTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty,
                                              LetterSpacingProperty, StrokeThicknessProperty);
            AffectsRender<OutlinedTextBlock>(FillProperty, StrokeProperty);
        }

        private void RebuildGeometry()
        {
            _glyphs.Clear();
            double x = 0;
            double height = 0;

            var text = Text;
            var size = FontSize;
            if (!string.IsNullOrEmpty(text) && size > 0)
            {
                // The outline extends StrokeThickness/2 beyond the glyph on every side; bake that
                // margin into the origin so the top/left of the outline isn't clipped at (0,0).
                var margin = StrokeThickness / 2;
                var typeface = new Typeface(FontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight);

                foreach (var ch in text)
                {
                    // NB: FormattedText.BuildGeometry returns null when the foreground brush is
                    // null, so a non-null brush is mandatory here even though the returned geometry
                    // is re-coloured by Fill/Stroke at draw time.
                    var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size,
                                               Brushes.Black);
                    var g = ft.BuildGeometry(new Point(x + margin, margin));
                    if (g != null)
                        _glyphs.Add(g);
                    x += ft.WidthIncludingTrailingWhitespace + LetterSpacing;
                    height = Math.Max(height, ft.Height);
                }

                // The last glyph gets no trailing spacing.
                x -= LetterSpacing;
            }

            _geoWidth = Math.Max(0, x);
            _geoHeight = height;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            RebuildGeometry();
            if (_glyphs.Count == 0)
                return default;

            return new Size(_geoWidth + StrokeThickness, _geoHeight + StrokeThickness);
        }

        public override void Render(DrawingContext context)
        {
            if (_glyphs.Count == 0)
                return;

            var stroke = Stroke;
            var pen = stroke != null && StrokeThickness > 0
                          ? new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }
                          : null;

            // All strokes first, then all fills, so a neighbour's outline can never paint over
            // this glyph's white body.
            if (pen != null)
                foreach (var g in _glyphs)
                    context.DrawGeometry(null, pen, g);

            var fill = Fill;
            if (fill != null)
                foreach (var g in _glyphs)
                    context.DrawGeometry(fill, null, g);
        }
    }
}
