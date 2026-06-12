using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.Drawing.Graphics;

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
        private readonly List<GraphicVisual> _visuals;

        public GraphicCollection(DrawingCanvas parent)
        {
            _graphics = new List<GraphicBase>();
            _visuals = new List<GraphicVisual>();
            _dpi = parent.CanvasUiElementScale;
        }

        public void Add(GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            var vis = new GraphicVisual(graphic, this);
            _graphics.Add(graphic);
            _visuals.Add(vis);
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, vis, args);
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        public void Insert(int index, GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            var vis = new GraphicVisual(graphic, this);
            _graphics.Insert(index, graphic);
            _visuals.Insert(index, vis);
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, vis, args);
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        private void GraphicPropertyChanged(GraphicBase graphic, GraphicVisual visual, PropertyChangedEventArgs e)
        {
            visual.UpdateEffect();
            visual.InvalidateVisual();
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

        /// <summary>
        /// Renders the artwork (no selection chrome) into a 96-dpi bitmap, preserving drop
        /// shadow effects, via a transient unrooted canvas (§2.10 / decision table #20).
        /// </summary>
        public Bitmap DrawGraphicsToBitmap(IBrush background)
        {
            var gl = GetGraphicList(false);
            var bounds = ContentBounds;

            if (bounds.Width < 1 || bounds.Height < 1)
                return null;

            var width = (int)Math.Ceiling(bounds.Width);
            var height = (int)Math.Ceiling(bounds.Height);

            var canvas = new Canvas
            {
                Width = width,
                Height = height,
            };

            // draw background
            if (background != null)
            {
                canvas.Children.Add(new Border
                {
                    Background = background,
                    Width = width,
                    Height = height,
                });
            }

            // draw all graphics (without any selection handles etc)
            foreach (var g in gl)
            {
                var vis = new GraphicVisual(g) { ObjectOnly = true };
                Canvas.SetLeft(vis, -bounds.Left);
                Canvas.SetTop(vis, -bounds.Top);
                canvas.Children.Add(vis);
            }

            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));

            var bmp = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bmp.Render(canvas);
            return bmp;
        }

        internal GraphicVisual GetVisual(int index) => _visuals[index];

        public GraphicBase this[int index] => _graphics[index];

        // misc ICollection
        public bool Contains(GraphicBase graphic) => _graphics.Contains(graphic);
        public void CopyTo(GraphicBase[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(GraphicBase item) => _graphics.IndexOf(item);
        public IEnumerator<GraphicBase> GetEnumerator() => _graphics.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _graphics.GetEnumerator();

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
                if (g?.IsSelected == true)
                    _visuals[i].InvalidateVisual();
            }
        }

        private Rect GetArtworkBounds()
        {
            if (_graphics.Count == 0)
                return default;

            var artwork = _graphics.Where(g => !(g is GraphicSelectionRectangle));

            Rect result = new Rect(0, 0, 0, 0);
            bool first = true;
            foreach (var item in artwork)
            {
                var rect = item.Bounds;
                if (first)
                {
                    result = rect;
                    first = false;
                    continue;
                }

                result = result.Union(rect);
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
