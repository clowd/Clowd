using System;
using System.Globalization;
using Avalonia.Media;
using Prisc.Interop;

namespace Prisc
{
    /// <summary>
    /// A color in OKLCH (Björn Ottosson's OKLab in polar form, as CSS Color 4 writes it): perceived
    /// lightness <see cref="L"/> 0 to 1, chroma <see cref="C"/> from 0 (gray) up to about 0.37 at the
    /// edge of the widest displays, hue <see cref="H"/> in degrees, and <see cref="Alpha"/> 0 to 1.
    /// </summary>
    /// <remarks>
    /// OKLCH reaches well past sRGB, so a value here need not be displayable: <see cref="IsInSrgb"/>
    /// says whether it is, and <see cref="ToColor"/> maps it into sRGB the way oklch.com and CSS do
    /// for an out-of-gamut color: by lowering the chroma alone, so lightness and hue hold. The
    /// conversions are <c>prisc_rust_core::color</c>'s (through <see cref="OkLch"/>); the CSS text
    /// is this type's own.
    /// </remarks>
    public readonly record struct OkLchColor(double L, double C, double H, double Alpha = 1)
    {
        /// <summary>The chroma scale's top, as oklch.com draws it: just past the most saturated
        /// color P3 can show (sRGB tops out near 0.32, at a deep blue).</summary>
        public const double ChromaMax = SharedDefaults.OklchChromaMax;

        /// <summary>The OKLCH of an sRGB color. A gray has no hue: it comes back as 0, and callers
        /// that edit hue should keep their own across one (see <see cref="IsAchromatic"/>).</summary>
        public static OkLchColor FromColor(Color color)
        {
            var (l, c, h) = OkLch.FromSrgb((uint)(color.R << 16 | color.G << 8 | color.B));
            return new OkLchColor(l, c, h, color.A / 255d);
        }

        /// <summary>Chroma below this is a gray as far as 8-bit sRGB can tell, and its hue is noise.</summary>
        public const double AchromaticChroma = SharedDefaults.OklchAchromaticChroma;

        public bool IsAchromatic => C < AchromaticChroma;

        /// <summary>Whether sRGB can show this color as it is (alpha aside).</summary>
        public bool IsInSrgb
        {
            get
            {
                OkLch.ToSrgb(L, C, H, out var inGamut);
                return inGamut;
            }
        }

        /// <summary>
        /// The sRGB color nearest this one, alpha included. Outside the gamut the chroma is lowered,
        /// never the lightness or hue, until it fits; a lightness past either end is white or black.
        /// A non-finite channel is refused (<see cref="InteropException"/>): text only becomes a
        /// value through <see cref="TryParse"/> or <see cref="TryParseNumber"/>, which never yield one.
        /// </summary>
        public Color ToColor() => ToColor(out _);

        /// <summary>
        /// <see cref="ToColor()"/> and <see cref="IsInSrgb"/> from one conversion: the nearest sRGB
        /// color, and whether this value was inside sRGB as it is.
        /// </summary>
        public Color ToColor(out bool inGamut)
        {
            var alpha = (byte)Math.Round(Math.Clamp(Alpha, 0, 1) * 255);
            var rgb = OkLch.ToSrgb(L, C, H, out inGamut);
            return Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        /// <summary>
        /// Reads one channel field as the pickers write it: a CSS number ("0.1574") in the invariant
        /// culture, with a decimal comma taken as well for anyone who types one. Only a finite value
        /// counts, so "NaN", "Infinity" and an overflow such as "1e999" are refused: no OKLCH channel
        /// is non-finite, and the conversions reject one.
        /// </summary>
        public static bool TryParseNumber(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);

        public static double NormalizeHue(double degrees)
        {
            var h = degrees % 360;
            return h < 0 ? h + 360 : h;
        }

        /// <summary>
        /// CSS text: <c>oklch(62.8% 0.2577 29.23)</c>, with <c> / 50%</c> when translucent. Lightness
        /// is a percentage to two places, chroma to four, hue to two (oklch.com's rounding), always in
        /// the invariant culture since it is CSS.
        /// </summary>
        public override string ToString()
        {
            var text = $"oklch({FormatLightness(L)} {FormatChroma(C)} {FormatHue(H)}";
            return Alpha >= 1 ? text + ")" : text + $" / {FormatAlpha(Alpha)})";
        }

        public static string FormatLightness(double l) => (Math.Round(l * 100, 2) + 0d).ToString("0.##", CultureInfo.InvariantCulture) + "%";

        public static string FormatChroma(double c) => (Math.Round(c, 4) + 0d).ToString("0.####", CultureInfo.InvariantCulture);

        public static string FormatHue(double h) => (Math.Round(h, 2) + 0d).ToString("0.##", CultureInfo.InvariantCulture);

        public static string FormatAlpha(double a) => Math.Round(a * 100).ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// Reads what <see cref="ToString"/> writes, and the looser forms people paste: lightness as
        /// a percentage or a 0-1 number, chroma as a number or a percentage (100% = 0.4, per CSS), hue
        /// with or without <c>deg</c>, alpha after a slash as a number or a percentage, commas between
        /// the values, <c>none</c> for zero, a trailing semicolon — or the bare three numbers without
        /// the <c>oklch( )</c> around them.
        /// </summary>
        public static bool TryParse(string text, out OkLchColor color)
        {
            color = default;
            var s = (text ?? "").Trim().TrimEnd(';').Trim().ToLowerInvariant();

            if (s.StartsWith("oklch(", StringComparison.Ordinal))
            {
                if (!s.EndsWith(")", StringComparison.Ordinal))
                    return false;
                s = s.Substring(6, s.Length - 7);
            }

            var alphaText = "1";
            var slash = s.IndexOf('/');
            if (slash >= 0)
            {
                alphaText = s.Substring(slash + 1).Trim();
                s = s.Substring(0, slash);
            }

            var parts = s.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;

            if (!TryNumber(parts[0], 1, out var l)
                || !TryNumber(parts[1], 0.4, out var c)
                || !TryNumber(parts[2].Replace("deg", ""), double.NaN, out var h)
                || !TryNumber(alphaText, 1, out var a))
                return false;

            // "70" can only mean 70%: no lightness is past 1
            if (!parts[0].EndsWith("%", StringComparison.Ordinal) && l > 1 && l <= 100)
                l /= 100;

            color = new OkLchColor(Math.Clamp(l, 0, 1), Math.Max(0, c), NormalizeHue(h), Math.Clamp(a, 0, 1));
            return true;
        }

        // a number, or a percentage of percentScale (NaN where a percentage is not allowed)
        private static bool TryNumber(string text, double percentScale, out double value)
        {
            value = 0;
            if (text == "none")
                return true;

            var percent = text.EndsWith("%", StringComparison.Ordinal);
            if (percent && double.IsNaN(percentScale))
                return false;

            if (!double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || !double.IsFinite(value))
                return false;

            if (percent)
                value = value / 100 * percentScale;
            return true;
        }
    }
}
