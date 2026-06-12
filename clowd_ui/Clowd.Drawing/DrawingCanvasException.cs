using System;

namespace Clowd.Drawing
{
    /// <summary>
    /// Exception thrown by DrawingCanvas Load and Save methods
    /// </summary>
    public class DrawingCanvasException : Exception
    {
        public DrawingCanvasException(string message)
            : base(message)
        { }

        public DrawingCanvasException(string message, Exception innerException)
            : base(message, innerException)
        { }

        public DrawingCanvasException()
            : base("Unknown error")
        { }
    }
}
