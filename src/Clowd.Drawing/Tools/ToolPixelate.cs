﻿using System.Linq;
using System.Windows;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolPixelate : ToolSelection
    {
        public ToolPixelate() : base(() => CursorResources.Obscure)
        {
        }

        protected override void MakeSelection(DrawingCanvas canvas, Rect selectedArea)
        {
            var images = canvas.GraphicsList.OfType<GraphicImage>().ToArray();
            if (images.Any())
            {
                foreach (var g in images)
                {
                    g.AddObscuredArea(selectedArea, canvas.BlurRadius);
                }

                canvas.AddCommandToHistory(false);
            }
        }
    }
}
