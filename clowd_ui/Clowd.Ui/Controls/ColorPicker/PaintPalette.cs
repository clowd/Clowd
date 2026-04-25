using Avalonia.Media;

namespace Clowd.Ui.Controls.ColorPicker;

/// <summary>
/// 96-color paint palette ported from Clowd WPF (ColorPalettes.PaintPalette).
/// 64 opaque rows (4 × 16) followed by 32 semi-transparent rows (2 × 16) — fits a 16-column × 6-row WrapPanel.
/// </summary>
public static class PaintPalette
{
    public static readonly Color[] Colors = new[]
    {
        // Row 1 — fully saturated rainbow
        Color.FromArgb(255, 0, 0, 0),
        Color.FromArgb(255, 64, 64, 64),
        Color.FromArgb(255, 255, 0, 0),
        Color.FromArgb(255, 255, 106, 0),
        Color.FromArgb(255, 255, 216, 0),
        Color.FromArgb(255, 182, 255, 0),
        Color.FromArgb(255, 76, 255, 0),
        Color.FromArgb(255, 0, 255, 33),
        Color.FromArgb(255, 0, 255, 144),
        Color.FromArgb(255, 0, 255, 255),
        Color.FromArgb(255, 0, 148, 255),
        Color.FromArgb(255, 0, 38, 255),
        Color.FromArgb(255, 72, 0, 255),
        Color.FromArgb(255, 178, 0, 255),
        Color.FromArgb(255, 255, 0, 220),
        Color.FromArgb(255, 255, 0, 110),

        // Row 2 — half-saturated rainbow
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 128, 128, 128),
        Color.FromArgb(255, 127, 0, 0),
        Color.FromArgb(255, 127, 51, 0),
        Color.FromArgb(255, 127, 106, 0),
        Color.FromArgb(255, 91, 127, 0),
        Color.FromArgb(255, 38, 127, 0),
        Color.FromArgb(255, 0, 127, 14),
        Color.FromArgb(255, 0, 127, 70),
        Color.FromArgb(255, 0, 127, 127),
        Color.FromArgb(255, 0, 74, 127),
        Color.FromArgb(255, 0, 19, 127),
        Color.FromArgb(255, 33, 0, 127),
        Color.FromArgb(255, 87, 0, 127),
        Color.FromArgb(255, 127, 0, 110),
        Color.FromArgb(255, 127, 0, 55),

        // Row 3 — pastel rainbow
        Color.FromArgb(255, 160, 160, 160),
        Color.FromArgb(255, 48, 48, 48),
        Color.FromArgb(255, 255, 127, 127),
        Color.FromArgb(255, 255, 178, 127),
        Color.FromArgb(255, 255, 233, 127),
        Color.FromArgb(255, 218, 255, 127),
        Color.FromArgb(255, 165, 255, 127),
        Color.FromArgb(255, 127, 255, 142),
        Color.FromArgb(255, 127, 255, 197),
        Color.FromArgb(255, 127, 255, 255),
        Color.FromArgb(255, 127, 201, 255),
        Color.FromArgb(255, 127, 146, 255),
        Color.FromArgb(255, 161, 127, 255),
        Color.FromArgb(255, 214, 127, 255),
        Color.FromArgb(255, 255, 127, 237),
        Color.FromArgb(255, 255, 127, 182),

        // Row 4 — muted rainbow
        Color.FromArgb(255, 192, 192, 192),
        Color.FromArgb(255, 96, 96, 96),
        Color.FromArgb(255, 127, 63, 63),
        Color.FromArgb(255, 127, 89, 63),
        Color.FromArgb(255, 127, 116, 63),
        Color.FromArgb(255, 109, 127, 63),
        Color.FromArgb(255, 82, 127, 63),
        Color.FromArgb(255, 63, 127, 71),
        Color.FromArgb(255, 63, 127, 98),
        Color.FromArgb(255, 63, 127, 127),
        Color.FromArgb(255, 63, 100, 127),
        Color.FromArgb(255, 63, 73, 127),
        Color.FromArgb(255, 80, 63, 127),
        Color.FromArgb(255, 107, 63, 127),
        Color.FromArgb(255, 127, 63, 118),
        Color.FromArgb(255, 127, 63, 91),

        // Row 5 — semi-transparent rainbow (alpha 128)
        Color.FromArgb(128, 0, 0, 0),
        Color.FromArgb(128, 64, 64, 64),
        Color.FromArgb(128, 255, 0, 0),
        Color.FromArgb(128, 255, 106, 0),
        Color.FromArgb(128, 255, 216, 0),
        Color.FromArgb(128, 182, 255, 0),
        Color.FromArgb(128, 76, 255, 0),
        Color.FromArgb(128, 0, 255, 33),
        Color.FromArgb(128, 0, 255, 144),
        Color.FromArgb(128, 0, 255, 255),
        Color.FromArgb(128, 0, 148, 255),
        Color.FromArgb(128, 0, 38, 255),
        Color.FromArgb(128, 72, 0, 255),
        Color.FromArgb(128, 178, 0, 255),
        Color.FromArgb(128, 255, 0, 220),
        Color.FromArgb(128, 255, 0, 110),

        // Row 6 — semi-transparent muted (first slot fully transparent)
        Color.FromArgb(0, 255, 255, 255),
        Color.FromArgb(128, 128, 128, 128),
        Color.FromArgb(128, 127, 0, 0),
        Color.FromArgb(128, 127, 51, 0),
        Color.FromArgb(128, 127, 106, 0),
        Color.FromArgb(128, 91, 127, 0),
        Color.FromArgb(128, 38, 127, 0),
        Color.FromArgb(128, 0, 127, 14),
        Color.FromArgb(128, 0, 127, 70),
        Color.FromArgb(128, 0, 127, 127),
        Color.FromArgb(128, 0, 74, 127),
        Color.FromArgb(128, 0, 19, 127),
        Color.FromArgb(128, 33, 0, 127),
        Color.FromArgb(128, 87, 0, 127),
        Color.FromArgb(128, 127, 0, 110),
        Color.FromArgb(128, 127, 0, 55),
    };
}
