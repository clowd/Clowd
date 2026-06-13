using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    /// <summary>
    /// System.Text.Json contract for graphics serialization (undo snapshots, the graphics.json
    /// session file and the clipboard payload).
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
    /// </summary>
    public static class GraphicsSerializer
    {
        public static JsonSerializerOptions Options { get; } = CreateOptions();

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
            return options;
        }

        private static void ConfigureGraphicTypes(JsonTypeInfo info)
        {
            if (info.Type == typeof(GraphicBase))
            {
                info.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };

                var derived = typeof(GraphicBase).Assembly
                                                 .GetTypes()
                                                 .Where(t => t.IsPublic && !t.IsAbstract && typeof(GraphicBase).IsAssignableFrom(t))
                                                 .OrderBy(t => t.Name, StringComparer.Ordinal);
                foreach (var t in derived)
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(t, t.Name));
            }

            if (info.Kind != JsonTypeInfoKind.Object || !typeof(GraphicBase).IsAssignableFrom(info.Type))
                return;

            // replace the default (public property) contract with a field-based one. the contract
            // is built once per type but the accessors run on every undo snapshot / restore, so
            // FieldInfo.GetValue/SetValue reflection is replaced with compiled delegates.
            info.Properties.Clear();
            foreach (var field in EnumeratePersistedFields(info.Type))
            {
                var prop = info.CreateJsonPropertyInfo(field.FieldType, GetJsonName(field.Name));
                prop.Get = CompileGetter(field);
                prop.Set = CompileSetter(field);
                info.Properties.Add(prop);
            }

            if (!info.Type.IsAbstract)
            {
                // the graphics declare protected parameterless constructors for deserialization
                var ctor = info.Type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                                    null, Type.EmptyTypes, null)
                           ?? throw new InvalidOperationException($"{info.Type.Name} must declare a parameterless constructor.");
                info.CreateObject = Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile();
            }
        }

        private static Func<object, object> CompileGetter(FieldInfo field)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var body = Expression.Convert(Expression.Field(Expression.Convert(obj, field.DeclaringType), field), typeof(object));
            return Expression.Lambda<Func<object, object>>(body, obj).Compile();
        }

        private static Action<object, object> CompileSetter(FieldInfo field)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var value = Expression.Parameter(typeof(object), "value");
            var body = Expression.Assign(Expression.Field(Expression.Convert(obj, field.DeclaringType), field),
                                         Expression.Convert(value, field.FieldType));
            return Expression.Lambda<Action<object, object>>(body, obj, value).Compile();
        }

        /// <summary>
        /// All persisted instance fields for a graphic type, base-most class first
        /// (GraphicBase → ... → concrete type). Transient ([Transient]) and
        /// compiler-generated fields are excluded; statics are excluded by the binding flags.
        /// </summary>
        private static IEnumerable<FieldInfo> EnumeratePersistedFields(Type type)
        {
            var chain = new List<Type>();
            for (var t = type; t != null && t != typeof(SimpleNotifyObject); t = t.BaseType)
                chain.Add(t);
            chain.Reverse();

            foreach (var t in chain)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsDefined(typeof(TransientAttribute), false))
                        continue; // [Transient] = not persisted
                    if (f.Name.IndexOf('<') >= 0)
                        continue; // compiler-generated backing field
                    yield return f;
                }
            }
        }

        /// <summary>"_objectColor" → "objectColor" (lets the undo diff key graphics array items by
        /// their "id" property).</summary>
        private static string GetJsonName(string fieldName) => fieldName.TrimStart('_');
    }
}
