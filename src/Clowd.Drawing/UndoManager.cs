using System;
using System.Collections.Generic;

namespace Clowd.Drawing
{
    internal class UndoManager
    {
        public bool CanUndo => _nextUndo >= 0 && _nextUndo <= _historyList.Count - 1;
        public bool CanRedo => _nextUndo < _historyList.Count - 1;
        public event EventHandler StateChanged;

        private readonly DrawingCanvas _drawingCanvas;
        private List<byte[]> _historyList;
        private int _nextUndo;

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            this._drawingCanvas = drawingCanvas;
            ClearHistory();
        }

        public void ClearHistory()
        {
            _historyList = new List<byte[]>();
            _nextUndo = -1;
            RaiseStateChangedEvent();
        }

        public void AddCommandStep()
        {
            this.TrimHistoryList();
            var state = _drawingCanvas.GraphicsList.Serialize();
            _historyList.Add(state);
            _nextUndo++;
            RaiseStateChangedEvent();
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            var nextState = _historyList[_nextUndo];
            var nextGraphics = new GraphicCollection(_drawingCanvas);
            nextGraphics.DeserializeObjectsInto(nextState);

            // replace the current graphic set with the one from history
            var old = _drawingCanvas.GraphicsList;
            _drawingCanvas.GraphicsList = nextGraphics;
            _drawingCanvas.InvalidateVisual();
            old.Clear();

            _nextUndo--;
            RaiseStateChangedEvent();
        }

        public void Redo()
        {
            if (!CanRedo)
                return;

            int itemToRedo = _nextUndo + 1;

            var nextState = _historyList[itemToRedo];
            var nextGraphics = new GraphicCollection(_drawingCanvas);
            nextGraphics.DeserializeObjectsInto(nextState);

            // replace the current graphic set with the one from history
            var old = _drawingCanvas.GraphicsList;
            _drawingCanvas.GraphicsList = nextGraphics;
            _drawingCanvas.InvalidateVisual();
            old.Clear();

            _nextUndo++;
            RaiseStateChangedEvent();
        }

        private void TrimHistoryList()
        {
            // We can redo any undone command until we execute a new 
            // command. The new command takes us off in a new direction,
            // which means we can no longer redo previously undone actions. 
            // So, we purge all undone commands from the history list.*/

            if (_historyList.Count == 0)
                return;
            if (_nextUndo == _historyList.Count - 1)
                return;

            // Purge all items below the NextUndo pointer
            for (int i = _historyList.Count - 1; i > _nextUndo; i--)
                _historyList.RemoveAt(i);
        }

        private void RaiseStateChangedEvent()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
