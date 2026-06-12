using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace Clowd
{
    // System.Text.Json converters for external Avalonia value types, shared by the settings file
    // (SettingsService) and the graphics serializer (Clowd.Drawing). Every type maps to a single
    // JSON string — the per-property granularity of the undo diff in Clowd.Drawing depends on each
    // of these values staying a single leaf node.

    /// <summary>Color ↔ "#AARRGGBB".</summary>
    public sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Color.Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    /// <summary>Point ↔ "x,y".</summary>
    public sealed class PointJsonConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = JsonConverterUtil.SplitDoubles(reader.GetString(), 2);
            return new Point(v[0], v[1]);
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonConverterUtil.Join(value.X, value.Y));
    }

    /// <summary>Size ↔ "w,h".</summary>
    public sealed class SizeJsonConverter : JsonConverter<Size>
    {
        public override Size Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = JsonConverterUtil.SplitDoubles(reader.GetString(), 2);
            return new Size(v[0], v[1]);
        }

        public override void Write(Utf8JsonWriter writer, Size value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonConverterUtil.Join(value.Width, value.Height));
    }

    /// <summary>Rect ↔ "x,y,w,h".</summary>
    public sealed class RectJsonConverter : JsonConverter<Rect>
    {
        public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = JsonConverterUtil.SplitDoubles(reader.GetString(), 4);
            return new Rect(v[0], v[1], v[2], v[3]);
        }

        public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonConverterUtil.Join(value.X, value.Y, value.Width, value.Height));
    }

    /// <summary>PixelRect ↔ "x,y,w,h" (integers).</summary>
    public sealed class PixelRectJsonConverter : JsonConverter<PixelRect>
    {
        public override PixelRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = reader.GetString().Split(',');
            if (parts.Length != 4)
                throw new JsonException("Expected 4 comma-separated integers for PixelRect.");
            return new PixelRect(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        public override void Write(Utf8JsonWriter writer, PixelRect value, JsonSerializerOptions options) =>
            writer.WriteStringValue(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                                                  value.X, value.Y, value.Width, value.Height));
    }

    internal static class JsonConverterUtil
    {
        public static double[] SplitDoubles(string value, int count)
        {
            var parts = (value ?? "").Split(',');
            if (parts.Length != count)
                throw new JsonException($"Expected {count} comma-separated numbers, got \"{value}\".");

            var result = new double[count];
            for (int i = 0; i < count; i++)
                result[i] = double.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            return result;
        }

        public static string Join(params double[] values)
        {
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                parts[i] = values[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
    }
}
