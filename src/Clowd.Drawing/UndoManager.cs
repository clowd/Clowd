using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;
using Clowd.Drawing.Graphics;
using RT.Serialization;
using RT.Util.ExtensionMethods;

namespace Clowd.Drawing
{
    public class StateChangedEventArgs : EventArgs
    {
        public XElement State { get; }

        public StateChangedEventArgs(XElement state)
        {
            State = state;
        }
    }

    public class UndoManager
    {
        class SimpleLinkedListNode
        {
            public XElement Value { get; set; }
            public SimpleLinkedListNode Next { get; set; }
            public SimpleLinkedListNode Previous { get; set; }
            public string[] Changes { get; set; }
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

        public void ClearHistory(XElement initialState = null)
        {
            initialState ??= ClassifyXml.Serialize(new GraphicState { BackgroundColor = _drawingCanvas.ArtworkBackground });
            _canMergeNext = false;
            SetState(new SimpleLinkedListNode { Value = initialState });
        }

        public void AddCommandStep(bool mergable)
        {
            var xml = ClassifyXml.Serialize(GetNextState());

            if (_node?.Value == null)
            {
                _node = new SimpleLinkedListNode { Value = xml };
                return;
            }

            // 'mergable' prevents this event from being merged with current
            // but also the next event from being merged with it.
            _canMergeNext = mergable;

            // do nothing if nothing was changed.
            var nextChanges = GetChangedXmlNodes(_node.Value, xml);
            if (nextChanges.Length == 0)
            {
                return;
            }

            // merge the previous/next changes into a single step
            // if only the same properties were changed
            if (mergable && _canMergeNext && _node?.Changes?.SequenceEqual(nextChanges) == true)
            {
                _node.Value = xml;
                _node.Next = null;
                return;
            }

            _node.Next = new SimpleLinkedListNode { Value = xml, Previous = _node, Changes = nextChanges };
            _node = _node.Next;

            RaiseStateChangedEvent(_node.Value);
        }

        public static string[] GetChangedXmlNodes(XElement element1, XElement element2)
        {
            string GetElementName(XElement e, int index)
            {
                if (e.HasElements)
                {
                    var id = e.Element("id");
                    if (id?.Value != null) return id.Value;
                }
                if (index >= 0 && e.Name.LocalName == "item")
                {
                    return e.Name.LocalName + "." + index;
                }
                return e.Name.LocalName;
            }

            IEnumerable<IEnumerable<string>> GetChangedPathsInternal(IEnumerable<string> path, XElement prev, XElement next)
            {
                Dictionary<string, XElement> dict = new();

                // add all of prev properties to dictionary
                foreach (var e in prev.Elements().Select((Item, Index) => new { Index, Item }))
                {
                    dict.Add(GetElementName(e.Item, e.Index), e.Item);
                }

                // iterate next properties, find matches in dictionary
                foreach (var e in next.Elements().Select((Item, Index) => new { Index, Item }))
                {
                    var eNext = e.Item;
                    var elName = GetElementName(eNext, e.Index);
                    var elPath = path.Concat(elName);

                    if (!dict.TryGetValue(elName, out var ePrev))
                    {
                        // prev does not contain this property
                        yield return elPath;
                    }
                    else // match found
                    {
                        dict.Remove(elName);

                        if (ePrev.HasElements != eNext.HasElements)
                        {
                            // the structure of this element has changed
                            yield return path.Concat(elName);
                        }
                        else
                        {
                            if (ePrev.HasElements)
                            {
                                // they both have sub elements to check
                                foreach (var f in GetChangedPathsInternal(path.Concat(elName), ePrev, eNext))
                                {
                                    yield return f;
                                }
                            }
                            else
                            {
                                // they both have an absolute value and it's changed
                                if (!ePrev.Value.Equals((string)eNext.Value))
                                {
                                    yield return path.Concat(elName);
                                }
                            }
                        }
                    }
                }

                // anything not removed from the dictionary was not in 'next'
                foreach (var e in dict)
                {
                    yield return path.Concat(e.Key);
                }
            }

            return GetChangedPathsInternal(new[] { "root" }, element1, element2)
                .Select(s => String.Join("/", s))
                .OrderBy(s => s)
                .ToArray();
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

        void SetState(SimpleLinkedListNode node)
        {
            var state = ClassifyXml.Deserialize<GraphicState>(node.Value);
            var nextGraphics = new GraphicCollection(_drawingCanvas);
            foreach (var s in state.Graphics) nextGraphics.Add(s);
            _drawingCanvas.GraphicsList = nextGraphics;
            _drawingCanvas.ArtworkBackground = state.BackgroundColor;
            _node = node;
        }

        private void RaiseStateChangedEvent(XElement state)
        {
            StateChanged?.Invoke(this, new StateChangedEventArgs(state));
        }
    }
}
