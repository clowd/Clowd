using System;
using System.Globalization;
using Avalonia.Media;

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
    /// for an out-of-gamut color — by lowering the chroma alone, so lightness and hue hold.
    /// </remarks>
    public readonly record struct OkLchColor(double L, double C, double H, double Alpha = 1)
    {
        /// <summary>The chroma scale's top, as oklch.com draws it: just past the most saturated
        /// color P3 can show (sRGB tops out near 0.32, at a deep blue).</summary>
        public const double ChromaMax = 0.37;

        // how far outside 0..1 a linear channel may land and still count as in gamut, which keeps
        // float error from flagging an sRGB color as outside. Kept tiny: near black every channel
        // is close to zero, and a looser one let impossible dark colors through as near-black.
        private const double GamutTolerance = 1e-6;

        /// <summary>The OKLCH of an sRGB color. A gray has no hue: it comes back as 0, and callers
        /// that edit hue should keep their own across one (see <see cref="IsAchromatic"/>).</summary>
        public static OkLchColor FromColor(Color color)
        {
            var (r, g, b) = (ToLinear(color.R), ToLinear(color.G), ToLinear(color.B));
            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
            var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
            var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
            var c = Math.Sqrt(A * A + B * B);
            var h = c < AchromaticChroma ? 0 : NormalizeHue(Math.Atan2(B, A) * 180 / Math.PI);
            return new OkLchColor(L, c, h, color.A / 255d);
        }

        /// <summary>Chroma below this is a gray as far as 8-bit sRGB can tell, and its hue is noise.</summary>
        public const double AchromaticChroma = 2e-4;

        public bool IsAchromatic => C < AchromaticChroma;

        /// <summary>Whether sRGB can show this color as it is (alpha aside).</summary>
        public bool IsInSrgb => ToLinearSrgb(L, C, H, out _, out _, out _);

        /// <summary>
        /// The sRGB color nearest this one, alpha included. Outside the gamut the chroma is lowered,
        /// never the lightness or hue, until it fits; a lightness past either end is white or black.
        /// </summary>
        public Color ToColor()
        {
            var alpha = (byte)Math.Round(Math.Clamp(Alpha, 0, 1) * 255);
            var l = Math.Clamp(L, 0, 1);

            if (!ToLinearSrgb(l, C, H, out var r, out var g, out var b))
            {
                double lo = 0, hi = C;
                for (var i = 0; i < 24; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (ToLinearSrgb(l, mid, H, out _, out _, out _))
                        lo = mid;
                    else
                        hi = mid;
                }
                ToLinearSrgb(l, lo, H, out r, out g, out b);
            }

            return Color.FromArgb(alpha, Encode(r), Encode(g), Encode(b));
        }

        /// <summary>
        /// OKLCH to linear-light sRGB, the inner loop of the pickers' gamut charts. Returns whether
        /// the result is inside sRGB; the channels are left unclamped either way.
        /// </summary>
        public static bool ToLinearSrgb(double l, double c, double hueDegrees, out double r, out double g, out double b)
        {
            var hue = hueDegrees * Math.PI / 180;
            var a = c * Math.Cos(hue);
            var bb = c * Math.Sin(hue);
            var l_ = l + 0.3963377774 * a + 0.2158037573 * bb;
            var m_ = l - 0.1055613458 * a - 0.0638541728 * bb;
            var s_ = l - 0.0894841775 * a - 1.2914855480 * bb;
            var (L, M, S) = (l_ * l_ * l_, m_ * m_ * m_, s_ * s_ * s_);
            r = 4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S;
            g = -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S;
            b = -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S;
            const double lo = -GamutTolerance, hi = 1 + GamutTolerance;
            return r >= lo && r <= hi && g >= lo && g <= hi && b >= lo && b <= hi;
        }

        /// <summary>A linear-light channel as an 8-bit sRGB one, rounded to nearest.</summary>
        public static byte Encode(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            var v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        }

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

        private static double ToLinear(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }
}
