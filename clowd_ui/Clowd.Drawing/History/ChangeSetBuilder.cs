using System;
using System.Collections.Generic;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>The result of diffing the live document against the committed shadow.</summary>
    internal sealed class ChangeSet
    {
        /// <summary>
        /// Changed paths in the exact <see cref="UndoManager.GetChangedNodes"/> grammar:
        /// field edit → "root/Graphics/&lt;id&gt;/&lt;jsonName&gt;[...]"; add/remove →
        /// "root/Graphics/&lt;id&gt;"; pure reorder (same length AND same member set) → a single
        /// "root/Graphics/(order)" (membership change suppresses it even if survivors moved);
        /// background → "root/BackgroundColor".
        /// </summary>
        public readonly SortedSet<string> Changes = new SortedSet<string>(StringComparer.Ordinal);

        public readonly List<GraphicDelta> Deltas = new List<GraphicDelta>();

        public (Color Before, Color After)? Background;

        public (string[] Before, string[] After)? Order;
    }

    /// <summary>
    /// Builds a <see cref="ChangeSet"/> from the dirt reported by the frozen
    /// <c>GraphicCollection.ConsumeDirty()</c> seam (final-design §B.2 step 3). Cost is
    /// O(changed graphics × fields), plus one O(n) id-sequence compare only when a structural
    /// mutation happened. Transient-only dirt (and "dirtied then reverted") drops out here because
    /// the persisted-field records compare equal.
    /// </summary>
    internal static class ChangeSetBuilder
    {
        public static ChangeSet Build(DrawingCanvas canvas, CommittedState committed,
                                      HashSet<GraphicBase> dirtyGraphics, bool structuralDirty, bool backgroundDirty)
        {
            var result = new ChangeSet();
            var collection = canvas.GraphicsList;
            bool needStructural = structuralDirty;

            // ---- per-graphic field diffs (graphics still present under a committed id) ----
            var deltaIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in dirtyGraphics)
            {
                if (g is GraphicSelectionRectangle)
                    continue; // the marquee raises during group-select drags but is never committed

                if (!collection.TryGetById(g.Id, out var live) || !ReferenceEquals(live, g))
                    continue; // removed (or replaced) since the last commit — the structural pass reports it

                if (!committed.ById.TryGetValue(g.Id, out var prev))
                {
                    // present but unknown to the shadow: added since the last commit, or an
                    // uncommitted add that survived an undo, or an in-place Id rewrite. All of
                    // these are order-sequence-visible, so force the structural compare even if
                    // the collection's structural flag was already consumed.
                    needStructural = true;
                    continue;
                }

                DiffPresent(committed, prev, g, result, deltaIds, collection);
            }

            // ---- membership / z-order (O(n), only on structural commits) ----
            if (needStructural)
                DiffStructure(committed, collection, result, deltaIds);

            // ---- background ----
            if (backgroundDirty)
            {
                var after = canvas.ArtworkBackground;
                if (after != committed.Background)
                {
                    result.Changes.Add("root/BackgroundColor");
                    result.Background = (committed.Background, after);
                }
            }

            return result;
        }

        private static void DiffPresent(CommittedState committed, FieldRecord prev, GraphicBase live,
                                        ChangeSet result, HashSet<string> deltaIds, GraphicCollection collection)
        {
            var map = GraphicFieldMap.For(live.GetType());
            var prefix = "root/Graphics/" + live.Id;

            if (!ReferenceEquals(prev.Map, map))
            {
                // pathological: the same id now refers to a different TYPE (remove + re-add with a
                // colliding id inside one commit window). The oracle recurses into the object and
                // reports "$type" plus every member that differs or exists on only one side.
                var capture = map.Capture(live);
                EmitUnionDiff(prefix, prev, map, capture, result.Changes);
                var delta = new GraphicDelta(live.Id)
                {
                    Before = prev,
                    After = new FieldRecord(map, capture, live),
                    Index = FilteredIndexOf(collection, live),
                };
                result.Deltas.Add(delta);
                deltaIds.Add(live.Id);
                return;
            }

            var values = map.Capture(live);
            int before = result.Changes.Count;
            for (int i = 0; i < map.Slots.Length; i++)
                map.Slots[i].Codec.EmitPaths(prefix + "/" + map.Slots[i].JsonName, prev.Values[i], values[i], result.Changes);

            if (result.Changes.Count == before && ReferenceEquals(prev.Instance, live))
                return; // clean (transient-only or reverted dirt)

            // an instance swap under the same id applies by replacement, so it needs a real
            // z-index; plain field edits apply in place and never read Index
            int index = ReferenceEquals(prev.Instance, live) ? -1 : FilteredIndexOf(collection, live);
            result.Deltas.Add(new GraphicDelta(live.Id) { Before = prev, After = new FieldRecord(map, values, live), Index = index });
            deltaIds.Add(live.Id);
        }

        private static void DiffStructure(CommittedState committed, GraphicCollection collection,
                                          ChangeSet result, HashSet<string> deltaIds)
        {
            var liveIds = new List<string>(collection.Count);
            var liveGraphics = new List<GraphicBase>(collection.Count);
            foreach (var g in collection)
            {
                if (g is GraphicSelectionRectangle)
                    continue;
                liveIds.Add(g.Id);
                liveGraphics.Add(g);
            }

            var prevOrder = committed.Order;
            if (SequencesEqual(prevOrder, liveIds))
                return; // e.g. added-then-removed before one commit — structurally a no-op

            bool membershipChanged = false;

            var liveSet = new HashSet<string>(liveIds, StringComparer.Ordinal);
            for (int i = 0; i < prevOrder.Length; i++)
            {
                var id = prevOrder[i];
                if (liveSet.Contains(id))
                    continue;

                membershipChanged = true;
                var rec = committed.ById[id];
                result.Changes.Add("root/Graphics/" + id);
                result.Deltas.Add(new GraphicDelta(id) { Before = rec, After = null, Index = i });
                deltaIds.Add(id);

                // instance-retaining delete (final-design §0.2): the step keeps the live instance
                // for a cache-intact re-insert on undo, but drops its memory-heavy transients now
                rec.Instance?.TrimTransientCaches();
            }

            var prevSet = new HashSet<string>(prevOrder, StringComparer.Ordinal);
            for (int i = 0; i < liveIds.Count; i++)
            {
                var id = liveIds[i];
                if (prevSet.Contains(id))
                {
                    // same id on both sides: normally handled by the field pass (it only sees
                    // graphics that raised). Catch a silent same-id instance replacement here.
                    if (!deltaIds.Contains(id))
                    {
                        var rec = committed.ById[id];
                        if (!ReferenceEquals(rec.Instance, liveGraphics[i]))
                            DiffPresent(committed, rec, liveGraphics[i], result, deltaIds, collection);
                    }
                    continue;
                }

                membershipChanged = true;
                var g = liveGraphics[i];
                var map = GraphicFieldMap.For(g.GetType());
                result.Changes.Add("root/Graphics/" + id);
                result.Deltas.Add(new GraphicDelta(id) { Before = null, After = new FieldRecord(map, map.Capture(g), g), Index = i });
                deltaIds.Add(id);
            }

            // the oracle's exact rule: (order) only for a pure reorder — same length and same
            // member set (unique ids make those equivalent); any membership change suppresses it
            if (!membershipChanged)
                result.Changes.Add("root/Graphics/(order)");

            result.Order = (prevOrder, liveIds.ToArray());
        }

        /// <summary>
        /// Member-level diff across two DIFFERENT field maps (same-id type change), matching how
        /// DiffChildren reports two objects: "$type" differs, properties present on only one side
        /// are paths, common properties (shared base fields — same codec) compare by value.
        /// </summary>
        private static void EmitUnionDiff(string prefix, FieldRecord prev, GraphicFieldMap newMap, object[] newValues,
                                          SortedSet<string> changes)
        {
            changes.Add(prefix + "/$type");

            var prevIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < prev.Map.Slots.Length; i++)
                prevIndex[prev.Map.Slots[i].JsonName] = i;

            for (int i = 0; i < newMap.Slots.Length; i++)
            {
                var name = newMap.Slots[i].JsonName;
                if (prevIndex.TryGetValue(name, out var pi))
                {
                    newMap.Slots[i].Codec.EmitPaths(prefix + "/" + name, prev.Values[pi], newValues[i], changes);
                    prevIndex.Remove(name);
                }
                else
                {
                    changes.Add(prefix + "/" + name);
                }
            }

            foreach (var leftover in prevIndex.Keys)
                changes.Add(prefix + "/" + leftover);
        }

        internal static bool SequencesEqual(string[] a, List<string> b)
        {
            if (a.Length != b.Count)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        internal static int FilteredIndexOf(GraphicCollection collection, GraphicBase graphic)
        {
            int index = 0;
            foreach (var g in collection)
            {
                if (g is GraphicSelectionRectangle)
                    continue;
                if (ReferenceEquals(g, graphic))
                    return index;
                index++;
            }

            return index;
        }
    }
}
