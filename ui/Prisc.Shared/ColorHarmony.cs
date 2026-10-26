using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Prisc
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

    /// <summary>What a <see cref="ToneChannel"/> does when its offset would take a member out of
    /// range (below its floor, or past 1).</summary>
    public enum ToneOverflow
    {
        /// <summary>Bounce back off the limit by however far it overshot.</summary>
        Reflect,

        /// <summary>Apply the offset the other way instead (a member meant to be darker than a
        /// base already near black comes out lighter).</summary>
        Negate,

        /// <summary>Come round from the other end of the range.</summary>
        Wrap,
    }

    /// <summary>
    /// How one member's saturation or value follows the base's: the base's level plus
    /// <see cref="Delta"/>, kept in [<see cref="Floor"/>, 1] by <see cref="Overflow"/>. The floor
    /// stops a member collapsing to gray or black when the base is dragged toward the centre or
    /// dimmed; a member the user put below the floor explicitly drops its floor to 0 instead.
    /// </summary>
    public readonly record struct ToneChannel(double Delta, double Floor, ToneOverflow Overflow)
    {
        /// <summary>The member's level for a base at <paramref name="baseLevel"/>.</summary>
        public double Apply(double baseLevel)
        {
            if (Delta == 0)
                return baseLevel;

            // a member meant to be above the base never sinks under the floor with it
            if (Delta > 0 && baseLevel + Delta < Floor)
                return Floor;

            var level = baseLevel + Delta;
            if (level < 0 || (level < Floor && baseLevel >= Floor))
            {
                level = Overflow switch
                {
                    ToneOverflow.Reflect => 2 * Floor - level,
                    ToneOverflow.Negate => baseLevel - Delta,
                    _ => Math.Max(level + 1 - Floor, Floor),
                };

                // a big offset from a mid-range base can overshoot the other end too: settle on
                // whichever end is further from the base rather than flip-flopping between them
                if (level > 1)
                    level = 1 - baseLevel >= Math.Abs(baseLevel - Floor) ? 1 : Floor;
            }
            else if (level > 1)
            {
                level = Overflow switch
                {
                    ToneOverflow.Reflect => 2 - level,
                    ToneOverflow.Negate => baseLevel - Delta,
                    _ => Math.Min(Floor + level - 1, 1),
                };

                if (level < Floor)
                    level = 1 - baseLevel >= Math.Abs(baseLevel - (1 - Delta)) ? 1 : Math.Max(1 - Delta, Floor);
            }

            return level;
        }

        /// <summary>The channel retuned so that a base at <paramref name="baseLevel"/> gives exactly
        /// <paramref name="level"/> (a member dragged or edited by hand).</summary>
        public ToneChannel Through(double baseLevel, double level) =>
            this with { Delta = level - baseLevel, Floor = level < Floor ? 0 : Floor };
    }

    /// <summary>How one palette member's saturation and value follow the base's.</summary>
    public readonly record struct HarmonyTone(ToneChannel Saturation, ToneChannel Value);

    /// <summary>
    /// A palette: a preset, a base, and each member's <see cref="HarmonyTone"/>. The preset fixes
    /// every member's hue relative to the base's, and that relationship always holds: moving any
    /// member's hue turns the whole palette. Saturation and brightness belong to each member, so a
    /// member can be lightened or desaturated on its own, and the rest only follow the base's.
    /// Immutable; every edit returns a new palette.
    /// </summary>
    public sealed class HarmonyPalette
    {
        private readonly HarmonyTone[] _tones;

        /// <summary>A palette of <paramref name="preset"/> from <paramref name="baseColor"/>, with
        /// the preset's own tones unless <paramref name="tones"/> (one per member) are given.</summary>
        public HarmonyPalette(HarmonyPreset preset, HsvColor baseColor, IReadOnlyList<HarmonyTone> tones = null)
        {
            if (tones != null && tones.Count != ColorHarmony.ColorCount)
                throw new ArgumentException($"Exactly {ColorHarmony.ColorCount} tones are required.", nameof(tones));

            Preset = preset;
            Base = baseColor.Normalized();
            _tones = tones == null ? ColorHarmony.DefaultTones(preset) : [.. tones];
            Colors = ColorHarmony.Generate(preset, Base, _tones);
        }

        public HarmonyPreset Preset { get; }

        public HsvColor Base { get; }

        public IReadOnlyList<HarmonyTone> Tones => _tones;

        /// <summary>The five colors; index <see cref="ColorHarmony.BaseIndex"/> is the base.</summary>
        public IReadOnlyList<HsvColor> Colors { get; }

        /// <summary>A new base, every member following it through its tone.</summary>
        public HarmonyPalette WithBase(HsvColor baseColor) => new(Preset, baseColor, _tones);

        /// <summary>Another preset from the same base. Tones start over from the new preset's own.</summary>
        public HarmonyPalette WithPreset(HarmonyPreset preset) => new(preset, Base);

        /// <summary>
        /// Member <paramref name="index"/> moved to <paramref name="member"/> (a wheel drag, the
        /// brightness strip, an edit in the mini flyout). The base simply becomes the new color. Any
        /// other member lands exactly there: its hue turns the base (and so every member) to keep
        /// the preset's angles, and whichever of its saturation and value changed is retuned on that
        /// member alone, leaving the base's and the other members' as they were.
        /// </summary>
        public HarmonyPalette WithMember(int index, HsvColor member)
        {
            if (index is < 0 or >= ColorHarmony.ColorCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            var m = member.Normalized();
            if (index == ColorHarmony.BaseIndex)
                return WithBase(m);

            var current = Colors[index];
            var tone = _tones[index];

            // an edit that leaves the hue alone (the brightness strip) keeps the base's exactly,
            // rather than letting it creep through the round trip on and off the artistic wheel
            var hue = Math.Abs(m.Hue - current.Hue) < 1e-9
                ? Base.Hue
                : ColorHarmony.FromArtistic(ColorHarmony.ToArtistic(m.Hue) - ColorHarmony.HueOffset(Preset, index));

            // only a channel that actually moved is retuned: one held by its floor or folded back by
            // its overflow keeps its offset, so a drag round the wheel does not quietly rewrite it
            if (Math.Abs(m.Saturation - current.Saturation) > 1e-9)
                tone = tone with { Saturation = tone.Saturation.Through(Base.Saturation, m.Saturation) };
            if (Math.Abs(m.Value - current.Value) > 1e-9)
                tone = tone with { Value = tone.Value.Through(Base.Value, m.Value) };

            var tones = (HarmonyTone[])_tones.Clone();
            tones[index] = tone;
            return new HarmonyPalette(Preset, Base with { Hue = hue }, tones);
        }
    }

    /// <summary>
    /// The result of <see cref="ColorHarmony.FitClosest"/>: the palette that best explains a set of
    /// colors, and which member each input landed on (<c>MemberOf[i]</c> is the member index of
    /// input <c>i</c>, so a caller can keep the user's selected color selected across the switch).
    /// </summary>
    public sealed record HarmonyFit(HarmonyPalette Palette, int[] MemberOf, double Error);

    /// <summary>
    /// One dot of a preset's button glyph: where it sits (a hue offset from the base on the
    /// artistic wheel, the base drawn at the top, and a fraction of the glyph's radius) and the
    /// saturation and value it is painted with, so the buttons preview what the preset would make
    /// of the current base hue.
    /// </summary>
    public readonly record struct HarmonyGlyphDot(double HueOffset, double Radius, double Saturation, double Value)
    {
        public Color ColorFor(double baseHue) =>
            new HsvColor(ColorHarmony.FromArtistic(ColorHarmony.ToArtistic(baseHue) + HueOffset), Saturation, Value).ToColor();
    }

    /// <summary>
    /// The color dialog's harmony model, free of any UI: the eight presets, each generating five
    /// colors from a base, and fitting a preset to arbitrary colors. Editing a palette is
    /// <see cref="HarmonyPalette"/>'s job.
    /// </summary>
    /// <remarks>
    /// Modelled on Adobe Color's color wheel, and benchmarked against it (ColorHarmonyTests). Hue
    /// offsets are taken on an artistic (red-yellow-blue) wheel rather than the HSV one, which
    /// gives red-orange and yellow-green more room and so puts a color's complement where a painter
    /// would expect it (orange opposite blue rather than cyan). Each member's saturation and value
    /// are the base's plus a fixed offset, kept in range by <see cref="ToneChannel"/>'s overflow
    /// rules, which is what gives several presets their muted companion colors.
    /// </remarks>
    public static class ColorHarmony
    {
        /// <summary>How many colors every preset generates.</summary>
        public const int ColorCount = 5;

        /// <summary>Which of the five is the base: the middle one, with a zero offset in every preset.</summary>
        public const int BaseIndex = 2;

        // the floors a derived member's saturation and value are held above as the base moves
        private const double SaturationFloor = 0.1;
        private const double ValueFloor = 0.2;

        // HSV hue against artistic hue, joined by straight lines: red, orange, yellow, green, cyan,
        // blue, magenta, and red again. Orange and yellow come round a quarter of the way further
        // than they do on the HSV wheel, and green to the halfway point less a bit.
        private static readonly double[] HsvKnots = [0, 35, 60, 120, 180, 240, 300, 360];
        private static readonly double[] ArtisticKnots = [0, 60, 122, 165, 218, 275, 330, 360];

        private readonly record struct Member(double HueOffset, double SaturationDelta, double ValueDelta);

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

        /// <summary>Where HSV <paramref name="hue"/> sits on the artistic wheel, in degrees.</summary>
        public static double ToArtistic(double hue) => Interpolate(HsvColor.WrapHue(hue), HsvKnots, ArtisticKnots);

        /// <summary>The HSV hue at <paramref name="angle"/> degrees round the artistic wheel.</summary>
        public static double FromArtistic(double angle) => Interpolate(HsvColor.WrapHue(angle), ArtisticKnots, HsvKnots);

        private static double Interpolate(double x, double[] from, double[] to)
        {
            var i = 1;
            while (i < from.Length - 1 && x >= from[i])
                i++;
            return HsvColor.WrapHue(to[i - 1] + (x - from[i - 1]) * (to[i] - to[i - 1]) / (from[i] - from[i - 1]));
        }

        // Each preset's members, base in the middle. Hue offsets are on the artistic wheel; the
        // deltas step the companions of each hue in toward the centre and down toward black.
        private static Member[] Members(HarmonyPreset preset) => preset switch
        {
            HarmonyPreset.Analogous => [new(-18, 0, 0), new(18, 0, 0), new(0, 0, 0), new(-36, 0, 0), new(36, 0, 0)],
            HarmonyPreset.Monochromatic => [new(0, -.2, -.2), new(0, -.4, -.4), new(0, 0, 0), new(0, -.6, -.6), new(0, -.8, -.8)],
            HarmonyPreset.Triad => [new(-120, 0, 0), new(120, 0, 0), new(0, 0, 0), new(0, -1 / 3d, -1 / 3d), new(-120, -.5, -.5)],
            HarmonyPreset.Complementary => [new(180, 0, 0), new(0, -.25, -.25), new(0, 0, 0), new(180, -1 / 3d, -1 / 3d), new(0, -.5, -.5)],
            HarmonyPreset.SplitComplementary => [new(198, 0, 0), new(162, 0, 0), new(0, 0, 0), new(0, -.5, -.5), new(198, -.5, -.5)],
            HarmonyPreset.Square => [new(-90, 0, 0), new(180, 0, 0), new(0, 0, 0), new(90, 0, 0), new(0, -1 / 3d, -1 / 3d)],
            HarmonyPreset.Compound => [new(-30, 0, 0), new(-150, 0, 0), new(0, 0, 0), new(180, 0, 0), new(0, -1 / 3d, -1 / 3d)],
            // Shades only lift brightness, coming round from the dark end once past white
            HarmonyPreset.Shades => [new(0, 0, .66), new(0, 0, .5), new(0, 0, 0), new(0, 0, .34), new(0, 0, .18)],
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        /// <summary>Member <paramref name="index"/>'s hue offset from the base, in degrees round the
        /// artistic wheel.</summary>
        public static double HueOffset(HarmonyPreset preset, int index)
        {
            if (index is < 0 or >= ColorCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Members(preset)[index].HueOffset;
        }

        /// <summary>The preset's own tones, one per member (the base's is the identity).</summary>
        public static HarmonyTone[] DefaultTones(HarmonyPreset preset)
        {
            var (saturation, value) = preset == HarmonyPreset.Shades
                ? (ToneOverflow.Negate, ToneOverflow.Wrap)
                : (ToneOverflow.Reflect, ToneOverflow.Negate);

            var members = Members(preset);
            var tones = new HarmonyTone[ColorCount];
            for (var i = 0; i < ColorCount; i++)
            {
                tones[i] = new HarmonyTone(
                    new ToneChannel(members[i].SaturationDelta, SaturationFloor, saturation),
                    new ToneChannel(members[i].ValueDelta, ValueFloor, value));
            }

            return tones;
        }

        /// <summary>The five colors <paramref name="preset"/> makes from <paramref name="baseColor"/>
        /// (with the preset's own tones unless <paramref name="tones"/> are given); index
        /// <see cref="BaseIndex"/> is the base itself (normalized).</summary>
        public static HsvColor[] Generate(HarmonyPreset preset, HsvColor baseColor, IReadOnlyList<HarmonyTone> tones = null)
        {
            var b = baseColor.Normalized();
            tones ??= DefaultTones(preset);
            var members = Members(preset);
            var artistic = ToArtistic(b.Hue);

            var colors = new HsvColor[ColorCount];
            for (var i = 0; i < ColorCount; i++)
            {
                colors[i] = i == BaseIndex
                    ? b
                    : new HsvColor(
                        FromArtistic(artistic + members[i].HueOffset),
                        tones[i].Saturation.Apply(b.Saturation),
                        tones[i].Value.Apply(b.Value)).Normalized();
            }

            return colors;
        }

        /// <summary>A preset button's glyph: its dots, base first (drawn at the top).</summary>
        public static IReadOnlyList<HarmonyGlyphDot> Glyph(HarmonyPreset preset) => preset switch
        {
            HarmonyPreset.Analogous => new HarmonyGlyphDot[] { new(-36, 1, .8, .94), new(-18, 1, .8, .94), new(0, 1, .8, .94), new(18, 1, .8, .94), new(36, 1, .8, .94) },
            HarmonyPreset.Monochromatic => new HarmonyGlyphDot[] { new(0, .3, .3, .94), new(0, .65, .55, .94), new(0, 1, .85, .94) },
            HarmonyPreset.Triad => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(120, 1, .8, .94), new(240, 1, .8, .94) },
            HarmonyPreset.Complementary => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(180, 1, .8, .94) },
            HarmonyPreset.SplitComplementary => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(162, 1, .8, .94), new(198, 1, .8, .94) },
            HarmonyPreset.Square => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(90, 1, .8, .94), new(180, 1, .8, .94), new(270, 1, .8, .94) },
            HarmonyPreset.Compound => new HarmonyGlyphDot[] { new(0, 1, .8, .94), new(-30, 1, .8, .94), new(-150, 1, .8, .94), new(180, 1, .8, .94) },
            HarmonyPreset.Shades => new HarmonyGlyphDot[] { new(0, .3, .8, .35), new(0, .65, .8, .6), new(0, 1, .8, .94) },
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        /// <summary>
        /// The palette that best explains five arbitrary colors (the Image tab's "To closest
        /// palette"). Since saturation and value are each member's own, every input keeps its own;
        /// what is fitted is the hue relationship. Every preset is tried; for each, candidate base
        /// hues come from treating every input as every member, the inputs are matched to the
        /// members in whichever order costs least, and the best candidate is then refined by fitting
        /// the base hue to its matches. The error is the mean CIELAB distance (ΔE*76) between each
        /// input and its member, so a gray's hue, which the eye cannot see, costs next to nothing.
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
            var members = Members(preset);
            (double Error, int[] MemberOf, double BaseAngle) best = (double.MaxValue, null, 0);

            void Consider(double baseAngle)
            {
                // each input against each member: the member's hue, the input's own tone
                var cost = new double[ColorCount, ColorCount];
                for (var i = 0; i < ColorCount; i++)
                {
                    for (var j = 0; j < ColorCount; j++)
                    {
                        var member = inputs[i] with { Hue = FromArtistic(baseAngle + members[j].HueOffset) };
                        cost[i, j] = inputLab[i].DistanceTo(Lab.From(member.ToColor()));
                    }
                }

                var (error, memberOf) = BestAssignment(cost);
                if (error < best.Error - 1e-9)
                    best = (error, memberOf, baseAngle);
            }

            for (var input = 0; input < ColorCount; input++)
            {
                for (var member = 0; member < ColorCount; member++)
                    Consider(ToArtistic(inputs[input].Hue) - members[member].HueOffset);
            }

            // a couple of rounds of fitting the base hue to the matches found, which finds bases
            // that sit between the inputs rather than exactly on one of them
            for (var round = 0; round < 2; round++)
                Consider(RefineBaseAngle(members, inputs, best.MemberOf));

            // the base takes its tone from the input matched to it, and every other member is
            // tuned to land on its own input's
            var baseInput = inputs[Array.IndexOf(best.MemberOf, BaseIndex)];
            var baseColor = baseInput with { Hue = FromArtistic(best.BaseAngle) };
            var tones = DefaultTones(preset);
            for (var i = 0; i < ColorCount; i++)
            {
                var j = best.MemberOf[i];
                if (j == BaseIndex)
                    continue;
                tones[j] = new HarmonyTone(
                    tones[j].Saturation.Through(baseColor.Saturation, inputs[i].Saturation),
                    tones[j].Value.Through(baseColor.Value, inputs[i].Value));
            }

            return new HarmonyFit(new HarmonyPalette(preset, baseColor, tones), best.MemberOf, best.Error);
        }

        /// <summary>The base hue (on the artistic wheel) closest to every input's implied one: their
        /// circular mean, weighted by how colorful each input is, since a gray's hue says nothing.</summary>
        private static double RefineBaseAngle(Member[] members, HsvColor[] inputs, int[] memberOf)
        {
            double x = 0, y = 0;
            for (var i = 0; i < ColorCount; i++)
            {
                var input = inputs[i];
                var weight = input.Saturation * input.Value + 1e-6;
                var angle = (ToArtistic(input.Hue) - members[memberOf[i]].HueOffset) * Math.PI / 180;
                x += Math.Cos(angle) * weight;
                y += Math.Sin(angle) * weight;
            }

            return Math.Atan2(y, x) * 180 / Math.PI;
        }

        /// <summary>The cheapest one-to-one matching of inputs (rows) to members (columns), by brute
        /// force over all 120 orders (five is small enough that nothing cleverer is worth it).</summary>
        private static (double Error, int[] MemberOf) BestAssignment(double[,] cost)
        {
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
