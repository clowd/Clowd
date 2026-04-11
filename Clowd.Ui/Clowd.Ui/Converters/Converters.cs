using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Clowd.Ui.Converters;

public static class Converters
{
    public static readonly FuncValueConverter<Color, IBrush> ColorToBrush =
        new(c => new SolidColorBrush(c));

    public static readonly FuncValueConverter<Color, string> ColorToHex =
        new(c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
}
