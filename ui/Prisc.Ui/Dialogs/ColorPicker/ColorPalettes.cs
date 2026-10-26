using Avalonia.Media;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's standard swatches, a 12 x 5 grid laid out as one: every column is a hue,
    /// so a color's other versions sit directly above and below it. The top row is the grays;
    /// under it each hue runs tint, pure, deep, then half-transparent.
    /// </summary>
    /// <remarks>
    /// How the values were made, should the grid ever need regenerating: the pure hues are
    /// full-saturation sRGB at hand-picked angles (0, 30, 45, 60, 90, 120, 165, 195, 215, 240,
    /// 275, 315), spaced to look even rather than to be even; the tints and shades are derived in
    /// OKLab, so each keeps its column's hue as it lightens or darkens.
    /// </remarks>
    public static class ColorPalettes
    {
        /// <summary>The swatches in reading order, row by row; the dialog wraps them at 12.</summary>
        public static Color[] StandardSwatches => new[]
        {
            // grays, black to white in even steps of perceived lightness (CIE L*)
            Color.FromRgb(0, 0, 0),
            Color.FromRgb(26, 26, 26),
            Color.FromRgb(44, 44, 44),
            Color.FromRgb(64, 64, 64),
            Color.FromRgb(86, 86, 86),
            Color.FromRgb(108, 108, 108),
            Color.FromRgb(130, 130, 130),
            Color.FromRgb(154, 154, 154),
            Color.FromRgb(178, 178, 178),
            Color.FromRgb(203, 203, 203),
            Color.FromRgb(229, 229, 229),
            Color.FromRgb(255, 255, 255),

            // tints: each hue mixed 60% of the way to white (in OKLab)
            Color.FromRgb(255, 181, 167),
            Color.FromRgb(255, 207, 174),
            Color.FromRgb(255, 231, 182),
            Color.FromRgb(255, 255, 190),
            Color.FromRgb(209, 255, 186),
            Color.FromRgb(191, 255, 185),
            Color.FromRgb(193, 255, 229),
            Color.FromRgb(182, 231, 255),
            Color.FromRgb(163, 200, 255),
            Color.FromRgb(143, 182, 255),
            Color.FromRgb(209, 179, 255),
            Color.FromRgb(255, 181, 230),

            // the pure hues, fully saturated: red, orange, amber, yellow, lime, green, aqua, sky, azure, blue, violet, magenta
            Color.FromRgb(255, 0, 0),
            Color.FromRgb(255, 128, 0),
            Color.FromRgb(255, 191, 0),
            Color.FromRgb(255, 255, 0),
            Color.FromRgb(128, 255, 0),
            Color.FromRgb(0, 255, 0),
            Color.FromRgb(0, 255, 191),
            Color.FromRgb(0, 191, 255),
            Color.FromRgb(0, 106, 255),
            Color.FromRgb(0, 0, 255),
            Color.FromRgb(149, 0, 255),
            Color.FromRgb(255, 0, 191),

            // deep shades: each hue darkened in OKLab, keeping its hue
            Color.FromRgb(125, 0, 0),
            Color.FromRgb(101, 47, 0),
            Color.FromRgb(82, 60, 0),
            Color.FromRgb(67, 67, 0),
            Color.FromRgb(33, 76, 0),
            Color.FromRgb(0, 79, 0),
            Color.FromRgb(0, 76, 55),
            Color.FromRgb(0, 70, 96),
            Color.FromRgb(0, 49, 128),
            Color.FromRgb(0, 19, 115),
            Color.FromRgb(72, 0, 128),
            Color.FromRgb(114, 0, 84),

            // the pure hues at half opacity, as the old palette had them (a highlighter's colors)
            Color.FromArgb(128, 255, 0, 0),
            Color.FromArgb(128, 255, 128, 0),
            Color.FromArgb(128, 255, 191, 0),
            Color.FromArgb(128, 255, 255, 0),
            Color.FromArgb(128, 128, 255, 0),
            Color.FromArgb(128, 0, 255, 0),
            Color.FromArgb(128, 0, 255, 191),
            Color.FromArgb(128, 0, 191, 255),
            Color.FromArgb(128, 0, 106, 255),
            Color.FromArgb(128, 0, 0, 255),
            Color.FromArgb(128, 149, 0, 255),
            Color.FromArgb(128, 255, 0, 191),
        };
    }
}
