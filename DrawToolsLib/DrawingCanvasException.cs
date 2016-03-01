using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.Serialization;


namespace DrawToolsLib
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
            : base(Properties.Settings.Default.UnknownException)
        {

        }

        protected DrawingCanvasException(SerializationInfo info, StreamingContext context) 
            : base( info, context )
        { 
        }

    }
}
