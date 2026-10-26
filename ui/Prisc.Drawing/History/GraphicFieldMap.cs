using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// The single source of truth for "persisted graphic field" (final-design §B.1), factored out
    /// of <see cref="GraphicsSerializer"/> so the System.Text.Json contract and the history delta
    /// codecs can never disagree:
    /// - instance fields (public and non-public), walking the hierarchy base-most-first from
    ///   GraphicBase down to (but not including) SimpleNotifyObject;
    /// - fields marked [Transient] and compiler-generated backing fields excluded; statics
    ///   excluded by binding flags;
    /// - JSON name = field name with the leading '_' trimmed ("_objectColor" → "objectColor");
    ///   the "id" name is load-bearing for the undo diff's per-graphic keying;
    /// - accessors are compiled expression delegates (run per history capture / undo snapshot);
    /// - instances are created via each type's protected parameterless constructor.
    /// The serializer consumes <see cref="Slots"/> verbatim, so its output stays byte-identical.
    /// </summary>
    internal sealed class GraphicFieldMap
    {
        public Type Type { get; }

        /// <summary>Short type name — the "$type" discriminator value.</summary>
        public string TypeName { get; }

        /// <summary>Persisted field slots, base-most class first (the serialized property order).</summary>
        public FieldSlot[] Slots { get; }

        /// <summary>Compiled parameterless constructor; null for abstract types.</summary>
        public Func<object> CreateObject { get; }

        private static readonly ConcurrentDictionary<Type, GraphicFieldMap> _maps =
            new ConcurrentDictionary<Type, GraphicFieldMap>();

        public static GraphicFieldMap For(Type type) => _maps.GetOrAdd(type, t => new GraphicFieldMap(t));

        private GraphicFieldMap(Type type)
        {
            Type = type;
            TypeName = type.Name;

            var slots = new List<FieldSlot>();
            foreach (var field in EnumeratePersistedFields(type))
                slots.Add(new FieldSlot(GetJsonName(field.Name), field.FieldType,
                                        CompileGetter(field), CompileSetter(field),
                                        FieldCodec.ForType(field.FieldType)));
            Slots = slots.ToArray();

            if (!type.IsAbstract)
            {
                // the graphics declare protected parameterless constructors for deserialization
                var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                               null, Type.EmptyTypes, null)
                           ?? throw new InvalidOperationException($"{type.Name} must declare a parameterless constructor.");
                CreateObject = Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile();
            }
        }

        /// <summary>
        /// Captures the current persisted field values of <paramref name="graphic"/> as a record
        /// aligned with <see cref="Slots"/>. Mutable values (point lists, obscured-shape arrays)
        /// are deep-copied so the record is immune to later edits of the live instance.
        /// </summary>
        public object[] Capture(GraphicBase graphic)
        {
            var values = new object[Slots.Length];
            for (int i = 0; i < Slots.Length; i++)
                values[i] = Slots[i].Codec.Capture(Slots[i].Get(graphic));
            return values;
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

    /// <summary>One persisted field: JSON name, compiled accessors and its history codec.</summary>
    internal readonly struct FieldSlot
    {
        public readonly string JsonName;
        public readonly Type FieldType;
        public readonly Func<object, object> Get;
        public readonly Action<object, object> Set;
        public readonly IFieldCodec Codec;

        public FieldSlot(string jsonName, Type fieldType, Func<object, object> get, Action<object, object> set, IFieldCodec codec)
        {
            JsonName = jsonName;
            FieldType = fieldType;
            Get = get;
            Set = set;
            Codec = codec;
        }
    }
}
