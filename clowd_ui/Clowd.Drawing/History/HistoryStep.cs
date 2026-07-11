using System;
using System.Collections.Generic;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// One side of a graphic's state: its field map, a captured (deep-copied) value record
    /// aligned with the map's slots, and the live instance that carried those values. Retaining
    /// the instance is what makes undo of a delete a list insert instead of a deserialize
    /// (final-design §0.2 graft #2) — the instance's transient caches survive untouched.
    /// </summary>
    internal sealed class FieldRecord
    {
        public readonly GraphicFieldMap Map;
        public readonly object[] Values;
        public readonly GraphicBase Instance;

        public FieldRecord(GraphicFieldMap map, object[] values, GraphicBase instance)
        {
            Map = map;
            Values = values;
            Instance = instance;
        }
    }

    /// <summary>
    /// Per-graphic transition within a step. <c>Before == null</c> → the graphic was added by the
    /// step; <c>After == null</c> → removed; both non-null → a field edit (or, pathologically, an
    /// instance/type replacement under the same id — the apply path reconciles by identity).
    /// Mutable because a merge fold rewrites <c>After</c> in place (final-design §B.2 step 5).
    /// </summary>
    internal sealed class GraphicDelta
    {
        public readonly string Id;
        public FieldRecord Before;
        public FieldRecord After;

        /// <summary>
        /// Marquee-excluded z-index hint for re-insertion (index in the Before order for removals,
        /// in the After order for additions); -1 for pure field edits. The step's Order sequences
        /// are authoritative — this only pre-places instances so the final permutation is short.
        /// </summary>
        public int Index = -1;

        public GraphicDelta(string id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// A history node — same doubly-linked topology as the old SimpleLinkedListNode, but carrying
    /// field deltas instead of a full JSON snapshot (final-design §B.1). Each node describes the
    /// transition from <see cref="Previous"/> to itself; the root node (ClearHistory) has
    /// <c>Changes == null</c> and no deltas, exactly like the old root's null Changes (which is
    /// what prevents merging into it).
    /// </summary>
    internal sealed class HistoryStep
    {
        public HistoryStep Previous;
        public HistoryStep Next;

        /// <summary>
        /// The changed-path set of this transition, same grammar as
        /// <see cref="UndoManager.GetChangedNodes"/>; drives the merge decision via SequenceEqual.
        /// Null on the root node.
        /// </summary>
        public SortedSet<string> Changes;

        public GraphicDelta[] Graphics = Array.Empty<GraphicDelta>();

        public (Color Before, Color After)? Background;

        /// <summary>
        /// Full marquee-excluded id sequences of both sides, recorded whenever the sequence
        /// changed at all (any add/remove/reorder). Undo/redo finishes by permuting the live list
        /// to the target sequence, which makes membership deltas order-exact even when survivors
        /// moved around an add/remove (a case the "(order)" change path deliberately does not
        /// report, matching the oracle's membership-suppresses-(order) rule).
        /// </summary>
        public (string[] Before, string[] After)? Order;
    }
}
