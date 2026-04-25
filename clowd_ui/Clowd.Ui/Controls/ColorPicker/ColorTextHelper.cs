using System;
using System.Globalization;
using Avalonia.Media;

namespace Clowd.Ui.Controls.ColorPicker;

/// <summary>
/// Static helpers for converting between Color/HslRgbColor and human-readable
/// strings (hex, rgb(), hsl()). Ported from Clowd WPF (Clowd.UI.Converters.ColorTextHelper).
/// </summary>
public static class ColorTextHelper
{
    public static string GetHsl(Color color) => GetHsl(HslRgbColor.FromColor(color));

    public static string GetHsl(HslRgbColor hsl)
    {
        if (hsl.Alpha >= 1)
            return string.Format(CultureInfo.InvariantCulture,
                "hsl({0:0}, {1:0}%, {2:0}%)",
                hsl.Hue, hsl.Saturation * 100, hsl.Lightness * 100);
        return string.Format(CultureInfo.InvariantCulture,
            "hsla({0:0}, {1:0}%, {2:0}%, {3})",
            hsl.Hue, hsl.Saturation * 100, hsl.Lightness * 100, Math.Round(hsl.Alpha, 2));
    }

    public static string GetRgb(Color color)
    {
        if (color.A == 255)
            return string.Format(CultureInfo.InvariantCulture,
                "rgb({0}, {1}, {2})", color.R, color.G, color.B);
        return string.Format(CultureInfo.InvariantCulture,
            "rgba({0}, {1}, {2}, {3})", color.R, color.G, color.B,
            Math.Round(color.A / 255d, 2));
    }

    public static string GetRgb(HslRgbColor color)
    {
        if (color.Alpha >= 1d)
            return string.Format(CultureInfo.InvariantCulture,
                "rgb({0}, {1}, {2})", color.R, color.G, color.B);
        return string.Format(CultureInfo.InvariantCulture,
            "rgba({0}, {1}, {2}, {3})", color.R, color.G, color.B,
            Math.Round(color.Alpha, 2));
    }

    public static string GetHex(Color color)
    {
        if (color.A == 255)
            return string.Format(CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        return string.Format(CultureInfo.InvariantCulture,
            "#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B, color.A);
    }

    public static string GetHex(HslRgbColor color)
    {
        if (color.Alpha >= 1d)
            return string.Format(CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        return string.Format(CultureInfo.InvariantCulture,
            "#{0:X2}{1:X2}{2:X2}{3:X2}", color.R, color.G, color.B,
            (int)Math.Round(color.Alpha * 255d));
    }

    public static Color FromHex(string s)
    {
        s = s.Trim().TrimStart('#');

        if (s.Length is not 6 and not 8)
            throw new InvalidOperationException("Invalid hex color");

        byte r = 0, g = 0, b = 0, a = 255;

        if (s.Length >= 6)
        {
            r = Convert.ToByte(s.Substring(0, 2), 16);
            g = Convert.ToByte(s.Substring(2, 2), 16);
            b = Convert.ToByte(s.Substring(4, 2), 16);
        }

        if (s.Length == 8)
        {
            a = Convert.ToByte(s.Substring(6, 2), 16);
        }

        return Color.FromArgb(a, r, g, b);
    }
}
