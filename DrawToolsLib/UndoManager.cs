using System;
using System.Collections.Generic;
using System.Text;

/// Undo-Redo code is written using the article:
/// http://www.codeproject.com/cs/design/commandpatterndemo.asp
//  The Command Pattern and MVC Architecture
//  By David Veeneman.


namespace DrawToolsLib
{
    /// <summary>
    /// Class is responsible for executing Undo - Redo operations
    /// </summary>
    class UndoManager
    {
        #region Class Members

        DrawingCanvas drawingCanvas;

        List<CommandBase> historyList;
        int nextUndo;

        /// <summary>
        /// This event is raised after any operation which can change
        /// UndoManager state. Client can subscribe to this event and
        /// check CanUndo, CanRedo and IsDirty values.
        /// </summary>
        public event EventHandler StateChanged;


        #endregion  Class Members

        #region Constructor

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            this.drawingCanvas = drawingCanvas;

            ClearHistory();
        }

        #endregion Constructor

        #region Properties

        /// <summary>
        /// Return true if Undo operation is available
        /// </summary>
        public bool CanUndo
        {
            get
            {
                // If the NextUndo pointer is -1, no commands to undo
                if (nextUndo < 0 ||
                    nextUndo > historyList.Count - 1)   // precaution
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Return true if Redo operation is available
        /// </summary>
        public bool CanRedo
        {
            get
            {
                // If the NextUndo pointer points to the last item, no commands to redo
                if (nextUndo == historyList.Count - 1)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Returns dirty flag for client document.
        /// Document is dirty if it is possible to make Undo operation -
        /// I hope this is correct.
        /// 
        /// This can be changed if history has restricted length.
        /// Once history is trimmed from the beginning, IsDirty should
        /// be always true.
        /// </summary>
        public bool IsDirty
        {
            get
            {
                return CanUndo;
            }
        }

        #endregion Properties

        #region Public Functions

        /// <summary>
        /// Clear History
        /// </summary>
        public void ClearHistory()
        {
            historyList = new List<CommandBase>();
            nextUndo = -1;

            
            RaiseStateChangedEvent();
        }

        /// <summary>
        /// Add new command to history.
        /// Called by client after executing some action.
        /// </summary>
        /// <param name="command"></param>
        public void AddCommandToHistory(CommandBase command)
        {
            // Purge history list
            this.TrimHistoryList();

            // Add command and increment undo counter
            historyList.Add(command);

            nextUndo++;

            RaiseStateChangedEvent();
        }

        /// <summary>
        /// Undo
        /// </summary>
        public void Undo()
        {
            if (!CanUndo)
            {
                return;
            }

            // Get the Command object to be undone
            CommandBase command = historyList[nextUndo];

            // Execute the Command object's undo method
            command.Undo(drawingCanvas);

            // Move the pointer up one item
            nextUndo--;

            RaiseStateChangedEvent();
        }

        /// <summary>
        /// Redo
        /// </summary>
        public void Redo()
        {
            if (!CanRedo)
            {
                return;
            }

            // Get the Command object to redo
            int itemToRedo = nextUndo + 1;
            CommandBase command = historyList[itemToRedo];

            // Execute the Command object
            command.Redo(drawingCanvas);

            // Move the undo pointer down one item
            nextUndo++;

            RaiseStateChangedEvent();
        }

        #endregion Public Functions

        #region Private Functions

        private void TrimHistoryList()
        {
            // We can redo any undone command until we execute a new 
            // command. The new command takes us off in a new direction,
            // which means we can no longer redo previously undone actions. 
            // So, we purge all undone commands from the history list.*/

            // Exit if no items in History list
            if (historyList.Count == 0)
            {
                return;
            }

            // Exit if NextUndo points to last item on the list
            if (nextUndo == historyList.Count - 1)
            {
                return;
            }

            // Purge all items below the NextUndo pointer
            for (int i = historyList.Count - 1; i > nextUndo; i--)
            {
                historyList.RemoveAt(i);
            }
        }

        /// <summary>
        /// Raise UndoManagerOperation event.
        /// </summary>
        private void RaiseStateChangedEvent()
        {
            if ( StateChanged != null )
            {
                StateChanged(this, EventArgs.Empty);
            }
        }

        #endregion
    }
}
