using System;
using System.Diagnostics;
using Avalonia.Media;
using Microsoft.Win32;

namespace Clowd
{
    /// <summary>
    /// Clowd's accent color (issue #48): either the color Windows itself is themed with, or one
    /// the user picked in the Capture settings page. Whichever it is, it is darkened until it has
    /// enough contrast with white — the overlay draws white labels and icons on top of
    /// accent-filled buttons, and a light accent leaves them unreadable.
    ///
    /// It paints the capture surfaces directly, and the rest of the app through
    /// <c>AccentTheme</c>, which writes it over the Semi theme's blue ramp — so
    /// <c>AppStyles.AccentColor</c>, reading the theme, hands back this same color.
    /// </summary>
    public static class AccentColors
    {
        /// <summary>The legacy "clowd blue" the capturer has always been drawn in.</summary>
        public static readonly Color ClowdBlue = Color.FromRgb(0x3B, 0x97, 0xD2);

        /// <summary>WCAG AA for normal text (4.5:1), measured against the white glyphs the overlay
        /// draws on top of the accent.</summary>
        public const double MinimumContrastWithWhite = 4.5;

        /// <summary>Clowd blue taken down to <see cref="MinimumContrastWithWhite"/> (#2F7CAE): the
        /// corrected default accent, and what the capturer lands on from its own
        /// <c>--accent-color</c> default (the raw <see cref="ClowdBlue"/>, corrected by
        /// clowd_capture/src/accent.rs).</summary>
        public static readonly Color Default = EnsureContrastWithWhite(ClowdBlue);

        /// <summary>Whether this OS exposes an accent color we can follow. Windows only: macOS has
        /// no equivalent we can read without AppKit interop, so the "use system accent" option is
        /// hidden there and the user's own color is always used.</summary>
        public static bool SystemAccentSupported => OperatingSystem.IsWindows();

        /// <summary>
        /// The accent color currently configured in Windows' personalization settings, or null
        /// when there is none to read (non-Windows, or the registry values are missing).
        /// Not contrast-adjusted — callers pass the result through
        /// <see cref="EnsureContrastWithWhite"/>.
        /// </summary>
        public static Color? GetSystemAccent()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                // AccentPalette holds 8 RGBA entries ordered light -> dark; index 3 is the one the
                // WinRT UISettings API reports as UIColorType.Accent, i.e. the swatch shown in the
                // Windows personalization page. Reading the registry avoids taking a WinRT
                // dependency in this net8.0 (RID-agnostic) assembly.
                using (var explorerAccent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (explorerAccent?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 16)
                        return Color.FromRgb(palette[12], palette[13], palette[14]);
                }

                // Older builds (and profiles where the palette was never written) still carry the
                // DWM title bar accent, stored as a 0xAABBGGRR DWORD.
                using (var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (dwm?.GetValue("AccentColor") is int abgr)
                        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to read the system accent color: " + ex.Message);
            }

            return null;
        }

        /// <summary>WCAG relative luminance (0 = black, 1 = white), ignoring alpha.</summary>
        public static double RelativeLuminance(Color color)
        {
            return 0.2126 * ToLinear(color.R) + 0.7152 * ToLinear(color.G) + 0.0722 * ToLinear(color.B);
        }

        /// <summary>WCAG contrast ratio between this color and white: 1 for white itself, 21 for
        /// black.</summary>
        public static double ContrastWithWhite(Color color)
        {
            return 1.05 / (RelativeLuminance(color) + 0.05);
        }

        /// <summary>
        /// Returns <paramref name="color"/> darkened just enough to reach
        /// <paramref name="minimumContrast"/> against white, or unchanged when it is dark enough
        /// already. Hue and saturation are preserved: only the brightness moves.
        /// </summary>
        public static Color EnsureContrastWithWhite(Color color, double minimumContrast = MinimumContrastWithWhite)
        {
            var targetLuminance = Math.Max(1.05 / Math.Max(minimumContrast, 1.0) - 0.05, 0.0);
            var luminance = RelativeLuminance(color);
            if (luminance <= targetLuminance)
                return color;

            // Luminance is a linear combination of the linear-light channels, so scaling all three
            // by the same factor scales the luminance by exactly that factor — no search needed,
            // and the ratios between the channels (the hue) are untouched.
            var scale = targetLuminance / luminance;
            return Color.FromArgb(color.A,
                                  Encode(ToLinear(color.R) * scale),
                                  Encode(ToLinear(color.G) * scale),
                                  Encode(ToLinear(color.B) * scale));
        }

