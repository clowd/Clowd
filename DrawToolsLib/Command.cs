using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


/// Undo-Redo code is written using the article:
/// http://www.codeproject.com/cs/design/commandpatterndemo.asp
//  The Command Pattern and MVC Architecture
//  By David Veeneman.

namespace DrawToolsLib
{
    /// <summary>
    /// Base class for commands used for Undo - Redo
    /// </summary>
    abstract class CommandBase
    {
        // This function is used to make Undo operation.
        // It makes action opposite to the original command.
        public abstract void Undo(DrawingCanvas drawingCanvas);

        // This command is used to make Redo operation.
        // It makes original command again.
        public abstract void Redo(DrawingCanvas drawingCanvas);

        // Derived classes have members which contain enough information
        // to make Undo and Redo operations for every specific command.
    }
}
