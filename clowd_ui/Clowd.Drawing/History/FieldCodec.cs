using System;
using System.Collections.Generic;
using Avalonia;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// Per-field-type capture/compare/diff behavior for the history delta engine (final-design
    /// §B.1). A codec must satisfy two contracts:
    /// <list type="number">
    /// <item><b>Capture</b> deep-copies mutable reference values (List&lt;Point&gt;,
    /// ObscuredShape[]) so a stored record can never be mutated through the live graphic, and is
    /// also used when writing a record back into a live instance for the same reason.</item>
    /// <item><b>AreEqual/EmitPaths</b> must agree EXACTLY with what
    /// <see cref="UndoManager.GetChangedNodes"/> reports over the serialized JSON — the change-set
    /// grammar drives the undo merge decision, and the JSON diff remains the DEBUG parity oracle.
    /// That is why doubles compare bitwise (the serializer's shortest-round-trip text is injective
    /// on bit patterns, so 0.0 vs -0.0 is a JSON-visible change) and why list/array codecs emit
    /// the same positional "item.N" (and nested member) paths as DiffChildren.</item>
    /// </list>
    /// </summary>
    internal interface IFieldCodec
    {
        /// <summary>Snapshot of a field value; deep-copies mutable reference types.</summary>
        object Capture(object value);

        /// <summary>True when the serialized JSON of both values would be identical.</summary>
        bool AreEqual(object before, object after);

        /// <summary>
        /// Adds the changed-path set for this field, rooted at <paramref name="prefix"/>
        /// ("root/Graphics/&lt;id&gt;/&lt;jsonName&gt;"), matching DiffChildren's grammar.
        /// </summary>
        void EmitPaths(string prefix, object before, object after, SortedSet<string> changes);
    }

    internal static class FieldCodec
    {
        private static readonly IFieldCodec _scalar = new ScalarCodec();
        private static readonly IFieldCodec _double = new DoubleCodec();
        private static readonly IFieldCodec _point = new PointCodec();
        private static readonly IFieldCodec _size = new SizeCodec();
        private static readonly IFieldCodec _rect = new RectCodec();
        private static readonly IFieldCodec _pointList = new PointListCodec();
        private static readonly IFieldCodec _obscuredShapes = new ObscuredShapeArrayCodec();

        public static IFieldCodec ForType(Type fieldType)
        {
            if (fieldType == typeof(double)) return _double;
            if (fieldType == typeof(Point)) return _point;
            if (fieldType == typeof(Size)) return _size;
            if (fieldType == typeof(Rect)) return _rect;
            if (fieldType == typeof(List<Point>)) return _pointList;
            if (fieldType == typeof(GraphicImage.ObscuredShape[])) return _obscuredShapes;

            // every remaining persisted field type today (string, bool, int, Color, PixelRect,
            // font enums) serializes to a single leaf and has value/ordinal equality semantics.
            // A new mutable container type needs its own codec or history records would alias the
            // live value — fail loudly at map construction rather than corrupt silently.
            if (fieldType.IsArray ||
                (fieldType.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(fieldType) && fieldType != typeof(string)))
            {
                throw new InvalidOperationException(
                    $"No history field codec is registered for '{fieldType}'. Add one to FieldCodec.ForType " +
                    "(it must deep-copy on capture and reproduce the GetChangedNodes path grammar).");
            }

            return _scalar;
        }

        // bitwise: shortest-round-trip formatting is injective on non-NaN bit patterns, so this is
        // exactly "the serialized text differs" (unlike ==, which treats 0.0 and -0.0 as equal)
        internal static bool DoubleEquals(double a, double b) =>
            BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

        internal static bool PointEquals(Point a, Point b) =>
            DoubleEquals(a.X, b.X) && DoubleEquals(a.Y, b.Y);

        private sealed class ScalarCodec : IFieldCodec
        {
            public object Capture(object value) => value; // immutable (string/boxed value type)

            public bool AreEqual(object before, object after) => Equals(before, after);

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                if (!AreEqual(before, after))
                    changes.Add(prefix);
            }
        }

        private sealed class DoubleCodec : IFieldCodec
        {
            public object Capture(object value) => value;

            public bool AreEqual(object before, object after) => DoubleEquals((double)before, (double)after);

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                if (!AreEqual(before, after))
                    changes.Add(prefix);
            }
        }

        private sealed class PointCodec : IFieldCodec
        {
            public object Capture(object value) => value;

            public bool AreEqual(object before, object after) => PointEquals((Point)before, (Point)after);

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                if (!AreEqual(before, after))
                    changes.Add(prefix);
            }
        }

        private sealed class SizeCodec : IFieldCodec
        {
            public object Capture(object value) => value;

            public bool AreEqual(object before, object after)
            {
                var a = (Size)before;
                var b = (Size)after;
                return DoubleEquals(a.Width, b.Width) && DoubleEquals(a.Height, b.Height);
            }

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                if (!AreEqual(before, after))
                    changes.Add(prefix);
            }
        }

        private sealed class RectCodec : IFieldCodec
        {
            public object Capture(object value) => value;

            public bool AreEqual(object before, object after)
            {
                var a = (Rect)before;
                var b = (Rect)after;
                return DoubleEquals(a.X, b.X) && DoubleEquals(a.Y, b.Y) &&
                       DoubleEquals(a.Width, b.Width) && DoubleEquals(a.Height, b.Height);
            }

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                if (!AreEqual(before, after))
                    changes.Add(prefix);
            }
        }

        /// <summary>
        /// GraphicPolyLine._points. Serializes as a JSON array of "x,y" string leaves, which
        /// DiffChildren keys positionally: per-element changes are "prefix/item.N"; a length
        /// change reports one "item.N" per index present on only one side; a null↔instance
        /// transition is a structure change reported as the bare field path.
        /// </summary>
        private sealed class PointListCodec : IFieldCodec
        {
            public object Capture(object value) => value == null ? null : new List<Point>((List<Point>)value);

            public bool AreEqual(object before, object after)
            {
                var b = (List<Point>)before;
                var a = (List<Point>)after;
                if (b == null || a == null) return ReferenceEquals(b, a);
                if (b.Count != a.Count) return false;
                for (int i = 0; i < b.Count; i++)
                    if (!PointEquals(b[i], a[i]))
                        return false;
                return true;
            }

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                var b = (List<Point>)before;
                var a = (List<Point>)after;
                if (b == null || a == null)
                {
                    if (!ReferenceEquals(b, a))
                        changes.Add(prefix); // null ↔ [] / [..] is a structure change → one path
                    return;
                }

                int min = Math.Min(b.Count, a.Count);
                int max = Math.Max(b.Count, a.Count);
                for (int i = 0; i < min; i++)
                    if (!PointEquals(b[i], a[i]))
                        changes.Add(prefix + "/item." + i);
                for (int i = min; i < max; i++)
                    changes.Add(prefix + "/item." + i);
            }
        }

        /// <summary>
        /// GraphicImage._obscuredShapes. Each element serializes as an object (no "id" property →
        /// positional "item.N" key), so DiffChildren recurses per element into the record's
        /// serialized properties: P0..P3 ("x,y" leaves), BlurRadius and Mode (an enum string leaf).
        /// </summary>
        private sealed class ObscuredShapeArrayCodec : IFieldCodec
        {
            public object Capture(object value) =>
                value == null ? null : (GraphicImage.ObscuredShape[])((GraphicImage.ObscuredShape[])value).Clone();

            public bool AreEqual(object before, object after)
            {
                var b = (GraphicImage.ObscuredShape[])before;
                var a = (GraphicImage.ObscuredShape[])after;
                if (b == null || a == null) return ReferenceEquals(b, a);
                if (b.Length != a.Length) return false;
                for (int i = 0; i < b.Length; i++)
                    if (!ShapeEquals(b[i], a[i]))
                        return false;
                return true;
            }

            public void EmitPaths(string prefix, object before, object after, SortedSet<string> changes)
            {
                var b = (GraphicImage.ObscuredShape[])before;
                var a = (GraphicImage.ObscuredShape[])after;
                if (b == null || a == null)
                {
                    if (!ReferenceEquals(b, a))
                        changes.Add(prefix);
                    return;
                }

                int min = Math.Min(b.Length, a.Length);
                int max = Math.Max(b.Length, a.Length);
                for (int i = 0; i < min; i++)
                {
                    var itemPrefix = prefix + "/item." + i;
                    if (!PointEquals(b[i].P0, a[i].P0)) changes.Add(itemPrefix + "/P0");
                    if (!PointEquals(b[i].P1, a[i].P1)) changes.Add(itemPrefix + "/P1");
                    if (!PointEquals(b[i].P2, a[i].P2)) changes.Add(itemPrefix + "/P2");
                    if (!PointEquals(b[i].P3, a[i].P3)) changes.Add(itemPrefix + "/P3");
                    if (!DoubleEquals(b[i].BlurRadius, a[i].BlurRadius)) changes.Add(itemPrefix + "/BlurRadius");
                    if (b[i].Mode != a[i].Mode) changes.Add(itemPrefix + "/Mode");
                }

                for (int i = min; i < max; i++)
                    changes.Add(prefix + "/item." + i);
            }

            private static bool ShapeEquals(in GraphicImage.ObscuredShape x, in GraphicImage.ObscuredShape y) =>
                PointEquals(x.P0, y.P0) && PointEquals(x.P1, y.P1) && PointEquals(x.P2, y.P2) &&
                PointEquals(x.P3, y.P3) && DoubleEquals(x.BlurRadius, y.BlurRadius) && x.Mode == y.Mode;
        }
    }
}