        /// <summary>OKLab lightness the comet's body is held between. The band straddles the graphite
        /// tray (L ≈ 0.27) and its dark shadow, so a body below the floor sinks into them; above the
        /// ceiling there is too little room left for chroma and it washes out to a pastel.</summary>
        public const double CometMinLightness = 0.62, CometMaxLightness = 0.72;

        /// <summary>
        /// The two colours of the floating strips' entrance comet, derived from the accent as the user
        /// picked it (not the contrast-darkened fill, which reads as muddy once it glows): the body, and
        /// the hot tint its head burns towards.
        /// <para>
        /// Worked in OKLCH so that "lighter" moves only perceived lightness and keeps the hue the user
        /// chose — HSL lightening drifts blues toward purple and greys out yellows. The body keeps the
        /// pick's hue and as much of its chroma as sRGB can hold at the clamped lightness; the head is
        /// the same hue, lighter, with chroma eased off so it reads as a white-hot core of that colour.
        /// </para>
        /// </summary>
        public static (Color Body, Color Head) CometColors(Color picked)
        {
            var (l, c, h) = ToOkLch(picked);
            var bodyL = Math.Clamp(l, CometMinLightness, CometMaxLightness);
            var body = FromOkLch(bodyL, c, h);
            var head = FromOkLch(Math.Min(bodyL + 0.1, 0.95), c * 0.6, h);
            return (body, head);
        }

        /// <summary>OKLCH (lightness 0..1, chroma, hue in radians) of an sRGB colour, ignoring alpha.</summary>
        public static (double L, double C, double H) ToOkLch(Color color)
        {
            var (r, g, b) = (ToLinear(color.R), ToLinear(color.G), ToLinear(color.B));
            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
            var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
            var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
            return (L, Math.Sqrt(A * A + B * B), Math.Atan2(B, A));
        }

        /// <summary>
        /// The opaque sRGB colour at OKLCH (<paramref name="l"/>, <paramref name="c"/>, <paramref name="h"/>).
        /// Out of gamut, the chroma is reduced — never the lightness or the hue — until it fits, so the
        /// result is the most saturated colour sRGB has at that lightness and hue.
        /// </summary>
        public static Color FromOkLch(double l, double c, double h)
        {
            if (!TryOkLchToLinear(l, c, h, out var rgb))
            {
                double lo = 0, hi = c;
                for (var i = 0; i < 24; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (TryOkLchToLinear(l, mid, h, out _))
                        lo = mid;
                    else
                        hi = mid;
                }
                TryOkLchToLinear(l, lo, h, out rgb);
            }

            return Color.FromRgb(EncodeRounded(rgb.R), EncodeRounded(rgb.G), EncodeRounded(rgb.B));
        }

        private static bool TryOkLchToLinear(double l, double c, double h, out (double R, double G, double B) rgb)
        {
            var a = c * Math.Cos(h);
            var b = c * Math.Sin(h);
            var l_ = l + 0.3963377774 * a + 0.2158037573 * b;
            var m_ = l - 0.1055613458 * a - 0.0638541728 * b;
            var s_ = l - 0.0894841775 * a - 1.2914855480 * b;
            var (L, M, S) = (l_ * l_ * l_, m_ * m_ * m_, s_ * s_ * s_);
            rgb = (4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
                   -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
                   -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S);
            const double eps = 1e-4;
            return rgb.R >= -eps && rgb.R <= 1 + eps && rgb.G >= -eps && rgb.G <= 1 + eps && rgb.B >= -eps && rgb.B <= 1 + eps;
        }

        // Unlike Encode, rounds to the nearest channel value: nothing here is a threshold to stay under.
        private static byte EncodeRounded(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            var v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        }

        private static double ToLinear(byte channel)
        {
            var v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        // Rounds *down* to the nearest 8-bit channel: quantization must never push the result back
        // above the target luminance, or a color "fixed" for contrast could still fail the check
        // it was just adjusted for.
        private static byte Encode(double linear)
        {
            var v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Floor(v * 255.0), 0, 255);
        }
    }
}
