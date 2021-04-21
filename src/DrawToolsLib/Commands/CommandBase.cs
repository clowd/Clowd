// Undo-Redo code is written using the article:
// http://www.codeproject.com/cs/design/commandpatterndemo.asp
// The Command Pattern and MVC Architecture
// By David Veeneman.

namespace DrawToolsLib
{
    internal abstract class CommandBase
    {
        public abstract void Undo(DrawingCanvas drawingCanvas);

        public abstract void Redo(DrawingCanvas drawingCanvas);
    }
}
