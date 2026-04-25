using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Clowd.Drawing;

namespace Clowd.Ui.Converters;

public static class Converters
{
    public static readonly FuncValueConverter<Color, IBrush> ColorToBrush =
        new(c => new SolidColorBrush(c));

    public static readonly FuncValueConverter<Color, string> ColorToHex =
        new(c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");

    /// <summary>
    /// True when the bound <see cref="Skill"/> flags include the requested flag
    /// (passed as the converter parameter, either as a string name or a Skill).
    /// Used by the editor's top toolbar to show/hide tool-specific property
    /// editors based on <c>DrawingCanvas.SubjectSkill</c>.
    /// </summary>
    public static readonly SkillFlagConverter SkillFlag = new();

    /// <summary>
    /// True when the bound <see cref="ToolType"/> equals the parameter
    /// (passed as a string name or a ToolType). Used to highlight the
    /// active tool button in the left rail.
    /// </summary>
    public static readonly ToolEqualityConverter ToolEquals = new();
}

public sealed class SkillFlagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Skill subject) return false;
        var flag = ParseSkill(parameter);
        return (subject & flag) == flag && flag != Skill.None;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Skill ParseSkill(object? p)
    {
        return p switch
        {
            Skill s => s,
            string str when Enum.TryParse<Skill>(str, ignoreCase: true, out var s) => s,
            _ => Skill.None,
        };
    }
}

public sealed class ToolEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ToolType current) return false;
        return parameter switch
        {
            ToolType t => current == t,
            string str when Enum.TryParse<ToolType>(str, ignoreCase: true, out var t) => current == t,
            _ => false,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
