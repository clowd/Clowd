using System;
using System.Diagnostics;
using Avalonia.Media;
using Microsoft.Win32;
using Prisc.Interop;

namespace Prisc
{
    /// <summary>
    /// Prisc's accent color (issue #48): either the color Windows itself is themed with, or one
    /// the user picked in the Capture settings page. Whichever it is, it is darkened until it has
    /// enough contrast with white — the overlay draws white labels and icons on top of
    /// accent-filled buttons, and a light accent leaves them unreadable.
    ///
    /// It paints the capture surfaces directly, and the rest of the app through
    /// <c>AccentTheme</c>, which writes it over the Semi theme's blue ramp — so
    /// <c>AppStyles.AccentColor</c>, reading the theme, hands back this same color.
    ///
    /// The maths is <c>prisc_rust_core::color</c> (through <see cref="ColorMath"/>), the code the
    /// capture overlay paints with, so the two cannot drift; the constants are generated
    /// (<see cref="SharedDefaults"/>), so reading them never needs the native library.
    /// </summary>
    public static class AccentColors
    {
        /// <summary>The legacy "prisc blue" the capturer has always been drawn in.</summary>
        public static readonly Color PriscBlue = FromRgb(SharedDefaults.PriscBlue);

        /// <summary>Prisc blue taken down to <see cref="SharedDefaults.MinimumContrastWithWhite"/>
        /// (#2F7CAE): the corrected default accent, and what the capturer lands on from its own
        /// <c>--accent-color</c> default. Computed in Rust when the constants are generated, so type
        /// initialisation makes no native call.</summary>
        public static readonly Color Default = FromRgb(SharedDefaults.DefaultAccent);

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
        public static double RelativeLuminance(Color color) => ColorMath.RelativeLuminance(Rgb(color));

        /// <summary>
        /// Returns <paramref name="color"/> darkened just enough to reach
        /// <see cref="SharedDefaults.MinimumContrastWithWhite"/> against white, or unchanged when it
        /// is dark enough already. Hue and saturation are preserved, only the brightness moves, and
        /// so is alpha.
        /// </summary>
        public static Color EnsureContrastWithWhite(Color color)
            => WithAlpha(color.A, ColorMath.Palette(Rgb(color), maintainContrast: true).Effective);

        /// <summary>
        /// The two colours of the capture overlay's hint comet (the overlay derives its own from the
        /// same pick through the same Rust code), from the accent as the user picked it (not the
        /// contrast-darkened fill, which reads as muddy once it glows): the body, and the hot tint its
        /// head burns towards. Worked in OKLCH, so the body keeps the pick's hue and as much of its
        /// chroma as sRGB holds at a glowing lightness, and the head is the same hue, lighter, with
        /// chroma eased off.
        /// </summary>
        public static (Color Body, Color Head) CometColors(Color picked)
        {
            var palette = ColorMath.Palette(Rgb(picked), maintainContrast: true);
            return (FromRgb(palette.CometBody), FromRgb(palette.CometHead));
        }

        /// <summary>Everything the accent drives, in one native call: the effective accent, the
        /// comet, and the light, dark and dark-text ramps <c>AccentTheme</c> repaints the theme
        /// with.</summary>
        public static AccentColorPalette Palette(Color picked, bool maintainContrast)
        {
            var p = ColorMath.Palette(Rgb(picked), maintainContrast);
            return new AccentColorPalette(FromRgb(p.Effective), FromRgb(p.CometBody), FromRgb(p.CometHead),
                Ramp(p.Light), Ramp(p.Dark), Ramp(p.DarkText));
        }

        private static Color[] Ramp(ReadOnlyMemory<uint> ramp)
        {
            var colors = new Color[ramp.Length];
            for (var i = 0; i < colors.Length; i++)
                colors[i] = FromRgb(ramp.Span[i]);
            return colors;
        }

        private static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);

        private static Color FromRgb(uint rgb) => WithAlpha(0xFF, rgb);

        private static Color WithAlpha(byte alpha, uint rgb) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>One accent's palette as Avalonia colours (see <see cref="AccentColors.Palette"/>).
    /// Each ramp has ten steps with the base at step 5.</summary>
    public sealed record AccentColorPalette(Color Effective, Color CometBody, Color CometHead, Color[] Light, Color[] Dark, Color[] DarkText);
}
