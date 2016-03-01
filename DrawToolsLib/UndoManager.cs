using System;
using System.Collections.Generic;
using System.Text;

/// Undo-Redo code is written using the article:
/// http://www.codeproject.com/cs/design/commandpatterndemo.asp
//  The Command Pattern and MVC Architecture
//  By David Veeneman.

namespace DrawToolsLib
{
    internal class UndoManager
    {
        public bool CanUndo => _nextUndo >= 0 && _nextUndo <= _historyList.Count - 1;
        public bool CanRedo => _nextUndo < _historyList.Count - 1;
        public event EventHandler StateChanged;

        private readonly DrawingCanvas _drawingCanvas;
        private List<CommandBase> _historyList;
        private int _nextUndo;

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            this._drawingCanvas = drawingCanvas;
            ClearHistory();
        }

        public void ClearHistory()
        {
            _historyList = new List<CommandBase>();
            _nextUndo = -1;


            RaiseStateChangedEvent();
        }
        public void AddCommandToHistory(CommandBase command)
        {
            this.TrimHistoryList();
            _historyList.Add(command);
            _nextUndo++;
            RaiseStateChangedEvent();
        }
        public void Undo()
        {
            if (!CanUndo)
                return;

            CommandBase command = _historyList[_nextUndo];
            command.Undo(_drawingCanvas);
            _nextUndo--;
            RaiseStateChangedEvent();
        }
        public void Redo()
        {
            if (!CanRedo)
                return;

            int itemToRedo = _nextUndo + 1;
            CommandBase command = _historyList[itemToRedo];
            command.Redo(_drawingCanvas);
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
