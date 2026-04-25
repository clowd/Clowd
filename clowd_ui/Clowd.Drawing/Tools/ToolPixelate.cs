using System.Linq;
using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolPixelate : ToolSelection
    {
        public ToolPixelate() : base(() => new Cursor(StandardCursorType.Cross))
        {
        }

        protected override void MakeSelection(DrawingCanvas canvas, Rect selectedArea)
        {
            var images = canvas.GraphicsList.OfType<GraphicImage>().ToArray();
            if (!images.Any()) return;

            foreach (var g in images)
            {
                g.AddObscuredArea(selectedArea, canvas.BlurRadius);
            }
            canvas.AddCommandToHistory(false);
        }
    }
}
