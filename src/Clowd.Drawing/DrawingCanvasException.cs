using System;
using System.Runtime.Serialization;


namespace Clowd.Drawing
{
    /// <summary>
    /// Exception thrown by DrawingCanvas Load and Save methods
    /// </summary>
    [Serializable]      // make FxCop happy
    public class DrawingCanvasException : Exception
    {
        public DrawingCanvasException(string message)
            : base(message)
        {
        }

        public DrawingCanvasException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        // FxCop requirements
        public DrawingCanvasException()
            : base("Unknown error")
        {

        }

        protected DrawingCanvasException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

    }
}
