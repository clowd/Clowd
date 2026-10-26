using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.History;

namespace Clowd.Drawing
{
    /// <summary>
    /// System.Text.Json contract for graphics serialization (session persistence via the
    /// graphics.json file, the clipboard payload, and the autosave StateUpdated snapshots).
    ///
    /// The contract mirrors what the legacy (WPF-era) serializer used to persist:
    /// - <see cref="GraphicBase"/>-derived types serialize their instance FIELDS (public and
    ///   non-public, walking the hierarchy down from GraphicBase), so deserialization restores the
    ///   exact captured state without running property setters (which have side effects such as
    ///   re-normalizing text bounds or invalidating image caches).
    /// - Fields marked <see cref="TransientAttribute"/> are transient (selection/editing state,
    ///   cached bitmaps/geometry) and are excluded; they reset to their constructed defaults.
    /// - Instances are created via the protected parameterless constructor each graphic declares,
    ///   so field initializers still provide defaults for anything absent from the JSON.
    /// - Polymorphism uses a "$type" discriminator carrying the short type name; all public
    ///   concrete <see cref="GraphicBase"/> subclasses are registered automatically (the internal
    ///   GraphicSelectionRectangle is never serialized — GetGraphicList filters it out).
    /// - Avalonia value types (Color/Point/Size/Rect/PixelRect) and font enums serialize as single
    ///   strings, which keeps one JSON leaf per property — the per-property undo merge diff in
    ///   <see cref="UndoManager"/> depends on this shape.
    ///
    /// The field enumeration, JSON naming and compiled accessors live in
    /// <see cref="GraphicFieldMap"/> (final-design §B.1), which the history delta engine consumes
    /// too — one definition of "persisted field", so the two can never disagree.
    /// </summary>
    public static class GraphicsSerializer
    {
        public static JsonSerializerOptions Options { get; } = CreateOptions();

        // the registered "$type" names; the tolerant array converter drops elements whose
        // discriminator is not in this set (sessions saved by a build with graphic types that
        // no longer exist — e.g. the removed raster v1's GraphicRaster — must still load)
        private static readonly HashSet<string> _knownTypeNames =
            new HashSet<string>(ConcreteGraphicTypes().Select(t => t.Name), StringComparer.Ordinal);

        /// <summary>Serializes a graphics array to UTF-8 JSON bytes (clipboard payload).</summary>
        public static byte[] SerializeToUtf8Bytes(GraphicBase[] graphics) =>
            JsonSerializer.SerializeToUtf8Bytes(graphics, Options);

        /// <summary>Deserializes a graphics array from UTF-8 JSON bytes (clipboard payload).</summary>
        public static GraphicBase[] DeserializeFromUtf8Bytes(byte[] utf8Json) =>
            JsonSerializer.Deserialize<GraphicBase[]>(utf8Json, Options);

        private static JsonSerializerOptions CreateOptions()
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(ConfigureGraphicTypes);

            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = resolver,
            };

            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new ColorJsonConverter());
            options.Converters.Add(new PointJsonConverter());
            options.Converters.Add(new SizeJsonConverter());
            options.Converters.Add(new RectJsonConverter());
            options.Converters.Add(new PixelRectJsonConverter());
            options.Converters.Add(new TolerantGraphicArrayConverter());
            return options;
        }

        private static IEnumerable<Type> ConcreteGraphicTypes() =>
            typeof(GraphicBase).Assembly
                               .GetTypes()
                               .Where(t => t.IsPublic && !t.IsAbstract && typeof(GraphicBase).IsAssignableFrom(t))
                               .OrderBy(t => t.Name, StringComparer.Ordinal);

        private static void ConfigureGraphicTypes(JsonTypeInfo info)
        {
            if (info.Type == typeof(GraphicBase))
            {
                info.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };

                foreach (var t in ConcreteGraphicTypes())
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(t, t.Name));
            }

            if (info.Kind != JsonTypeInfoKind.Object || !typeof(GraphicBase).IsAssignableFrom(info.Type))
                return;

            // replace the default (public property) contract with the field-based one from the
            // shared field map — same slot order (base-most-first), names and compiled delegates
            // as always, so the serialized bytes are unchanged.
            var map = GraphicFieldMap.For(info.Type);

            info.Properties.Clear();
            foreach (var slot in map.Slots)
            {
                var prop = info.CreateJsonPropertyInfo(slot.FieldType, slot.JsonName);
                prop.Get = slot.Get;
                prop.Set = slot.Set;
                info.Properties.Add(prop);
            }

            if (!info.Type.IsAbstract)
                info.CreateObject = map.CreateObject;
        }

        /// <summary>
        /// Array-element tolerance for unknown "$type" discriminators: an element whose
        /// discriminator names a type that is no longer registered (e.g. "GraphicRaster" from a
        /// session saved by a build that had it) is dropped, and the remaining graphics load.
        /// Elements without a recognizable string discriminator fall through to the normal
        /// pipeline, so malformed JSON fails exactly as before. Writing is delegated element-wise
        /// to the polymorphic contract — serialized bytes are unchanged.
        /// </summary>
        private sealed class TolerantGraphicArrayConverter : JsonConverter<GraphicBase[]>
        {
            public override GraphicBase[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException($"Expected an array of graphics, found {reader.TokenType}.");

                var graphics = new List<GraphicBase>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    using var element = JsonDocument.ParseValue(ref reader);
                    var root = element.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("$type", out var discriminator) &&
                        discriminator.ValueKind == JsonValueKind.String &&
                        !_knownTypeNames.Contains(discriminator.GetString()))
                    {
                        continue; // unknown graphic type — drop this element, keep the rest
                    }

                    graphics.Add(root.Deserialize<GraphicBase>(options));
                }

                return graphics.ToArray();
            }

            public override void Write(Utf8JsonWriter writer, GraphicBase[] value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                foreach (var graphic in value)
                    JsonSerializer.Serialize(writer, graphic, options);
                writer.WriteEndArray();
            }
        }
    }
}
