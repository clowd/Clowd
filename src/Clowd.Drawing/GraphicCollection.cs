using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Clowd.Drawing.Graphics;
using RT.Serialization;

namespace Clowd.Drawing
{
    public sealed class GraphicCollection : SimpleNotifyObject, ICollection<GraphicBase>
    {
        public int VisualCount => _visuals.Count;

        public int Count => _graphics.Count;

        public bool IsReadOnly => false;

        public GraphicBase[] SelectedItems
        {
            get => _selectedItems;
            private set => Set(ref _selectedItems, value);
        }

        public Rect ContentBounds
        {
            get => _contentBounds;
            private set => Set(ref _contentBounds, value);
        }

        internal DpiScale Dpi
        {
            get => _dpi;
            set
            {
                // if the DPI has changed, the selected elements need to be re-drawn
                if (Set(ref _dpi, value))
                {
                    InvalidateDpi();
                }
            }
        }

        private Rect _contentBounds;
        private DpiScale _dpi;
        private GraphicBase[] _selectedItems = new GraphicBase[0];
        private readonly List<GraphicBase> _graphics;
        private readonly VisualCollection _visuals;

        internal GraphicCollection(DrawingCanvas parent)
        {
            _visuals = new VisualCollection(parent);
            _graphics = new List<GraphicBase>();
            _dpi = parent.CanvasUiElementScale;
        }

        public void Add(GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            var vis = new DrawingVisual();
            _graphics.Add(graphic);
            _visuals.Add(vis);
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, vis, args);
            DrawGraphic(graphic, vis);
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        public void Insert(int index, GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            var vis = new DrawingVisual();
            _graphics.Insert(index, graphic);
            _visuals.Insert(index, vis);
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, vis, args);
            DrawGraphic(graphic, vis);
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        private void GraphicPropertyChanged(GraphicBase graphic, DrawingVisual visual, PropertyChangedEventArgs e)
        {
            DrawGraphic(graphic, visual);
            InvalidateBounds();

            if (e.PropertyName == nameof(GraphicBase.IsSelected))
                InvalidateSelected();
        }

        public bool Remove(GraphicBase graphic)
        {
            var index = _graphics.IndexOf(graphic);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            var g = _graphics[index];
            g.DisconnectFromParent();
            _graphics.RemoveAt(index);
            _visuals.RemoveAt(index);
            InvalidateBounds();
            if (g.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        public void Clear()
        {
            _graphics.ForEach(g => g?.DisconnectFromParent());
            _graphics.Clear();
            _visuals.Clear();
            InvalidateBounds();
            InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        internal DrawingVisual DrawGraphicsToVisual(Brush backgroundBrush)
        {
            // note, this method loses all bitmap effects,
            // but the alternative requires recursive painting with VisualBrush and produces really poor text rendering
            // it might be preferrable to flatten a bitmap first with DrawGraphicsToBitmap and then paint this on a visual
            // if the bitmap effects are required. Since this function is only used for printing, I think it's fine for now.

            var gl = GetGraphicList(false);
            var bounds = ContentBounds;
            var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

            if (bounds.Width < 1 || bounds.Height < 1)
                return null;

            DrawingVisual vs = new DrawingVisual();
            using (DrawingContext dc = vs.RenderOpen())
            {
                dc.PushTransform(transform);

                // draw background
                if (backgroundBrush != null)
                    dc.DrawRectangle(backgroundBrush, null, bounds);

                // draw all graphics (without any selection handles etc)
                foreach (var g in gl)
                    g.DrawObject(dc);
            }

            return vs;
        }

        internal BitmapSource DrawGraphicsToBitmap(Brush backgroundBrush)
        {
            var gl = GetGraphicList(false);
            var bounds = ContentBounds;
            var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

            if (bounds.Width < 1 || bounds.Height < 1)
                return null;

            RenderTargetBitmap bmp = new RenderTargetBitmap(
                (int)bounds.Width,
                (int)bounds.Height,
                96,
                96,
                PixelFormats.Pbgra32);

            // draw background
            if (backgroundBrush != null)
            {
                DrawingVisual background = new DrawingVisual();
                using (DrawingContext dc = background.RenderOpen())
                {
                    dc.PushTransform(transform);
                    dc.DrawRectangle(backgroundBrush, null, bounds);
                }

                bmp.Render(background);
            }

            // draw all graphics (without any selection handles etc)
            foreach (var g in gl)
            {
                DrawingVisual v = new DrawingVisual();
                DrawGraphic(g, v, true, transform);
                bmp.Render(v);
            }

            return bmp;
        }

        public Visual GetVisual(int index) => _visuals[index];

        public GraphicBase this[int index] => _graphics[index];

        // misc ICollection
        public bool Contains(GraphicBase graphic) => _graphics.Contains(graphic);
        public void CopyTo(GraphicBase[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(GraphicBase item) => _graphics.IndexOf(item);
        public IEnumerator<GraphicBase> GetEnumerator() => _graphics.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _graphics.GetEnumerator();

        private void DrawGraphic(GraphicBase g, DrawingVisual v, bool objectOnly = false, Transform transform = null)
        {
            // update drop shadow effect
            if (g.DropShadowEffect && v.Effect == null)
                v.Effect = new DropShadowEffect()
                {
                    Opacity = 0.5,
                    ShadowDepth = 2,
                    RenderingBias = RenderingBias.Performance
                };
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
                    g.Draw(c, Dpi);
                }

                if (transform != null)
                    c.Pop();
            }
        }

        private void InvalidateSelected()
        {
            var selected = this.Where(g => g.IsSelected).ToArray();
            if (!Enumerable.SequenceEqual(SelectedItems, selected))
            {
                SelectedItems = selected;
            }
        }

        private void InvalidateBounds()
        {
            ContentBounds = GetArtworkBounds();
        }

        private void InvalidateDpi()
        {
            // if the zoom has changed, selected items need to be re-rendered as ui controls are scaled
            for (int i = 0; i < _graphics.Count; i++)
            {
                var g = _graphics[i];
                var v = _visuals[i] as DrawingVisual;
                if (g?.IsSelected == true && v != null)
                    DrawGraphic(g, v);
            }
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

        public GraphicBase[] GetGraphicList(bool selectedOnly)
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
