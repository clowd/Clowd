using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Clowd
{
    /// <summary>
    /// A color as hue, saturation and value (brightness): the space the color dialog's harmony
    /// wheel works in. Hue is in degrees [0, 360), the other two in [0, 1]. Unlike a round trip
    /// through RGB, the hue survives a zero saturation or value, which is what lets a harmony keep
    /// its shape while its base is dragged through the wheel's centre or dimmed to black.
    /// </summary>
    public readonly record struct HsvColor(double Hue, double Saturation, double Value)
    {
        /// <summary>The same color with the hue wrapped into [0, 360) and the other two clamped
        /// into [0, 1].</summary>
        public HsvColor Normalized() => new(WrapHue(Hue), Math.Clamp(Saturation, 0, 1), Math.Clamp(Value, 0, 1));

        public Color ToColor(byte alpha = 255)
        {
            var n = Normalized();
            var c = n.Value * n.Saturation;
            var x = c * (1 - Math.Abs(n.Hue / 60 % 2 - 1));
            var m = n.Value - c;

            double r, g, b;
            if (n.Hue < 60) { r = c; g = x; b = 0; }
            else if (n.Hue < 120) { r = x; g = c; b = 0; }
            else if (n.Hue < 180) { r = 0; g = c; b = x; }
            else if (n.Hue < 240) { r = 0; g = x; b = c; }
            else if (n.Hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromArgb(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }

        /// <summary>The color's HSV. Alpha is dropped; a gray reports hue 0.</summary>
        public static HsvColor FromColor(Color color)
        {
            double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            double hue = 0;
            if (delta > 0)
            {
                if (max == r) hue = 60 * ((g - b) / delta % 6);
                else if (max == g) hue = 60 * ((b - r) / delta + 2);
                else hue = 60 * ((r - g) / delta + 4);
            }

            return new HsvColor(WrapHue(hue), max == 0 ? 0 : delta / max, max);
        }

        internal static double WrapHue(double hue)
        {
            hue %= 360;
            return hue < 0 ? hue + 360 : hue;
        }

        private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
    }

    /// <summary>The color dialog's harmony presets, in the order its 4 x 2 grid lays them out.
    /// There is deliberately no "custom": every palette is always one of these relationships.</summary>
    public enum HarmonyPreset
    {
        Analogous,
        Monochromatic,
        Triad,
        Complementary,
        SplitComplementary,
        Square,
        Compound,
        Shades,
    }

    /// <summary>
    /// The result of <see cref="ColorHarmony.FitClosest"/>: the preset and base that best explain a
    /// set of colors, the palette they generate, and which generated member each input landed on
    /// (<c>MemberOf[i]</c> is the member index of input <c>i</c>, so a caller can keep the user's
    /// selected color selected across the switch).
    /// </summary>
    public sealed record HarmonyFit(HarmonyPreset Preset, HsvColor Base, HsvColor[] Colors, int[] MemberOf, double Error);

    /// <summary>
    /// One dot of a preset's button glyph: where it sits (a hue offset from the base, which is
    /// drawn at the top, and a fraction of the glyph's radius) and the saturation and value it is
    /// painted with, so the buttons preview what the preset would make of the current base hue.
    /// </summary>
    public readonly record struct HarmonyGlyphDot(double HueOffset, double Radius, double Saturation, double Value)
    {
        public Color ColorFor(double baseHue) => new HsvColor(baseHue + HueOffset, Saturation, Value).ToColor();
    }

    /// <summary>
    /// The color dialog's harmony model, free of any UI: the eight presets, each generating five
    /// colors from a base, and the inverse operations the Palette tab needs to keep a preset's
    /// relationship intact while one of its colors is dragged or edited.
    /// </summary>
    /// <remarks>
    /// Each member is the base with a fixed hue offset, and its saturation and value scaled by fixed
    /// factors (the color dialog brief, §12.2: the defining hue angles are the textbook ones, the
    /// rest was tuned by eye in the mockup). Every factor is at most 1, so a member is never more
    /// saturated or brighter than the base, and every factor is non-zero, so any member can be
    /// inverted back to the base that produces it.
    /// </remarks>
    public static class ColorHarmony
    {
        /// <summary>How many colors every preset generates.</summary>
        public const int ColorCount = 5;

        /// <summary>Which of the five is the base: the middle one, with a zero offset in every preset.</summary>
        public const int BaseIndex = 2;

        private readonly record struct Member(double HueOffset, double SaturationScale, double ValueScale);

        /// <summary>Grid order, which is also the enum's order.</summary>
        public static IReadOnlyList<HarmonyPreset> Presets { get; } = new[]
        {
            HarmonyPreset.Analogous,
            HarmonyPreset.Monochromatic,
            HarmonyPreset.Triad,
            HarmonyPreset.Complementary,
            HarmonyPreset.SplitComplementary,
            HarmonyPreset.Square,
            HarmonyPreset.Compound,
            HarmonyPreset.Shades,
        };

        public static string DisplayName(HarmonyPreset preset) => preset switch
        {
            HarmonyPreset.Analogous => "Analogous",
            HarmonyPreset.Monochromatic => "Monochromatic",
            HarmonyPreset.Triad => "Triad",
            HarmonyPreset.Complementary => "Complementary",
            HarmonyPreset.SplitComplementary => "Split complementary",
            HarmonyPreset.Square => "Square",
            HarmonyPreset.Compound => "Compound",
            HarmonyPreset.Shades => "Shades",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        private static Member[] Members(HarmonyPreset preset) => preset switch
        {
            HarmonyPreset.Analogous => new Member[] { new(-40, 1, .95), new(-20, .9, 1), new(0, 1, 1), new(20, .9, 1), new(40, 1, .95) },
            HarmonyPreset.Monochromatic => new Member[] { new(0, .35, 1), new(0, 1, .55), new(0, 1, 1), new(0, .6, .8), new(0, .22, .92) },
            HarmonyPreset.Triad => new Member[] { new(120, 1, 1), new(120, .5, .85), new(0, 1, 1), new(240, 1, 1), new(240, .5, .9) },
            HarmonyPreset.Complementary => new Member[] { new(0, .5, 1), new(0, 1, .6), new(0, 1, 1), new(180, 1, 1), new(180, .5, .8) },
            HarmonyPreset.SplitComplementary => new Member[] { new(150, 1, 1), new(150, .55, .85), new(0, 1, 1), new(210, 1, 1), new(210, .55, 1) },
            HarmonyPreset.Square => new Member[] { new(90, 1, 1), new(180, 1, 1), new(0, 1, 1), new(270, 1, 1), new(0, .45, 1) },
            HarmonyPreset.Compound => new Member[] { new(30, 1, 1), new(30, .5, .7), new(0, 1, 1), new(165, 1, 1), new(195, .6, .8) },
            HarmonyPreset.Shades => new Member[] { new(0, 1, .35), new(0, 1, .55), new(0, 1, 1), new(0, 1, .8), new(0, 1, .68) },
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        /// <summary>The five colors <paramref name="preset"/> makes from <paramref name="baseColor"/>;
        /// index <see cref="BaseIndex"/> is the base itself (normalized).</summary>
        public static HsvColor[] Generate(HarmonyPreset preset, HsvColor baseColor)
        {
            var b = baseColor.Normalized();
            var members = Members(preset);
            var colors = new HsvColor[ColorCount];
            for (var i = 0; i < ColorCount; i++)
            {
                var m = members[i];
                colors[i] = new HsvColor(b.Hue + m.HueOffset, b.Saturation * m.SaturationScale, b.Value * m.ValueScale).Normalized();
            }

            return colors;
        }

        /// <summary>
        /// The base that would put member <paramref name="index"/> at <paramref name="member"/>.
        /// Where that would need a base saturation or value past 1, the base stops at 1 and the
        /// member lands as close as the preset allows: the relationship always wins over the drag.
        /// </summary>
        public static HsvColor BaseFor(HarmonyPreset preset, int index, HsvColor member)
        {
            if (index is < 0 or >= ColorCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            var m = Members(preset)[index];
            var n = member.Normalized();
            return new HsvColor(n.Hue - m.HueOffset, n.Saturation / m.SaturationScale, n.Value / m.ValueScale).Normalized();
        }

        /// <summary>
        /// The whole palette re-derived after member <paramref name="index"/> was moved to
        /// <paramref name="member"/> (a wheel drag, or an edit in the mini flyout), so the other
        /// four follow it and the preset's relationship is kept.
        /// </summary>
        public static HsvColor[] Move(HarmonyPreset preset, int index, HsvColor member) =>
            Generate(preset, BaseFor(preset, index, member));

        /// <summary>A preset button's glyph: its dots, base first (drawn at the top).</summary>
        public static IReadOnlyList<HarmonyGlyphDot> Glyph(HarmonyPreset preset) => preset switch
        {
            HarmonyPreset.Analogous => new HarmonyGlyphDot[] { new(-40, 1, .8, .94), new(-20, 1, .8, .94), new(0, 1, .8, .94), new(20, 1, .8, .94), new(40, 1, .8, .94) },
            HarmonyPreset.Monochromatic => new HarmonyGlyphDot[] { new(0, .3, .3, .94), new(0, .65, .55, .94), new(0, 1, .85, .94) },
            HarmonyPreset.Triad => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(120, 1, .8, .94), new(240, 1, .8, .94) },
            HarmonyPreset.Complementary => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(180, 1, .8, .94) },
            HarmonyPreset.SplitComplementary => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(150, 1, .8, .94), new(210, 1, .8, .94) },
            HarmonyPreset.Square => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(90, 1, .8, .94), new(180, 1, .8, .94), new(270, 1, .8, .94) },
            HarmonyPreset.Compound => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(30, 1, .8, .94), new(180, 1, .8, .94), new(210, 1, .8, .94) },
            HarmonyPreset.Shades => new HarmonyGlyphDot[] { new(0, .3, .8, .35), new(0, .65, .8, .6), new(0, 1, .8, .94) },
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        /// <summary>
        /// The preset and base that best explain five arbitrary colors (the Image tab's "To closest
        /// palette"). Every preset is tried; for each, candidate bases come from treating every input
        /// as every member, the inputs are matched to the generated members in whichever order
        /// costs least, and the best candidate is then refined by fitting the base to its matches.
        /// The error is the mean CIELAB distance (ΔE*76) between each input and its member.
        /// </summary>
        public static HarmonyFit FitClosest(IReadOnlyList<Color> colors)
        {
            if (colors == null || colors.Count != ColorCount)
                throw new ArgumentException($"Exactly {ColorCount} colors are required.", nameof(colors));

            var inputs = new HsvColor[ColorCount];
            var inputLab = new Lab[ColorCount];
            for (var i = 0; i < ColorCount; i++)
            {
                inputs[i] = HsvColor.FromColor(colors[i]);
                inputLab[i] = Lab.From(colors[i]);
            }

            HarmonyFit best = null;
            foreach (var preset in Presets)
            {
                var fit = FitPreset(preset, inputs, inputLab);
                if (best == null || fit.Error < best.Error - 1e-9)
                    best = fit;
            }

            return best;
        }

        private static HarmonyFit FitPreset(HarmonyPreset preset, HsvColor[] inputs, Lab[] inputLab)
        {
            HarmonyFit best = null;

            void Consider(HsvColor baseColor)
            {
                var generated = Generate(preset, baseColor);
                var (error, memberOf) = BestAssignment(generated, inputLab);
                if (best == null || error < best.Error - 1e-9)
                    best = new HarmonyFit(preset, baseColor.Normalized(), generated, memberOf, error);
            }

            for (var input = 0; input < ColorCount; input++)
            {
                for (var member = 0; member < ColorCount; member++)
                    Consider(BaseFor(preset, member, inputs[input]));
            }

            // a couple of rounds of fitting the base to the matches found, which finds bases that
            // sit between the inputs rather than exactly on one of them
            for (var round = 0; round < 2; round++)
                Consider(RefineBase(preset, inputs, best.MemberOf));

            return best;
        }

        /// <summary>
        /// The base minimizing the squared error of each input against the member it was matched
        /// to: hue as the circular mean of the inputs' implied base hues (weighted by how colorful
        /// each input is, since a gray's hue says nothing), saturation and value as the least-squares
        /// scale through the members' factors.
        /// </summary>
        private static HsvColor RefineBase(HarmonyPreset preset, HsvColor[] inputs, int[] memberOf)
        {
            var members = Members(preset);
            double x = 0, y = 0, sNum = 0, sDen = 0, vNum = 0, vDen = 0;

            for (var i = 0; i < ColorCount; i++)
            {
                var m = members[memberOf[i]];
                var input = inputs[i];
                var weight = input.Saturation * input.Value + 1e-6;
                var hue = (input.Hue - m.HueOffset) * Math.PI / 180;
                x += Math.Cos(hue) * weight;
                y += Math.Sin(hue) * weight;
                sNum += m.SaturationScale * input.Saturation;
                sDen += m.SaturationScale * m.SaturationScale;
                vNum += m.ValueScale * input.Value;
                vDen += m.ValueScale * m.ValueScale;
            }

            return new HsvColor(Math.Atan2(y, x) * 180 / Math.PI, sNum / sDen, vNum / vDen).Normalized();
        }

        /// <summary>The cheapest one-to-one matching of inputs to generated members, by brute force
        /// over all 120 orders (five is small enough that nothing cleverer is worth it).</summary>
        private static (double Error, int[] MemberOf) BestAssignment(HsvColor[] generated, Lab[] inputLab)
        {
            var cost = new double[ColorCount, ColorCount];
            for (var i = 0; i < ColorCount; i++)
            {
                for (var j = 0; j < ColorCount; j++)
                    cost[i, j] = inputLab[i].DistanceTo(Lab.From(generated[j].ToColor()));
            }

            var order = new[] { 0, 1, 2, 3, 4 };
            var bestOrder = (int[])order.Clone();
            var bestCost = double.MaxValue;

            void Permute(int k)
            {
                if (k == ColorCount)
                {
                    double total = 0;
                    for (var i = 0; i < ColorCount; i++)
                        total += cost[i, order[i]];
                    if (total < bestCost)
                    {
                        bestCost = total;
                        Array.Copy(order, bestOrder, ColorCount);
                    }

                    return;
                }

                for (var i = k; i < ColorCount; i++)
                {
                    (order[k], order[i]) = (order[i], order[k]);
                    Permute(k + 1);
                    (order[k], order[i]) = (order[i], order[k]);
                }
            }

            Permute(0);
            return (bestCost / ColorCount, bestOrder);
        }

        /// <summary>CIELAB (D65), for a color distance that tracks what the eye sees better than
        /// RGB or HSV do.</summary>
        private readonly record struct Lab(double L, double A, double B)
        {
            public static Lab From(Color color)
            {
                static double Linear(byte channel)
                {
                    var c = channel / 255d;
                    return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }

                double r = Linear(color.R), g = Linear(color.G), b = Linear(color.B);
                var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
                var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;

                static double F(double t) => t > 216d / 24389 ? Math.Cbrt(t) : (24389d / 27 * t + 16) / 116;

                double fx = F(x), fy = F(y), fz = F(z);
                return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
            }

            public double DistanceTo(Lab other)
            {
                double dl = L - other.L, da = A - other.A, db = B - other.B;
                return Math.Sqrt(dl * dl + da * da + db * db);
            }
        }
    }
}
