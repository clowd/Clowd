using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Ink;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A highlighter stroke: a brush stroke whose ink is a tall, slanted chisel tip swept along the
    /// centreline (see <see cref="ChiselStrokeBuilder"/>) rather than round, pressure-thinned ink,
    /// multiplied into the artwork like marker ink (see <see cref="HighlighterDrawOperation"/>).
    /// <see cref="GraphicBase.LineWidth"/> is the tip's height. The slant is drawn from
    /// <see cref="TipSeed"/>, which is persisted with the samples so the stroke rebuilds exactly.
    /// Capture, persistence and hit-testing are the brush's; the ink lies flat on the artwork, so
    /// it casts no shadow.
    /// </summary>
    [GraphicDesc("Highlighter Stroke", Skills = Skill.Color | Skill.Stroke)]
    public class GraphicHighlighter : GraphicBrush
    {
        private int _tipSeed;

        protected GraphicHighlighter() // serializer constructor
        { }

        public GraphicHighlighter(DrawingCanvas canvas, Point origin, int tipSeed)
            : this(canvas.ObjectColor, canvas.LineWidth, origin, tipSeed)
        { }

        public GraphicHighlighter(Color objectColor, double lineWidth, Point origin, int tipSeed)
            : base(objectColor, lineWidth, origin)
        {
            _tipSeed = tipSeed;
        }

        /// <summary>The seed of the tip's slant and drift along the stroke.</summary>
        public int TipSeed => _tipSeed;

        public override bool DropShadowEffect
        {
            get => false;
            set { }
        }

        internal override double Size => Math.Max(1, LineWidth);

        internal override IInkOutline CreateOutline() => new ChiselStrokeBuilder(Size, _tipSeed);

        internal override void DrawObject(DrawingContext ctx)
        {
            if (SampleCount == 0)
                return;

            var outline = (ChiselStrokeBuilder)GetOutline();
            using (ctx.PushTransform(Matrix.CreateTranslation(Origin.X, Origin.Y)))
                ctx.Custom(new HighlighterDrawOperation(outline.Runs, outline.Bounds, ObjectColor));
        }
    }
}
