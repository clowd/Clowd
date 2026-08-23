using System;
using System.Diagnostics;
using Avalonia.Media;
using Microsoft.Win32;

namespace Clowd
{
    /// <summary>
    /// The accent color of the capture overlay (issue #48): either the color Windows itself is
    /// themed with, or one the user picked in the Capture settings page. Whichever it is, it is
    /// darkened until it has enough contrast with white — the overlay draws white labels and icons
    /// on top of accent-filled buttons, and a light accent leaves them unreadable.
    ///
    /// This is deliberately separate from <c>AppStyles.AccentColor</c>, which themes the rest of
    /// the C# UI and follows the Semi theme rather than the OS.
    /// </summary>
    public static class AccentColors
    {
        /// <summary>The legacy "clowd blue" the capturer has always been drawn in.</summary>
        public static readonly Color ClowdBlue = Color.FromRgb(0x3B, 0x97, 0xD2);

        /// <summary>WCAG AA for normal text (4.5:1), measured against the white glyphs the overlay
        /// draws on top of the accent.</summary>
        public const double MinimumContrastWithWhite = 4.5;

        /// <summary>Clowd blue taken down to <see cref="MinimumContrastWithWhite"/> (#2F7CAE). The
        /// default for the accent color setting, and mirrored by the capturer's own
        /// <c>--accent-color</c> default (clowd_capture/src/settings.rs).</summary>
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
