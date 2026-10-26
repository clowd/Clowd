using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// A freehand stroke's filled outline, kept current as samples are appended: what
    /// <see cref="GraphicBrush"/> draws, hit-tests and bounds. Two pieces, both local to the stroke's
    /// origin: <see cref="Settled"/>, the part no later sample can change (null until there is
    /// one), and <see cref="Tail"/>, the rest through the tip. The brush's round, pressure-thinned
    /// ink is <see cref="FreehandStrokeBuilder"/>; the highlighter's chisel tip is
    /// <see cref="ChiselStrokeBuilder"/>.
    /// </summary>
    internal interface IInkOutline
    {
        /// <summary>The tip size the outline was built for; a different one needs a new outline.</summary>
        double Size { get; }

        /// <summary>The samples the last <see cref="Update"/> saw.</summary>
        int SampleCount { get; }

        /// <summary>The finished part of the stroke as one NonZero geometry; null until there is one.</summary>
        Geometry Settled { get; }

        /// <summary>The rest of the stroke through the tip; never null after an Update.</summary>
        Geometry Tail { get; }

        /// <summary>Union of the two pieces' bounds, in the samples' (origin-local) space.</summary>
        Rect Bounds { get; }

        /// <summary>True when <see cref="Update"/> can take <paramref name="sampleCount"/> samples
        /// as a continuation of what it has; anything else needs a fresh outline.</summary>
        bool CanAppend(int sampleCount);

        /// <summary>Folds the samples appended since the last call into the outline.</summary>
        void Update(ReadOnlySpan<GraphicBrush.Sample> samples);

        /// <summary>Drops the incremental state once the stroke is finished, keeping only what is drawn.</summary>
        void Compact();

        bool FillContains(Point p);

        bool StrokeContains(IPen pen, Point p);
    }
}
