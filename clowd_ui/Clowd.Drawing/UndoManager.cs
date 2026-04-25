using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    public class StateChangedEventArgs : EventArgs
    {
        public string State { get; }

        public StateChangedEventArgs(string state)
        {
            State = state;
        }
    }

    /// <summary>
    /// Snapshot-based undo: each step is a JSON serialization of the full
    /// graphic state. Same architecture as the WPF original — the WPF version
    /// used XML via <c>RT.Serialization.ClassifyXml</c>; this port uses
    /// <c>System.Text.Json</c> with polymorphic <c>[JsonDerivedType]</c>
    /// attributes on <see cref="GraphicBase"/>.
    ///
    /// The merge optimisation from the WPF original (collapse rapid same-property
    /// changes into one undo step) is not ported here because each tool already
    /// fires <c>AddCommandToHistory</c> exactly once at the end of a drag, so
    /// drags naturally collapse to a single snapshot.
    /// </summary>
    internal class UndoManager
    {
        private class HistoryNode
        {
            public string Value { get; init; } = string.Empty;
            public HistoryNode? Next { get; set; }
            public HistoryNode? Previous { get; set; }
        }

        private class GraphicState
        {
            public Color BackgroundColor { get; set; } = Colors.Transparent;
            public GraphicBase[] Graphics { get; set; } = Array.Empty<GraphicBase>();
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            // Compact: undo snapshots are kept in memory, no need to pretty-print
            WriteIndented = false,
            Converters =
            {
                new JsonStringEnumConverter(),
            },
        };

        public bool CanUndo => _node?.Previous != null;

        public bool CanRedo => _node?.Next != null;

        public event EventHandler<StateChangedEventArgs>? StateChanged;

        private readonly DrawingCanvas _drawingCanvas;
        private HistoryNode? _node;

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            _drawingCanvas = drawingCanvas;
            ClearHistory();
        }

        public void ClearHistory(string? initialState = null)
        {
            initialState ??= JsonSerializer.Serialize(GetCurrentState(), _jsonOptions);
            _node = new HistoryNode { Value = initialState };
        }

        public void AddCommandStep(bool merge)
        {
            var json = JsonSerializer.Serialize(GetCurrentState(), _jsonOptions);

            if (_node == null)
            {
                _node = new HistoryNode { Value = json };
                return;
            }

            // Skip if nothing actually changed.
            if (_node.Value == json)
                return;

            var next = new HistoryNode { Value = json, Previous = _node };
            _node.Next = next;
            _node = next;

            RaiseStateChangedEvent(_node.Value);
        }

        public void Undo()
        {
            if (!CanUndo || _node?.Previous == null)
                return;

            _node = _node.Previous;
            ApplyState(_node.Value);
            RaiseStateChangedEvent(_node.Value);
        }

        public void Redo()
        {
            if (!CanRedo || _node?.Next == null)
                return;

            _node = _node.Next;
            ApplyState(_node.Value);
            RaiseStateChangedEvent(_node.Value);
        }

        private GraphicState GetCurrentState()
        {
            return new GraphicState
            {
                BackgroundColor = _drawingCanvas.ArtworkBackground,
                Graphics = _drawingCanvas.GraphicsList.GetGraphicList(false),
            };
        }

        private void ApplyState(string json)
        {
            var state = JsonSerializer.Deserialize<GraphicState>(json, _jsonOptions);
            if (state == null) return;

            var nextGraphics = new GraphicCollection();
            foreach (var g in state.Graphics)
            {
                // Normalize ran automatically via IJsonOnDeserialized.
                nextGraphics.Add(g);
            }
            _drawingCanvas.GraphicsList = nextGraphics;
            _drawingCanvas.ArtworkBackground = state.BackgroundColor;
        }

        private void RaiseStateChangedEvent(string state)
        {
            StateChanged?.Invoke(this, new StateChangedEventArgs(state));
        }
    }
}
