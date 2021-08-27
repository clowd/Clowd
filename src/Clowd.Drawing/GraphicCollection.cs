using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Clowd.Drawing.Graphics;
using RT.Serialization;

namespace Clowd.Drawing
{
    public class GraphicCollection : SimpleNotifyObject, ICollection<GraphicBase>
    {
        public int VisualCount => _visuals.Count;
        public int Count => _graphics.Count;
        public bool IsReadOnly => false;
        public Rect ContentBounds { get; private set; }

        private readonly DrawingCanvas _parent;
        private readonly List<GraphicBase> _graphics;
        private readonly VisualCollection _visuals;
        private readonly object _lock = new object();

        public GraphicCollection(DrawingCanvas parent)
        {
            _parent = parent;
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
                graphic.PropertyChanged += (sender, args) => DrawGraphic(graphic, vis);
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
                graphic.PropertyChanged += (sender, args) => DrawGraphic(graphic, vis);
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
                var g = _graphics[index];
                g.DisconnectFromParent();
                _graphics.RemoveAt(index);
                _visuals.RemoveAt(index);
                InvalidateBounds();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _graphics.ForEach(g => g?.DisconnectFromParent());
                _graphics.Clear();
                _visuals.Clear();
                InvalidateBounds();
            }
        }

        public byte[] Serialize()
        {
            var gl = GetGraphicList(false);
            return ClassifyBinary.Serialize(gl);
        }

        public byte[] SerializeSelected()
        {
            var gl = GetGraphicList(true);
            return ClassifyBinary.Serialize(gl);
        }

        public void DeserializeObjectsInto(byte[] bytes)
        {
            lock (_lock)
            {
                foreach (var g in ClassifyBinary.Deserialize<GraphicBase[]>(bytes))
                    Add(g);
            }
        }

        public DrawingVisual DrawGraphicsToVisual()
        {
            // note, this method loses all bitmap effects,
            // but the alternative requires recursive painting with VisualBrush and produces really poor text rendering
            // it might be preferrable to flatten a bitmap first with DrawGraphicsToBitmap and then paint this on a visual

            lock (_lock)
            {
                var gl = GetGraphicList(false);
                var bounds = ContentBounds;
                var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

                DrawingVisual vs = new DrawingVisual();
                using (DrawingContext dc = vs.RenderOpen())
                {
                    dc.PushTransform(transform);
                    foreach (var g in gl)
                        g.DrawObject(dc);
                }

                return vs;
            }
        }

        public BitmapSource DrawGraphicsToBitmap()
        {
            lock (_lock)
            {
                var gl = GetGraphicList(false);
                var bounds = ContentBounds;
                var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

                RenderTargetBitmap bmp = new RenderTargetBitmap(
                    (int)bounds.Width,
                    (int)bounds.Height,
                    96,
                    96,
                    PixelFormats.Pbgra32);

                foreach (var g in gl)
                {
                    DrawingVisual v = new DrawingVisual();
                    DrawGraphic(g, v, true, false, transform);
                    bmp.Render(v);
                }

                return bmp;
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

        private void DrawGraphic(GraphicBase g, DrawingVisual v, bool objectOnly = false, bool invalidate = true, Transform transform = null)
        {
            // update drop shadow effect
            if (g.DropShadowEffect && v.Effect == null)
                v.Effect = new DropShadowEffect() { Opacity = 0.5, ShadowDepth = 2, RenderingBias = RenderingBias.Performance };
            else if (!g.DropShadowEffect && v.Effect != null)
                v.Effect = null;

            using (var c = v.RenderOpen())
            {
                if (transform != null)
                    c.PushTransform(transform);

                if (objectOnly)
                {
                    g.DrawObject(c);
                }
                else
                {
                    // get dpi of editor window so resize handles can be scaled
                    var dpi = VisualTreeHelper.GetDpi(_parent);
                    g.Draw(c, dpi);
                }

                if (transform != null)
                    c.Pop();
            }

            if (invalidate)
                InvalidateBounds();
        }

        private void InvalidateBounds()
        {
            ContentBounds = GetArtworkBounds();
            OnPropertyChanged(nameof(ContentBounds));
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

        private GraphicBase[] GetGraphicList(bool selectedOnly)
        {
            if (selectedOnly)
            {
                return _graphics
                    .Where(g => !(g is GraphicSelectionRectangle))
                    .Where(g => g.IsSelected)
                    .ToArray();
            }
            else
            {
                return _graphics
                    .Where(g => !(g is GraphicSelectionRectangle))
                    .ToArray();
            }
        }
    }
}
