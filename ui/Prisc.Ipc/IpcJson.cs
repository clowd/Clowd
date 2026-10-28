using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Prisc.Ipc
{
    /// <summary>Reads one JSON value at the reader's current token.</summary>
    internal delegate T JsonRead<T>(ref Utf8JsonReader reader);

    /// <summary>The primitives the generated codecs are written in (HELPER_PROTOCOL.md §2.1, §6). A missing key and
    /// <c>null</c> read the same: the type's default (null for strings, records and lists' absence; empty lists
    /// stay empty). Unknown properties are skipped.</summary>
    internal static class IpcJson
    {
        // Non-ASCII and + < > & ' go out literally, as serde_json writes them.
        private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>The object's <c>type</c> string, wherever it is in the object; null when it has none.</summary>
        public static string ReadType(ReadOnlySpan<byte> utf8)
        {
            var r = new Utf8JsonReader(utf8);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                throw new JsonException("not a JSON object");
            while (NextProperty(ref r, out var name))
            {
                if (name == "type")
                    return r.TokenType == JsonTokenType.String ? r.GetString() : null;
                r.Skip();
            }
            return null;
        }

        /// <summary>A reader positioned on the object's start, ready for <see cref="NextProperty"/>.</summary>
        public static Utf8JsonReader Open(ReadOnlySpan<byte> utf8)
        {
            var r = new Utf8JsonReader(utf8);
            r.Read();
            return r;
        }

        /// <summary>True when the reader is on an object's start; false for null. Anything else is malformed.</summary>
        public static bool EnterObject(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.Null)
                return false;
            if (r.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"expected an object, got {r.TokenType}");
            return true;
        }

        /// <summary>Advances to the next property and onto its value; false at the object's end.</summary>
        public static bool NextProperty(ref Utf8JsonReader r, out string name)
        {
            if (!r.Read())
                throw new JsonException("truncated object");
            if (r.TokenType == JsonTokenType.EndObject)
            {
                name = null;
                return false;
            }
            name = r.GetString();
            if (!r.Read())
                throw new JsonException("truncated object");
            return true;
        }

        public static bool ReadBool(ref Utf8JsonReader r) => r.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False or JsonTokenType.Null => false,
            _ => throw Unexpected(ref r, "a bool"),
        };

        public static uint ReadUInt32(ref Utf8JsonReader r) =>
            r.TokenType == JsonTokenType.Null ? 0 : r.TokenType == JsonTokenType.Number && r.TryGetUInt32(out var v) ? v : throw Unexpected(ref r, "a u32");

        public static ulong ReadUInt64(ref Utf8JsonReader r) =>
            r.TokenType == JsonTokenType.Null ? 0 : r.TokenType == JsonTokenType.Number && r.TryGetUInt64(out var v) ? v : throw Unexpected(ref r, "a u64");

        public static int ReadInt32(ref Utf8JsonReader r) =>
            r.TokenType == JsonTokenType.Null ? 0 : r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var v) ? v : throw Unexpected(ref r, "an i32");

        public static long ReadInt64(ref Utf8JsonReader r) =>
            r.TokenType == JsonTokenType.Null ? 0 : r.TokenType == JsonTokenType.Number && r.TryGetInt64(out var v) ? v : throw Unexpected(ref r, "an i64");

        public static double ReadDouble(ref Utf8JsonReader r) =>
            r.TokenType == JsonTokenType.Null ? 0 : r.TokenType == JsonTokenType.Number && r.TryGetDouble(out var v) ? v : throw Unexpected(ref r, "an f64");

        public static string ReadString(ref Utf8JsonReader r) => r.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => r.GetString(),
            _ => throw Unexpected(ref r, "a string"),
        };

        private static JsonException Unexpected(ref Utf8JsonReader r, string expected) =>
            new($"expected {expected}, got {r.TokenType} at byte {r.TokenStartIndex}");

        public static T? ReadOptional<T>(ref Utf8JsonReader r, JsonRead<T> read) where T : struct =>
            r.TokenType == JsonTokenType.Null ? null : read(ref r);

        public static IReadOnlyList<T> ReadList<T>(ref Utf8JsonReader r, JsonRead<T> read)
        {
            if (r.TokenType == JsonTokenType.Null)
                return Array.Empty<T>();
            if (r.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"expected an array, got {r.TokenType}");
            var items = new List<T>();
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                items.Add(read(ref r));
            return items;
        }

        /// <summary>Floats are always finite on the wire: NaN and infinities go out as 0.</summary>
        public static void WriteDoubleValue(Utf8JsonWriter w, double value) => w.WriteNumberValue(double.IsFinite(value) ? value : 0);

        /// <summary>One object, written by <paramref name="body"/> between its braces, as a line without the newline.</summary>
        public static string Write(Action<Utf8JsonWriter> body)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
