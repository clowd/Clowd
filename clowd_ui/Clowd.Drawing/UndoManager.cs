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
            bool HasChildren(JsonNode node) => node is JsonObject || node is JsonArray;

            string GetItemName(JsonNode node, int index)
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

            IEnumerable<KeyValuePair<string, JsonNode>> NamedChildren(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var kvp in obj)
                        yield return kvp;
                }
                else if (node is JsonArray arr)
                {
                    for (int i = 0; i < arr.Count; i++)
                        yield return new KeyValuePair<string, JsonNode>(GetItemName(arr[i], i), arr[i]);
                }
            }

            IEnumerable<IEnumerable<string>> GetChangedPathsInternal(IEnumerable<string> path, JsonNode prevEl, JsonNode nextEl)
            {
                // id-keyed matching makes a pure reorder invisible to the per-item diff, but list
                // order is the z-order of the graphics — report it explicitly. (Adds/removes change
                // membership and already produce their own paths.)
                if (prevEl is JsonArray && nextEl is JsonArray)
                {
                    var prevNames = NamedChildren(prevEl).Select(c => c.Key).ToList();
                    var nextNames = NamedChildren(nextEl).Select(c => c.Key).ToList();
                    if (prevNames.Count == nextNames.Count &&
                        !prevNames.SequenceEqual(nextNames) &&
                        new HashSet<string>(prevNames).SetEquals(nextNames))
                    {
                        yield return path.Append("(order)");
                    }
                }

                Dictionary<string, JsonNode> dict = new();

                // add all of prev properties to dictionary
                foreach (var e in NamedChildren(prevEl))
                {
                    dict.Add(e.Key, e.Value);
                }

                // iterate next properties, find matches in dictionary
                foreach (var e in NamedChildren(nextEl))
                {
                    var eNext = e.Value;
                    var elName = e.Key;
                    var elPath = path.Append(elName);

                    if (!dict.TryGetValue(elName, out var ePrev))
                    {
                        // prev does not contain this property
                        yield return elPath;
                    }
                    else // match found
                    {
                        dict.Remove(elName);

                        if (HasChildren(ePrev) != HasChildren(eNext))
                        {
                            // the structure of this element has changed
                            yield return elPath;
                        }
                        else
                        {
                            if (HasChildren(ePrev))
                            {
                                // they both have children to check
                                foreach (var f in GetChangedPathsInternal(elPath, ePrev, eNext))
                                {
                                    yield return f;
                                }
                            }
                            else
                            {
                                // they both have an absolute value and it's changed
                                if (!JsonNode.DeepEquals(ePrev, eNext))
                                {
                                    yield return elPath;
                                }
                            }
                        }
                    }
                }

                // anything not removed from the dictionary was not in 'next'
                foreach (var e in dict)
                {
                    yield return path.Append(e.Key);
                }
            }

            return new SortedSet<string>(
                GetChangedPathsInternal(new[] { "root" }, prev, next)
                    .Select(s => String.Join("/", s)));
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
            var nextGraphics = new GraphicCollection(_drawingCanvas);
            foreach (var s in state.Graphics)
            {
                s.Normalize();
                nextGraphics.Add(s);
            }

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
