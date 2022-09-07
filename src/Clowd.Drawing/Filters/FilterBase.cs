using System;
using System.Collections.Generic;
using System.Windows;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Filters
{
    internal abstract class FilterBase : IDisposable
    {
        public abstract bool Start(DrawingBrush brush, Point p, DrawingCanvas canvas);
        
        public abstract void Handle(DrawingBrush brush, Point p);

        public abstract void Dispose();
    }
}
