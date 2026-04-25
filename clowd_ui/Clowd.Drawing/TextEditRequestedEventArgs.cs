using System;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    /// <summary>
    /// Raised by <see cref="DrawingCanvas.TextEditRequested"/> when the user
    /// double-clicks a <see cref="GraphicText"/> and the shell should open
    /// an in-place editor. The shell reads <see cref="Target"/>, positions
    /// its own TextBox on top of the graphic, and commits changes back to
    /// <see cref="GraphicText.Body"/>.
    /// </summary>
    public sealed class TextEditRequestedEventArgs : EventArgs
    {
        public GraphicText Target { get; }

        public TextEditRequestedEventArgs(GraphicText target)
        {
            Target = target;
        }
    }
}
