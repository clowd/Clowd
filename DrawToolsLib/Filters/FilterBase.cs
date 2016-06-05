using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Filters
{
    internal abstract class FilterBase
    {
        public DrawingCanvas Canvas { get; }
        public GraphicImage Source { get; }

        protected FilterBase(DrawingCanvas canvas, GraphicImage source)
        {
            Canvas = canvas;
            Source = source;
        }

        public abstract void Handle(DrawingBrush brush, Point p);

        public abstract void Close();
    }
}
