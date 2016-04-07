using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal sealed class GraphicsVisual : DrawingVisual, IDisposable
    {
        public GraphicsBase Graphic => _graphic;

        public int ObjectId => _graphic.ObjectId;

        public Color ObjectColor
        {
            get { return _graphic.ObjectColor; }
            set { _graphic.ObjectColor = value; }
        }
        public double LineWidth
        {
            get { return _graphic.LineWidth; }
            set { _graphic.LineWidth = value; }
        }
        public bool IsSelected
        {
            get { return _graphic.IsSelected; }
            set { _graphic.IsSelected = value; }
        }

        private readonly GraphicsBase _graphic;
        public GraphicsVisual(GraphicsBase graphic)
        {
            _graphic = graphic;
            _graphic.Invalidated += GraphicOnInvalidated;
        }

        private void GraphicOnInvalidated(object sender, EventArgs eventArgs)
        {
            RefreshDrawing();
        }
        public void RefreshDrawing()
        {
            DrawingContext dc = this.RenderOpen();
            _graphic.Draw(dc);
            dc.Close();
        }
        public void Dispose()
        {
            _graphic.Invalidated -= GraphicOnInvalidated;
        }
    }
}
