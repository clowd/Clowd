using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>Where each keystroke line sits against the block's anchor x: its left edge, its
    /// center, or its right edge.</summary>
    public enum KeystrokeAlignment
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Which way the keystroke rows grow from the anchor. <see cref="Up"/> is the editor's
    /// bottom-anchored block: the anchor is the newest row's bottom edge and finished rows are
    /// pushed upward. <see cref="Down"/> mirrors it for a block hung from the top of a screen: the
    /// anchor is the newest row's top edge and finished rows are pushed downward. Either way the
    /// newest row holds still against the anchor and the oldest row, the one about to leave, is
    /// the one farthest from it, so a row leaving never moves the rows still showing.
    /// </summary>
    public enum KeystrokeStacking
    {
        Up,
        Down,
    }

    /// <summary>
    /// The keystroke overlay's one Skia drawing pipeline: rows of typed text in pills and special
    /// keys as <see cref="Keycap"/>s, stacked from an anchor, each row playing its own entry and
    /// exit. Shared by the video editor's keyboard track (<c>FrameComposer.DrawKeyboard</c>, preview
    /// and render alike) and the live overlay Draw on Screen puts over the desktop
    /// (<see cref="LiveKeystrokes"/>) — the two only differ in where their rows come from (a
    /// recorded capture or a live hook) and in where the block is anchored.
    /// </summary>
    internal static class KeystrokeRenderer
    {
        /// <summary>Row animation length when a transition animates but carries no
        /// duration.</summary>
        internal const int DefaultRowMs = 250;

        /// <summary>
        /// The pill geometry of one keyboard row, derived from the font size in canvas pixels:
        /// the padding round the text, the gap between stacked rows, the row's own height and the
        /// pill's corner radius. It lives in one place because two callers measure with it — the
        /// drawing below, and the editor's transform gizmo, which boxes the block through
        /// <see cref="FrameComposer.MeasureKeyboardHeight"/> and would otherwise drift from the
        /// drawn pills the first time a proportion here was touched.
        /// </summary>
        internal readonly struct KeyboardMetrics
        {
            public KeyboardMetrics(float fontPx, float lineSpacing)
            {
                PadH = fontPx * 0.55f;
                PadV = fontPx * 0.30f;
                Gap = fontPx * 0.25f;
                RowHeight = lineSpacing + 2 * PadV;
            }

            /// <summary>Horizontal padding inside a pill, each side.</summary>
            public float PadH { get; }

            /// <summary>Vertical padding inside a pill, above and below the line.</summary>
            public float PadV { get; }

            /// <summary>Vertical space between two stacked pills.</summary>
            public float Gap { get; }

            /// <summary>One pill's height: the line pitch plus its vertical padding.</summary>
            public float RowHeight { get; }

            public float CornerRadius => RowHeight * 0.3f;

            /// <summary>The block height of <paramref name="rows"/> stacked pills, gaps
            /// included.</summary>
            public float BlockHeight(int rows) =>
                rows <= 0 ? 0 : rows * RowHeight + (rows - 1) * Gap;
        }

        /// <summary>Row animation length in ms for one transition, or 0 when the row is to
        /// appear/vanish instantly. A kind that animates but carries no duration (hand written
        /// json, or an older project) gets a sane one rather than snapping.</summary>
        internal static int RowMs(Transition transition)
        {
            if (transition == null || !TransitionMath.IsAnimated(transition.Kind))
                return 0;
            return transition.DurationTicks > 0
                ? (int)(transition.DurationTicks / TimeSpan.TicksPerMillisecond)
                : DefaultRowMs;
        }

        /// <summary>
        /// Draws <paramref name="rows"/> (oldest first, as <see cref="KeyboardLayout.VisibleRowsAt"/>
        /// returns them) from the anchor point, newest row against it. Typing draws as text in a
        /// pill colored by <see cref="KeyboardContent.BackgroundColor"/>/
        /// <see cref="KeyboardContent.TextColor"/>; every special key and every chord member draws
        /// as a <see cref="Keycap"/> instead, <b>outside</b> any pill — a row is one pill per run of
        /// typed words with the caps free between them (see <see cref="DrawLine"/>).
        ///
        /// Rows animate individually with <paramref name="entry"/>/<paramref name="exit"/> — a new
        /// row plays the entry as it arrives, a finished one plays the exit once its linger is up.
        /// A vertically sliding entry also takes only its eased share of the stack, so the rows
        /// beyond it glide aside as the new one lands rather than jumping. Lines wrap at
        /// <paramref name="wrapWidth"/> canvas px and a wrapped row keeps its reading order whichever
        /// way the block stacks.
        /// </summary>
        internal static void Draw(SKCanvas target, IReadOnlyList<KeyboardRow> rows, KeyboardContent style,
            Transition entry, Transition exit, float fontPx, float anchorX, float anchorY, float wrapWidth,
            KeystrokeAlignment alignment, KeystrokeStacking stacking, double opacity)
        {
            if (rows == null || rows.Count == 0 || fontPx <= 0 || opacity <= 0)
                return;

            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            var metrics = new KeyboardMetrics(fontPx, font.Spacing);
            using var caps = new Keycap(typeface, fontPx, metrics.RowHeight);
            var atomMetrics = new AtomMetrics(font, caps, fontPx, metrics);
            // the wrap box is the caller's box outright: the pills' padding is measured inside
            // the line, so subtracting it here would reserve it twice
            wrapWidth = Math.Max(fontPx, wrapWidth);

            // wrap each row into its drawn lines, oldest row first: a wrapped run animates as one
            var wrapped = new List<(List<List<KeyAtom>> Lines, ItemEffects Fx, float Advance)>();
            foreach (var row in rows)
            {
                var (fx, advance) = RowEffects(entry, exit, row);
                if (fx.Opacity <= 0)
                    continue;
                var lines = KeyboardLayout.WrapAtoms(row.Atoms, atomMetrics, wrapWidth);
                if (lines.Count > 0)
                    wrapped.Add((lines, fx, advance));
            }

            // newest row first, out from the anchor. Up walks each row's lines last to first so the
            // first line ends up on top; Down walks them first to last for the same reading order.
            float edge = anchorY;
            float step = metrics.RowHeight + metrics.Gap;
            for (int r = wrapped.Count - 1; r >= 0; r--)
            {
                var (lines, fx, advance) = wrapped[r];
                double alpha = opacity * fx.Opacity;
                for (int k = 0; k < lines.Count; k++)
                {
                    var atoms = lines[stacking == KeystrokeStacking.Up ? lines.Count - 1 - k : k];
                    float width = KeyboardLayout.LineWidth(atoms, atomMetrics);
                    float dx = (float)(fx.OffsetXFrac * width);
                    float dy = (float)(fx.OffsetYFrac * metrics.RowHeight);
                    float left = alignment switch
                    {
                        KeystrokeAlignment.Left => anchorX,
                        KeystrokeAlignment.Right => anchorX - width,
                        _ => anchorX - width / 2,
                    };
                    float top = stacking == KeystrokeStacking.Up ? edge - metrics.RowHeight : edge;
                    var rect = new SKRect(left + dx, top + dy, left + width + dx, top + metrics.RowHeight + dy);

                    if (alpha > 0)
                        DrawLine(target, atoms, atomMetrics, font, caps, metrics, rect, style, alpha);

                    edge += (stacking == KeystrokeStacking.Up ? -1 : 1) * advance * step;
                }
            }
        }

        /// <summary>One row's animation state: what to multiply/offset it by, and the share of its
        /// slot the stack beyond it should already have moved into.</summary>
        private static (ItemEffects Fx, float Advance) RowEffects(Transition entry, Transition exit, KeyboardRow row)
        {
            var entryKind = entry?.Kind ?? TransitionKind.None;
            var exitKind = exit?.Kind ?? TransitionKind.None;
            double entryShown = TransitionMath.IsAnimated(entryKind)
                ? Easing.Apply(entry.Easing, row.EntryRaw) : 1;
            double exitShown = TransitionMath.IsAnimated(exitKind)
                ? Easing.Apply(exit.Easing, row.ExitRaw) : 1;

            // only a vertical slide compresses the stack; the others settle their slot at once
            float advance = entryKind is TransitionKind.SlideUp or TransitionKind.SlideDown
                ? (float)Math.Clamp(entryShown, 0, 1)
                : 1f;

            return (TransitionMath.EvaluateShown(entryKind, entryShown, exitKind, exitShown), advance);
        }

        /// <summary>
        /// Widths for the wrap and the drawing alike: typed words measure in the row font, keycaps
        /// in their own geometry, and neighbors are spaced by what sits between them — a typed
        /// space inside a pill, a wider gap wherever a keycap breaks the row into separate
        /// segments.
        ///
        /// The typing pill's horizontal padding is carried <b>here</b>, not added by the drawing:
        /// a row is one pill per contiguous run of typed words, so the padding falls at the run's
        /// two ends — in <see cref="Edge"/> when the run starts or ends the line, and folded into
        /// <see cref="Gap"/> at every word↔keycap boundary. Measuring and drawing therefore walk
        /// the identical arithmetic, which is what keeps a wrapped line inside its box.
        /// </summary>
        private sealed class AtomMetrics : IKeyAtomMetrics
        {
            private readonly SKFont _font;
            private readonly Keycap _caps;
            private readonly float _space;
            private readonly float _capGap;
            private readonly float _plusGap;
            private readonly float _padH;

            public AtomMetrics(SKFont font, Keycap caps, float fontPx, KeyboardMetrics metrics)
            {
                _font = font;
                _caps = caps;
                _space = font.MeasureText(" ");
                _capGap = fontPx * 0.16f;
                _plusGap = fontPx * 0.05f;
                _padH = metrics.PadH;
            }

            public float Width(KeyAtom atom) => atom.Kind == KeyAtomKind.Cap
                ? _caps.Width(atom.Text)
                : _font.MeasureText(atom.Text);

            public float Gap(KeyAtom left, KeyAtom right)
            {
                if (left.Kind == KeyAtomKind.Word && right.Kind == KeyAtomKind.Word)
                    return _space;

                // a pill ends and/or begins here, so its padding is part of the gap
                float pad = Edge(left) + Edge(right);
                bool plus = left.Kind == KeyAtomKind.Plus || right.Kind == KeyAtomKind.Plus;
                return pad + (plus ? _plusGap : _capGap);
            }

            public float Edge(KeyAtom atom) => atom.Kind == KeyAtomKind.Word ? _padH : 0;
        }

        /// <summary>
        /// Draws one laid-out line inside <paramref name="rect"/>. A keycap never sits inside a
        /// pill: the line is split into segments, one pill per contiguous run of typed words with
        /// its own padding at both ends, and every keycap (and every chord "+") standing free
        /// between them. <c>somt⌫e</c> is therefore pill·cap·pill, and a line that starts or ends
        /// on a keycap grows no empty pill beside it.
        ///
        /// Positions come from the very arithmetic <see cref="KeyboardLayout.LineWidth"/> measured
        /// the line with, so the segments cannot drift out of the rect the wrap sized. Everything
        /// centers on the row's midline by the font's real metrics.
        /// </summary>
        private static void DrawLine(SKCanvas target, IReadOnlyList<KeyAtom> atoms,
            IKeyAtomMetrics atomMetrics, SKFont font, Keycap caps, KeyboardMetrics metrics,
            SKRect rect, KeyboardContent style, double alpha)
        {
            var pen = new float[atoms.Count];
            float x = rect.Left + atomMetrics.Edge(atoms[0]);
            for (int i = 0; i < atoms.Count; i++)
            {
                if (i > 0)
                    x += atomMetrics.Width(atoms[i - 1]) + atomMetrics.Gap(atoms[i - 1], atoms[i]);
                pen[i] = x;
            }

            using var paint = new SKPaint { IsAntialias = true };

            // pills first, behind their words
            paint.Color = Keycap.Fade(new SKColor(style.BackgroundColor), alpha);
            for (int i = 0; i < atoms.Count;)
            {
                if (atoms[i].Kind != KeyAtomKind.Word)
                {
                    i++;
                    continue;
                }

                int end = i;
                while (end < atoms.Count && atoms[end].Kind == KeyAtomKind.Word)
                    end++;

                var pill = new SKRect(
                    pen[i] - metrics.PadH, rect.Top,
                    pen[end - 1] + atomMetrics.Width(atoms[end - 1]) + metrics.PadH, rect.Bottom);
                using (var rr = new SKRoundRect(pill, metrics.CornerRadius, metrics.CornerRadius))
                    target.DrawRoundRect(rr, paint);

                i = end;
            }

            var ink = Keycap.Fade(new SKColor(style.TextColor), alpha);
            float baseline = Keycap.Baseline(font, rect.MidY);

            for (int i = 0; i < atoms.Count; i++)
            {
                var atom = atoms[i];
                if (atom.Kind == KeyAtomKind.Cap)
                {
                    caps.Draw(target, atom.Text, pen[i], rect.MidY, alpha);
                    continue;
                }

                // a chord's "+" belongs to the keycaps' fixed livery, not to the typing colors
                paint.Color = atom.Kind == KeyAtomKind.Plus ? Keycap.Fade(SKColors.White, alpha) : ink;
                target.DrawText(atom.Text, pen[i], baseline, SKTextAlign.Left, font, paint);
            }
        }
    }
}
