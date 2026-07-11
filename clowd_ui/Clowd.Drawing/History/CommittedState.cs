using System;
using System.Collections.Generic;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// Incrementally-maintained shadow of the current history node's document state (final-design
    /// §B.1): captured persisted fields per graphic id, the marquee-excluded id z-order, and the
    /// artwork background. Commits diff the live document against this (O(changed)); undo/redo
    /// move it to the applied side. It holds ONE field capture of the document — comparable to a
    /// single one of the old full-JSON snapshots, held once instead of per node.
    /// </summary>
    internal sealed class CommittedState
    {
        public readonly Dictionary<string, FieldRecord> ById = new Dictionary<string, FieldRecord>(StringComparer.Ordinal);

        public string[] Order = Array.Empty<string>();

        public Color Background;

        /// <summary>Full capture of the live document (bootstrap / ClearHistory / RestoreState).</summary>
        public static CommittedState Capture(DrawingCanvas canvas)
        {
            var state = new CommittedState { Background = canvas.ArtworkBackground };

            var collection = canvas.GraphicsList;
            if (collection == null)
                return state;

            var order = new List<string>(collection.Count);
            foreach (var g in collection)
            {
                if (g is GraphicSelectionRectangle)
                    continue; // the marquee is internal and never serialized/committed

                var map = GraphicFieldMap.For(g.GetType());
                state.ById[g.Id] = new FieldRecord(map, map.Capture(g), g);
                order.Add(g.Id);
            }

            state.Order = order.ToArray();
            return state;
        }
    }
}
