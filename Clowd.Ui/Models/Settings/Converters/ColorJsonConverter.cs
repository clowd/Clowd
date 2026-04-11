using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace Clowd.Ui.Models.Settings.Converters;

/// <summary>
/// Serializes Avalonia.Media.Color as a hex string "#AARRGGBB".
/// </summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) return Colors.Transparent;
        return Color.Parse(s);
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
    }
}
