using System;
using System.Collections.Generic;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>One mark on an input-overlay item, in item-local pixels: a click (or held drag)
    /// on the cursor row, a keystroke run — or several close together — on the keys row.</summary>
    internal readonly record struct InputMark(double X, double Width, int Count);

    /// <summary>
    /// The zoom-dependent part of the cursor and keys rows' previews, as pure math: where each
    /// click and keystroke run lands on an item at the current scale, and how nearby ones fold
    /// together. Everything here is in item-local pixels (0 = the item body's left edge) so the
    /// surface draws the result under a translate and caches it across scrolls.
    /// </summary>
    internal static class InputPreviewMath
    {
        /// <summary>A press is drawn at least this wide, so a tap stays a mark rather than a
        /// hairline; a hold stretches it to cover the drag.</summary>
        public const double ClickMarkMinWidth = 4;

        /// <summary>A lone keystroke run is drawn at least this wide — a single key-down has no
        /// extent of its own.</summary>
        public const double KeyBlipMinWidth = 3;

        /// <summary>Two runs closer than this on screen fold into one bigger blip: at that gap the
        /// eye reads them as one burst anyway, and separate marks would only dither.</summary>
        public const double KeyBlipMergeGap = 4;

        /// <summary>Blip height for a single run, and how much every run folded into it adds. The
        /// height is capped by the body (see <see cref="KeyBlipHeight"/>), so a big cluster reads
        /// as "a lot" and then stops growing — the width already says how long it went on.</summary>
        public const double KeyBlipBaseHeight = 6;

        public const double KeyBlipStepHeight = 2.5;

        /// <summary>The click marks of one item: each press from its down to its up, at least
        /// <see cref="ClickMarkMinWidth"/> wide, with marks that overlap (or touch, to the pixel)
        /// merged — zoomed out, a burst of clicks becomes one bar rather than a smear of
        /// overdraw. <see cref="InputMark.Count"/> is how many presses a mark covers.</summary>
        public static List<InputMark> ClickMarks(IReadOnlyList<CursorClickSpan> clicks, long sourceInTicks,
            double speed, double ticksPerPixel)
        {
            var marks = new List<InputMark>();
            if (clicks == null || clicks.Count == 0 || !(ticksPerPixel > 0))
                return marks;

            var scale = 1.0 / (Math.Max(speed, Double.Epsilon) * ticksPerPixel);
            foreach (var click in clicks)
            {
                var x = (click.DownTicks - sourceInTicks) * scale;
                var width = Math.Max(ClickMarkMinWidth, (click.UpTicks - click.DownTicks) * scale);
                Append(marks, x, width, mergeGap: 1);
            }

            return marks;
        }

        /// <summary>The keystroke blips of one item: each run from its first key to its last, at
        /// least <see cref="KeyBlipMinWidth"/> wide, with runs less than
        /// <see cref="KeyBlipMergeGap"/> apart on screen folded into one blip whose
        /// <see cref="InputMark.Count"/> is the number of runs it stands for.</summary>
        public static List<InputMark> KeyBlips(IReadOnlyList<TimelineKeyRun> runs, long sourceInTicks,
            double speed, double ticksPerPixel)
        {
            var marks = new List<InputMark>();
            if (runs == null || runs.Count == 0 || !(ticksPerPixel > 0))
                return marks;

            var scale = 1.0 / (Math.Max(speed, Double.Epsilon) * ticksPerPixel);
            foreach (var run in runs)
            {
                var x = (run.StartTicks - sourceInTicks) * scale;
                var width = Math.Max(KeyBlipMinWidth, (run.EndTicks - run.StartTicks) * scale);
                Append(marks, x, width, KeyBlipMergeGap);
            }

            return marks;
        }

        /// <summary>How tall a blip standing for <paramref name="count"/> runs draws on a body
        /// <paramref name="bodyHeight"/> tall: <see cref="KeyBlipBaseHeight"/> plus a step per
        /// extra run, never within two pixels of the body's edges.</summary>
        public static double KeyBlipHeight(int count, double bodyHeight)
        {
            var max = Math.Max(2, bodyHeight - 4);
            var height = KeyBlipBaseHeight + Math.Max(0, count - 1) * KeyBlipStepHeight;
            return Math.Min(max, height);
        }

        /// <summary>Adds a mark, folding it into the previous one when the gap between them is
        /// under <paramref name="mergeGap"/>. Inputs arrive ascending by start, so only the last
        /// mark can ever be the neighbor.</summary>
        private static void Append(List<InputMark> marks, double x, double width, double mergeGap)
        {
            if (marks.Count > 0)
            {
                var last = marks[^1];
                if (x - (last.X + last.Width) < mergeGap)
                {
                    var right = Math.Max(last.X + last.Width, x + width);
                    var left = Math.Min(last.X, x);
                    marks[^1] = new InputMark(left, right - left, last.Count + 1);
                    return;
                }
            }

            marks.Add(new InputMark(x, width, 1));
        }
    }
}
