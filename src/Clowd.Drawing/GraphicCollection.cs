using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    public class GraphicCollection : ICollection<GraphicBase>
    {
        public int VisualCount => _visuals.Count;
        public int Count => _graphics.Count;
        public bool IsReadOnly => false;
        public Rect ContentBounds { get; private set; }
        
        private readonly List<GraphicBase> _graphics;
        private readonly VisualCollection _visuals;
        private readonly object _lock = new object();

        public GraphicCollection(DrawingCanvas parent)
        {
            _visuals = new VisualCollection(parent);
            _graphics = new List<GraphicBase>();
        }

        public void Add(GraphicBase graphic)
        {
            lock (_lock)
            {
                var vis = new DrawingVisual();
                _graphics.Add(graphic);
                _visuals.Add(vis);
                graphic.Invalidated += (sender, args) => DrawGraphic(graphic, vis);
                DrawGraphic(graphic, vis);
            }
        }

        public void Insert(int index, GraphicBase graphic)
        {
            lock (_lock)
            {
                var vis = new DrawingVisual();
                _graphics.Insert(index, graphic);
                _visuals.Insert(index, vis);
                graphic.Invalidated += (sender, args) => DrawGraphic(graphic, vis);
                DrawGraphic(graphic, vis);
            }
        }

        public bool Remove(GraphicBase graphic)
        {
            lock (_lock)
            {
                var index = _graphics.IndexOf(graphic);
                if (index < 0) return false;
                RemoveAt(index);
                return true;
            }
        }

        public void RemoveAt(int index)
        {
            lock (_lock)
            {
                _graphics[index]?.ResetInvalidatedEvent();
                _graphics.RemoveAt(index);
                _visuals.RemoveAt(index);
                InvalidateBounds();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _graphics.ForEach(g => g.ResetInvalidatedEvent());
                _graphics.Clear();
                _visuals.Clear();
                InvalidateBounds();
            }
        }

        // indexers
        public Visual GetVisual(int index) => _visuals[index];
        public GraphicBase this[int index] => _graphics[index];

        // misc ICollection
        public bool Contains(GraphicBase graphic) => _graphics.Contains(graphic);
        public void CopyTo(GraphicBase[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(GraphicBase item) => _graphics.IndexOf(item);
        public IEnumerator<GraphicBase> GetEnumerator() => _graphics.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _graphics.GetEnumerator();

        private void DrawGraphic(GraphicBase g, DrawingVisual v)
        {
            v.Effect = g.Effect;
            using (var c = v.RenderOpen())
                g.Draw(c);
            InvalidateBounds();
        }

        private void InvalidateBounds()
        {
            ContentBounds = GetArtworkBounds();
        }

        private Rect GetArtworkBounds()
        {
            if (_graphics.Count == 0)
                return Rect.Empty;

            bool selectedOnly = false;

            var artwork = _graphics.Cast<GraphicBase>().Where(g => !(g is GraphicSelectionRectangle));

            Rect result = new Rect(0, 0, 0, 0);
            bool first = true;
            foreach (var item in artwork)
            {
                if (selectedOnly && !item.IsSelected)
                    continue;
                var rect = item.Bounds;
                if (first)
                {
                    result = rect;
                    first = false;
                    continue;
                }
                result.Union(rect);
            }
            return result;
        }
    }
}
