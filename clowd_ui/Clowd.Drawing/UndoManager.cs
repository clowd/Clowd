using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    internal class UndoManager
    {
        class SimpleLinkedListNode
        {
            public JsonObject Value { get; set; }
            public SimpleLinkedListNode Next { get; set; }
            public SimpleLinkedListNode Previous { get; set; }
            public SortedSet<string> Changes { get; set; }
        }

        class GraphicState
        {
            public Color BackgroundColor { get; set; } = Colors.Transparent;
            public GraphicBase[] Graphics { get; set; } = new GraphicBase[0];
        }

        public bool CanUndo => _node?.Previous != null;

        public bool CanRedo => _node?.Next != null;

        public event EventHandler<StateChangedEventArgs> StateChanged;

        private readonly DrawingCanvas _drawingCanvas;
        private SimpleLinkedListNode _node;
        private bool _canMergeNext = false;

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            _drawingCanvas = drawingCanvas;
            ClearHistory();
        }

        public void ClearHistory(JsonObject initialState = null)
        {
            initialState ??= SerializeState(new GraphicState { BackgroundColor = _drawingCanvas.ArtworkBackground });
            _canMergeNext = false;
            SetState(new SimpleLinkedListNode { Value = initialState });
        }

        public void AddCommandStep(bool mergable)
        {
            var json = SerializeState(GetNextState());

            if (_node?.Value == null)
            {
                _node = new SimpleLinkedListNode { Value = json };
                return;
            }

            // 'mergable=false' prevents this event from being merged with the current step
            // but also the next event from being merged with this one.
            var canMergeWithCurrent = _canMergeNext;
            _canMergeNext = mergable;

            // do nothing if nothing was changed.
            var nextChanges = GetChangedNodes(_node.Value, json);
            if (nextChanges.Count == 0)
            {
                return;
            }

            // merge the previous/next changes into a single step
            // if only the same properties were changed
            if (mergable && canMergeWithCurrent && _node?.Changes?.SequenceEqual(nextChanges) == true)
            {
                _node.Value = json;
                _node.Next = null;
                RaiseStateChangedEvent(_node.Value); // keep session persistence current
                return;
            }

            _node.Next = new SimpleLinkedListNode { Value = json, Previous = _node, Changes = nextChanges };
            _node = _node.Next;

            RaiseStateChangedEvent(_node.Value);
        }

        /// <summary>
        /// Computes the set of changed property paths between two state snapshots, at per-property
        /// granularity (one path per changed leaf, recursing into objects/arrays). Array items that
        /// are objects carrying an "id" property (the graphics) are keyed by that id, so per-graphic
        /// edits diff against the same graphic; a pure reorder (same ids, different order — i.e. a
        /// z-order change) is reported as a single "(order)" path on the array. Other array items
        /// are keyed positionally ("item.N"). The undo merge logic compares these sets to decide
        /// whether consecutive edits collapse into one step.
        /// </summary>
        internal static SortedSet<string> GetChangedNodes(JsonObject prev, JsonObject next)
        {
            // this runs on every command step (including each merged step of a drag), so it is
            // written iteratively over materialized child arrays with plain string paths — no
            // LINQ/iterator allocations per node.
            var changes = new SortedSet<string>(StringComparer.Ordinal);
            DiffChildren("root", prev, next, changes);
            return changes;
        }

        private static bool HasChildren(JsonNode node) => node is JsonObject || node is JsonArray;

        private static string GetItemName(JsonNode node, int index)
        {
            if (node is JsonObject obj &&
                obj.TryGetPropertyValue("id", out var id) &&
                id is JsonValue value &&
                value.TryGetValue<string>(out var name))
            {
                return name;
            }

            return "item." + index;
        }

        private static KeyValuePair<string, JsonNode>[] GetNamedChildren(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var children = new KeyValuePair<string, JsonNode>[obj.Count];
                int i = 0;
                foreach (var kvp in obj)
                    children[i++] = kvp;
                return children;
            }

            if (node is JsonArray arr)
            {
                var children = new KeyValuePair<string, JsonNode>[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    children[i] = new KeyValuePair<string, JsonNode>(GetItemName(arr[i], i), arr[i]);
                return children;
            }

            return Array.Empty<KeyValuePair<string, JsonNode>>();
        }

        private static void DiffChildren(string path, JsonNode prevEl, JsonNode nextEl, SortedSet<string> changes)
        {
            var prevChildren = GetNamedChildren(prevEl);
            var nextChildren = GetNamedChildren(nextEl);

            // id-keyed matching makes a pure reorder invisible to the per-item diff, but list
            // order is the z-order of the graphics — report it explicitly. (Adds/removes change
            // membership and already produce their own paths.)
            if (prevEl is JsonArray && nextEl is JsonArray && prevChildren.Length == nextChildren.Length)
            {
                bool sameOrder = true;
                for (int i = 0; i < prevChildren.Length; i++)
                {
                    if (!string.Equals(prevChildren[i].Key, nextChildren[i].Key, StringComparison.Ordinal))
                    {
                        sameOrder = false;
                        break;
                    }
                }

                if (!sameOrder)
                {
                    var prevNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var c in prevChildren)
                        prevNames.Add(c.Key);

                    bool sameMembers = true;
                    foreach (var c in nextChildren)
                    {
                        if (!prevNames.Contains(c.Key))
                        {
                            sameMembers = false;
                            break;
                        }
                    }

                    if (sameMembers)
                        changes.Add(path + "/(order)");
                }
            }

            // add all of prev properties to dictionary
            var dict = new Dictionary<string, JsonNode>(prevChildren.Length, StringComparer.Ordinal);
            foreach (var e in prevChildren)
                dict.Add(e.Key, e.Value);

            // iterate next properties, find matches in dictionary
            foreach (var e in nextChildren)
            {
                var elPath = path + "/" + e.Key;

                if (!dict.TryGetValue(e.Key, out var ePrev))
                {
                    // prev does not contain this property
                    changes.Add(elPath);
                    continue;
                }

                dict.Remove(e.Key);

                if (HasChildren(ePrev) != HasChildren(e.Value))
                {
                    // the structure of this element has changed
                    changes.Add(elPath);
                }
                else if (HasChildren(ePrev))
                {
                    // they both have children to check
                    DiffChildren(elPath, ePrev, e.Value, changes);
                }
                else if (!JsonNode.DeepEquals(ePrev, e.Value))
                {
                    // they both have an absolute value and it's changed
                    changes.Add(elPath);
                }
            }

            // anything not removed from the dictionary was not in 'next'
            foreach (var e in dict)
                changes.Add(path + "/" + e.Key);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            SetState(_node.Previous);
            RaiseStateChangedEvent(_node.Value);
        }

        public void Redo()
        {
            if (!CanRedo)
                return;

            SetState(_node.Next);
            RaiseStateChangedEvent(_node.Value);
        }

        GraphicState GetNextState()
        {
            return new()
            {
                BackgroundColor = _drawingCanvas.ArtworkBackground,
                Graphics = _drawingCanvas.GraphicsList.GetGraphicList(false),
            };
        }

        static JsonObject SerializeState(GraphicState state)
        {
            return (JsonObject)JsonSerializer.SerializeToNode(state, GraphicsSerializer.Options);
        }

        void SetState(SimpleLinkedListNode node)
        {
            var state = node.Value.Deserialize<GraphicState>(GraphicsSerializer.Options);
            foreach (var s in state.Graphics)
                s.Normalize();

            var nextGraphics = new GraphicCollection(_drawingCanvas);
            nextGraphics.AddRange(state.Graphics);

            _drawingCanvas.GraphicsList = nextGraphics;
            _drawingCanvas.ArtworkBackground = state.BackgroundColor;
            _node = node;
        }

        private void RaiseStateChangedEvent(JsonObject state)
        {
            StateChanged?.Invoke(this, new StateChangedEventArgs(state));
        }
    }
}
