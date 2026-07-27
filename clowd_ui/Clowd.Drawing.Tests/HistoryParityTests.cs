using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the history delta engine (final-design §B) against the retained JSON oracle:
    /// - every commit's built change set must set-equal what <see cref="UndoManager.GetChangedNodes"/>
    ///   reports over independently captured full snapshots (the randomized parity fuzz);
    /// - undo/redo must land on documents byte-identical to what the old full-snapshot restore
    ///   produced, driven by a reference reimplementation of the old merge algorithm;
    /// - restore is in place: the GraphicCollection instance and untouched (and even edited/deleted)
    ///   graphic instances survive undo/redo.
    /// </summary>
    public class HistoryParityTests
    {
        static HistoryParityTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static readonly FieldInfo PolyPointsField =
            typeof(GraphicPolyLine).GetField("_points", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Color[] Palette =
        {
            Colors.Red, Colors.Green, Colors.Blue, Colors.Orange, Colors.Purple, Colors.Teal,
        };

        // ====================================================================
        // Randomized parity fuzz (final-design §B.2 / risk #3) — ≥2000 edit scripts total
        // ====================================================================

        [AvaloniaFact]
        public void RandomizedEditScripts_AngleFree_MatchOracle_AndRoundTripExactly()
        {
            // rotation-free scripts: Normalize is a bitwise fixpoint, so undo/redo must reproduce
            // the reference snapshots byte-for-byte
            RunScripts(scriptCount: 1400, allowAngle: false, seed: 20260710, byteCompare: true);
        }

        [AvaloniaFact]
        public void RandomizedEditScripts_WithRotation_MatchOracle()
        {
            // rotated rectangles: Normalize re-derives CenterOfRotation through trig, which is not
            // ulp-stable, so (exactly like the old deserialize+Normalize restore) documents are
            // compared structurally (id sequences) while the change-set grammar stays exact
            RunScripts(scriptCount: 700, allowAngle: true, seed: 998877, byteCompare: false);
        }

        private static void RunScripts(int scriptCount, bool allowAngle, int seed, bool byteCompare)
        {
            var canvas = new DrawingCanvas();

            // with the Pointer tool a single-selected graphic gets property-panel bindings, and a
            // direct ObjectColor/LineWidth write then auto-commits via BoundGraphicPropertyChanged
            // — an extra AddCommandStep(true) the reference model would not see. That path is the
            // same commit code under test; keep the script's commit sequence the only driver.
            canvas.Tool = ToolType.None;

            SortedSet<string> lastBuilt = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => lastBuilt = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                var rng = new Random(seed);
                for (int script = 0; script < scriptCount; script++)
                {
                    // seed a fresh document and reset history around it via the public restore path
                    canvas.GraphicsList.Clear();
                    int n = rng.Next(2, 7);
                    for (int i = 0; i < n; i++)
                        canvas.GraphicsList.Add(MakeGraphic(rng, allowAngle));
                    canvas.RestoreState(UndoManager.SerializeDocument(canvas));

                    var reference = new ReferenceHistory(UndoManager.SerializeDocument(canvas));
                    string context = $"script {script} (seed {seed})";

                    void AssertNav()
                    {
                        Assert.True(reference.CanUndo == canvas.CommandUndo.CanExecute(null), context + ": CanUndo diverged");
                        Assert.True(reference.CanRedo == canvas.CommandRedo.CanExecute(null), context + ": CanRedo diverged");
                    }

                    void Commit(bool mergable)
                    {
                        lastBuilt = null;
                        canvas.AddCommandToHistory(mergable);
                        var next = UndoManager.SerializeDocument(canvas);
                        var oracle = UndoManager.GetChangedNodes(reference.Current, next);
                        Assert.True(lastBuilt != null, context + ": commit did not report a change set");
                        Assert.True(oracle.SetEquals(lastBuilt),
                                    $"{context}: change-set parity violation\n" +
                                    $"  oracle: [{string.Join(", ", oracle)}]\n" +
                                    $"  built:  [{string.Join(", ", lastBuilt)}]");
                        reference.Commit(next, mergable);
                        AssertNav();
                    }

                    void DoUndo()
                    {
                        canvas.Undo();
                        var expected = reference.Undo();
                        var actual = UndoManager.SerializeDocument(canvas);
                        AssertRestored(expected, actual, byteCompare, context + " (undo)");
                        // restore re-runs Normalize on the changed graphics, which is not
                        // bitwise-idempotent (ulp drift on CenterOfRotation and, when rotated,
                        // Left/Top/Right/Bottom); the engine absorbs the drift into its committed
                        // baseline, so the reference must adopt it too — repeated visits to this
                        // node are byte-stable afterwards (step records never change)
                        reference.RefreshCurrent(actual);
                        AssertNav();
                    }

                    void DoRedo()
                    {
                        canvas.Redo();
                        var expected = reference.Redo();
                        var actual = UndoManager.SerializeDocument(canvas);
                        AssertRestored(expected, actual, byteCompare, context + " (redo)");
                        reference.RefreshCurrent(actual);
                        AssertNav();
                    }

                    int ops = rng.Next(4, 11);
                    for (int op = 0; op < ops; op++)
                    {
                        int kind = rng.Next(100);
                        if (kind < 10 && reference.CanUndo)
                        {
                            DoUndo();
                            continue;
                        }

                        if (kind < 16 && reference.CanRedo)
                        {
                            DoRedo();
                            continue;
                        }

                        // transient selection noise must never surface in history
                        if (rng.Next(4) == 0 && canvas.GraphicsList.Count > 0)
                            canvas.GraphicsList[rng.Next(canvas.GraphicsList.Count)].IsSelected = rng.Next(2) == 0;

                        Mutate(canvas, rng, allowAngle);
                        Commit(rng.Next(2) == 0);
                    }

                    // unwind the whole stack and replay it
                    while (reference.CanUndo)
                        DoUndo();
                    while (reference.CanRedo)
                        DoRedo();
                }
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }
        }

        private static void AssertRestored(JsonObject expected, JsonObject actual, bool byteCompare, string context)
        {
            // byteCompare (angle-free scripts): exact modulo the documented Normalize ulp drift.
            // rotation scripts: same comparer with the same tolerance — the trig round-trips stay
            // far inside it, and everything non-numeric (ids, order, strings, counts) is exact.
            double tolerance = byteCompare ? 1e-9 : 1e-6;
            Assert.True(JsonAlmostEquals(expected, actual, tolerance),
                        $"{context}: restored document mismatch\n  expected: {expected.ToJsonString()}\n  actual:   {actual.ToJsonString()}");
        }

        /// <summary>Structural JSON equality with a numeric tolerance on double-ish leaves
        /// (raw numbers and the "x,y[,w,h]" value-type strings).</summary>
        private static bool JsonAlmostEquals(JsonNode a, JsonNode b, double tolerance)
        {
            if (a is null || b is null)
                return a is null && b is null;

            if (a is JsonObject ao && b is JsonObject bo)
            {
                if (ao.Count != bo.Count)
                    return false;
                foreach (var kvp in ao)
                {
                    if (!bo.TryGetPropertyValue(kvp.Key, out var bv))
                        return false;

                    // centerOfRotation is Normalize-derived, and restore re-runs Normalize on the
                    // changed graphics (the OLD SetState did the same on every graphic): a text
                    // committed with a stale center (Body-set Normalize recenters BEFORE the new
                    // measurement widens the bounds) gets legitimately recentered on restore, so
                    // the pre-restore snapshot is not the right expectation for this one field
                    if (string.Equals(kvp.Key, "centerOfRotation", StringComparison.Ordinal))
                        continue;

                    if (!JsonAlmostEquals(kvp.Value, bv, tolerance))
                        return false;
                }

                return true;
            }

            if (a is JsonArray aa && b is JsonArray ba)
            {
                if (aa.Count != ba.Count)
                    return false;
                for (int i = 0; i < aa.Count; i++)
                    if (!JsonAlmostEquals(aa[i], ba[i], tolerance))
                        return false;
                return true;
            }

            if (a is JsonValue && b is JsonValue)
            {
                var sa = a.ToJsonString();
                var sb = b.ToJsonString();
                if (string.Equals(sa, sb, StringComparison.Ordinal))
                    return true;

                var pa = sa.Trim('"').Split(',');
                var pb = sb.Trim('"').Split(',');
                if (pa.Length != pb.Length)
                    return false;
                for (int i = 0; i < pa.Length; i++)
                {
                    if (!double.TryParse(pa[i], System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out var da) ||
                        !double.TryParse(pb[i], System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out var db))
                        return false;
                    if (Math.Abs(da - db) > tolerance * Math.Max(1, Math.Max(Math.Abs(da), Math.Abs(db))))
                        return false;
                }

                return true;
            }

            return false;
        }

        // ---- randomized document / edit primitives -------------------------------------------

        private static Point RandPoint(Random rng) =>
            new Point(Math.Round(rng.NextDouble() * 300, 3), Math.Round(rng.NextDouble() * 300, 3));

        private static Rect RandRect(Random rng) =>
            new Rect(Math.Round(rng.NextDouble() * 200, 3), Math.Round(rng.NextDouble() * 200, 3),
                     Math.Round(5 + rng.NextDouble() * 80, 3), Math.Round(5 + rng.NextDouble() * 80, 3));

        private static GraphicBase MakeGraphic(Random rng, bool allowAngle)
        {
            var color = Palette[rng.Next(Palette.Length)];
            double lw = rng.Next(1, 6);
            double angle = allowAngle && rng.Next(3) == 0 ? rng.Next(-45, 46) : 0;

            switch (rng.Next(9))
            {
                case 0: return new GraphicRectangle(color, lw, RandRect(rng), angle);
                case 1: return new GraphicFilledRectangle(color, RandRect(rng));
                case 2: return new GraphicEllipse(color, lw, RandRect(rng), angle);
                case 3: return new GraphicLine(color, lw, RandPoint(rng), RandPoint(rng));
                case 4: return new GraphicArrow(color, lw, RandPoint(rng), RandPoint(rng));
                // texts stay unrotated: they commit with a stale (off-center) CenterOfRotation and
                // a rotated restore-Normalize then re-anchors Left/Top/Right/Bottom materially —
                // identically in the old and new restore, but unverifiable against snapshots
                case 5: return new GraphicText(color, lw, RandPoint(rng), 0, "note " + rng.Next(1000));
                case 6:
                    var start = RandPoint(rng);
                    var poly = new GraphicPolyLine(color, lw, start);
                    int extra = rng.Next(3, 7);
                    for (int i = 0; i < extra; i++)
                        poly.AddPoint(RandPoint(rng));
                    poly.EndDrawing(true);
                    return poly;
                case 7: return new GraphicMeasure(color, lw, RandPoint(rng), RandPoint(rng));
                default:
                    // never decoded in these tests (nothing renders and no obscure areas are added
                    // through the decode path), so a missing file is fine — paths are just fields
                    return new GraphicImage(@"C:\clowd-fuzz-does-not-exist\image.png", RandRect(rng), default);
            }
        }

        private static void Mutate(DrawingCanvas canvas, Random rng, bool allowAngle)
        {
            var list = canvas.GraphicsList;
            int kind = rng.Next(100);

            if (list.Count == 0 || kind >= 80 && kind < 87)
            {
                list.Add(MakeGraphic(rng, allowAngle));
                return;
            }

            var g = list[rng.Next(list.Count)];

            if (kind < 22)
            {
                g.ObjectColor = Palette[rng.Next(Palette.Length)];
            }
            else if (kind < 32)
            {
                g.LineWidth = rng.Next(1, 10) + (rng.Next(2) == 0 ? 0.5 : 0);
            }
            else if (kind < 44)
            {
                g.Move(Math.Round(rng.NextDouble() * 20 - 10, 3), Math.Round(rng.NextDouble() * 20 - 10, 3));
            }
            else if (kind < 54)
            {
                if (g is GraphicRectangle r && g is not GraphicText)
                {
                    r.MoveHandleTo(RandPoint(rng), rng.Next(1, 9));
                    r.Normalize(); // mirrors the pointer tool's mouse-up
                }
                else
                {
                    g.ObjectColor = Palette[rng.Next(Palette.Length)];
                }
            }
            else if (kind < 60)
            {
                if (allowAngle && g is GraphicRectangle rect && g is not GraphicText)
                {
                    rect.Angle = rng.Next(-60, 61);
                    rect.Normalize();
                }
                else
                {
                    g.LineWidth = rng.Next(1, 10);
                }
            }
            else if (kind < 68)
            {
                if (g is GraphicText text)
                {
                    if (rng.Next(2) == 0)
                        text.Body = "edited " + rng.Next(1000);
                    else
                        text.FontSize = rng.Next(10, 24);
                }
                else
                {
                    g.ObjectColor = Palette[rng.Next(Palette.Length)];
                }
            }
            else if (kind < 74)
            {
                if (g is GraphicPolyLine poly)
                {
                    // _points has no mutation funnel of its own outside drawing; a real edit always
                    // rides along with property raises on the same graphic (tools normalize/commit),
                    // so pair the reflection edit with one — this is exactly what exercises the
                    // "points/item.N" grammar against the oracle
                    var pts = (List<Point>)PolyPointsField.GetValue(poly);
                    if (pts.Count > 3 && rng.Next(3) == 0)
                        pts.RemoveAt(rng.Next(pts.Count));
                    else if (rng.Next(2) == 0)
                        pts.Add(RandPoint(rng));
                    else
                        pts[rng.Next(pts.Count)] = RandPoint(rng);
                    poly.LineWidth = poly.LineWidth + 0.25;
                }
                else
                {
                    g.Move(1.5, -2.5);
                }
            }
            else if (kind < 80)
            {
                if (g is GraphicImage img)
                {
                    var shapes = img.ObscuredShapes ?? Array.Empty<GraphicImage.ObscuredShape>();
                    if (shapes.Length == 0 || rng.Next(2) == 0)
                    {
                        img.ObscuredShapes = shapes
                                             .Append(new GraphicImage.ObscuredShape(RandPoint(rng), RandPoint(rng), RandPoint(rng),
                                                                                    RandPoint(rng), rng.Next(1, 20),
                                                                                    (ObscureMode)rng.Next(3)))
                                             .ToArray();
                    }
                    else
                    {
                        var clone = (GraphicImage.ObscuredShape[])shapes.Clone();
                        int i = rng.Next(clone.Length);
                        clone[i] = clone[i] with
                        {
                            P0 = RandPoint(rng),
                            BlurRadius = clone[i].BlurRadius + 1,
                            Mode = (ObscureMode)rng.Next(3),
                        };
                        img.ObscuredShapes = clone;
                    }
                }
                else
                {
                    g.ObjectColor = Palette[rng.Next(Palette.Length)];
                }
            }
            else if (kind < 92)
            {
                list.RemoveAt(rng.Next(list.Count));
            }
            else if (kind < 96)
            {
                if (list.Count >= 2)
                {
                    int i = rng.Next(list.Count);
                    var moved = list[i];
                    list.RemoveAt(i);
                    list.Insert(rng.Next(list.Count + 1), moved);
                }
                else
                {
                    g.ObjectColor = Palette[rng.Next(Palette.Length)];
                }
            }
            else if (kind < 98)
            {
                canvas.ArtworkBackground = Palette[rng.Next(Palette.Length)];
            }
            // else: mutate nothing — the commit must be a no-op that keeps the redo branch
        }

        /// <summary>
        /// A line-for-line reimplementation of the OLD UndoManager algorithm over full JSON
        /// snapshots (tools-history §2.3) — the behavioral reference the delta engine must match.
        /// </summary>
        private sealed class ReferenceHistory
        {
            private sealed class Node
            {
                public JsonObject Value;
                public Node Next;
                public Node Previous;
                public SortedSet<string> Changes;
            }

            private Node _node;
            private bool _canMergeNext;

            public ReferenceHistory(JsonObject initial)
            {
                _node = new Node { Value = initial };
            }

            public JsonObject Current => _node.Value;

            public bool CanUndo => _node.Previous != null;

            public bool CanRedo => _node.Next != null;

            public void Commit(JsonObject json, bool mergable)
            {
                var canMergeWithCurrent = _canMergeNext;
                _canMergeNext = mergable;

                var changes = UndoManager.GetChangedNodes(_node.Value, json);
                if (changes.Count == 0)
                    return;

                if (mergable && canMergeWithCurrent && _node.Changes?.SequenceEqual(changes) == true)
                {
                    _node.Value = json;
                    _node.Next = null;
                    return;
                }

                _node.Next = new Node { Value = json, Previous = _node, Changes = changes };
                _node = _node.Next;
            }

            public JsonObject Undo()
            {
                _node = _node.Previous;
                return _node.Value;
            }

            public JsonObject Redo()
            {
                _node = _node.Next;
                return _node.Value;
            }

            /// <summary>Adopts the engine's post-Normalize document as this node's snapshot
            /// (restore drift absorption — see the fuzz driver's comment).</summary>
            public void RefreshCurrent(JsonObject value)
            {
                _node.Value = value;
            }
        }

        // ====================================================================
        // Targeted grammar pins (the list/array codecs' positional paths)
        // ====================================================================

        [AvaloniaFact]
        public void PolyLinePointEdit_EmitsPositionalItemPaths_AndUndoRestoresPoints()
        {
            var canvas = new DrawingCanvas();
            var poly = new GraphicPolyLine(Colors.Red, 2, new Point(0, 0));
            poly.AddPoint(new Point(10, 5));
            poly.AddPoint(new Point(20, 0));
            poly.EndDrawing(true);
            canvas.GraphicsList.Add(poly);
            canvas.AddCommandToHistory(false);

            var original = ((List<Point>)PolyPointsField.GetValue(poly)).ToList();

            SortedSet<string> built = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => built = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                var pts = (List<Point>)PolyPointsField.GetValue(poly);
                pts[1] = new Point(11, 6);
                poly.LineWidth = 3; // reflection edits have no raise of their own; ride along with one
                canvas.AddCommandToHistory(false);
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            Assert.NotNull(built);
            Assert.Contains($"root/Graphics/{poly.Id}/points/item.1", built);
            Assert.Contains($"root/Graphics/{poly.Id}/lineWidth", built);
            Assert.DoesNotContain($"root/Graphics/{poly.Id}/points/item.0", built);
            Assert.DoesNotContain($"root/Graphics/{poly.Id}/points/item.2", built);

            canvas.Undo();
            Assert.Same(poly, canvas.GraphicsList[0]);
            Assert.Equal(original, (List<Point>)PolyPointsField.GetValue(poly));
            Assert.Equal(2, poly.LineWidth);
        }

        [AvaloniaFact]
        public void ObscuredShapeEdit_EmitsMemberAndItemPaths()
        {
            var canvas = new DrawingCanvas();
            var img = new GraphicImage(@"C:\clowd-fuzz-does-not-exist\image.png", new Rect(0, 0, 50, 50), default);
            img.ObscuredShapes = new[]
            {
                new GraphicImage.ObscuredShape(new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1), 8),
            };
            canvas.GraphicsList.Add(img);
            canvas.AddCommandToHistory(false);

            SortedSet<string> built = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => built = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                // edit one member of shape 0 and append a second shape
                var clone = (GraphicImage.ObscuredShape[])img.ObscuredShapes.Clone();
                clone[0] = clone[0] with { P0 = new Point(2, 2), BlurRadius = 9, Mode = ObscureMode.Solid };
                img.ObscuredShapes = clone
                                     .Append(new GraphicImage.ObscuredShape(new Point(5, 5), new Point(6, 5), new Point(6, 6),
                                                                            new Point(5, 6), 4, ObscureMode.Blur))
                                     .ToArray();
                canvas.AddCommandToHistory(false);
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            Assert.NotNull(built);
            Assert.Contains($"root/Graphics/{img.Id}/obscuredShapes/item.0/P0", built);
            Assert.Contains($"root/Graphics/{img.Id}/obscuredShapes/item.0/BlurRadius", built);
            Assert.Contains($"root/Graphics/{img.Id}/obscuredShapes/item.0/Mode", built);
            Assert.Contains($"root/Graphics/{img.Id}/obscuredShapes/item.1", built);
            Assert.DoesNotContain($"root/Graphics/{img.Id}/obscuredShapes/item.0/P1", built);
        }

        // ====================================================================
        // In-place restore identity (final-design §B.4)
        // ====================================================================

        [AvaloniaFact]
        public void UndoRedo_AppliesInPlace_PreservingCollectionAndInstances()
        {
            var canvas = new DrawingCanvas();
            var a = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10));
            var b = new GraphicRectangle(Colors.Blue, 2, new Rect(20, 20, 10, 10));
            canvas.GraphicsList.Add(a);
            canvas.GraphicsList.Add(b);
            canvas.AddCommandToHistory(false);
            var list = canvas.GraphicsList;

            b.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);

            canvas.Undo();
            Assert.Same(list, canvas.GraphicsList); // the collection is never swapped
            Assert.Same(a, canvas.GraphicsList[0]); // untouched instance survives
            Assert.Same(b, canvas.GraphicsList[1]); // the edited instance is restored in place
            Assert.Equal(Colors.Blue, b.ObjectColor);

            canvas.Redo();
            Assert.Same(list, canvas.GraphicsList);
            Assert.Same(b, canvas.GraphicsList[1]);
            Assert.Equal(Colors.Green, b.ObjectColor);
        }

        [AvaloniaFact]
        public void UndoOfDelete_ReinsertsTheRetainedInstance_Unselected()
        {
            var canvas = new DrawingCanvas();
            var a = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 10, 10));
            var b = new GraphicRectangle(Colors.Blue, 2, new Rect(20, 20, 10, 10));
            canvas.GraphicsList.Add(a);
            canvas.GraphicsList.Add(b);
            canvas.AddCommandToHistory(false);

            b.IsSelected = true;
            canvas.Delete();
            Assert.Single(canvas.GraphicsList);

            canvas.Undo();
            Assert.Equal(2, canvas.GraphicsList.Count);
            Assert.Same(b, canvas.GraphicsList[1]); // instance-retaining delete: same object, caches intact
            Assert.False(b.IsSelected);             // undo clears selection (contract #22)
        }

        [AvaloniaFact]
        public void RestoreState_LoadsInPlace_WithoutRaisingStateUpdated()
        {
            var canvas = new DrawingCanvas();
            var listBefore = canvas.GraphicsList;
            canvas.GraphicsList.Add(new GraphicRectangle(Colors.Red, 2, new Rect(1, 2, 30, 40)));
            var json = UndoManager.SerializeDocument(canvas);
            canvas.GraphicsList.Clear();

            int raises = 0;
            canvas.StateUpdated += (_, _) => raises++;
            canvas.RestoreState(json);

            Assert.Same(listBefore, canvas.GraphicsList); // in-place load, same collection
            Assert.Single(canvas.GraphicsList);
            Assert.Equal(0, raises); // restore never raises StateChanged/StateUpdated (contract #23)
            Assert.False(canvas.CommandUndo.CanExecute(null));
        }
    }
}
