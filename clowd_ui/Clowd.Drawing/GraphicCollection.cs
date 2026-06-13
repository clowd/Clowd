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

        /// <summary>
        /// Bulk add used when restoring an undo snapshot: one bounds/selection/Count invalidation
        /// for the whole batch instead of per graphic, and a single id set for the duplicate check
        /// instead of an O(n) scan per add.
        /// </summary>
        internal void AddRange(IEnumerable<GraphicBase> graphics)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in _graphics)
                ids.Add(g.Id);

            bool anySelected = false;
            foreach (var graphic in graphics)
            {
                // we should not ever allow duplicate object id's
                if (!ids.Add(graphic.Id))
                {
                    graphic.Id = Guid.NewGuid().ToString();
                    ids.Add(graphic.Id);
                }

                var vis = new GraphicVisual(graphic, this);
                var captured = graphic;
                _graphics.Add(graphic);
                _visuals.Add(vis);
                graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(captured, vis, args);
                anySelected |= graphic.IsSelected;
            }

            InvalidateBounds();
            if (anySelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
        }

        private void GraphicPropertyChanged(GraphicBase graphic, GraphicVisual visual, PropertyChangedEventArgs e)
        {
            // every property change of every graphic funnels through here — a drag raises several
            // of these per pointer move — so only do the work the change can actually require
            if (e.PropertyName == nameof(GraphicBase.DropShadowEffect))
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

            // draw all graphics (without any selection handles etc). each visual is pinned at
            // (0,0) spanning the whole bitmap — RenderTargetBitmap.Render culls visuals whose
            // arranged rect misses the target (zero-sized or offset out of view), so the
            // graphic-space translation happens inside GraphicVisual.Render instead.
            // NOTE: per-graphic DropShadowEffect is not honored by RenderTargetBitmap.Render
            // (immediate path; compositor-only feature) — so shadows are baked into bitmaps via
            // ShadowRenderer and composited directly under each graphic, preserving z-order.
            foreach (var g in gl)
            {
                var vis = new GraphicVisual(g)
                {
                    ObjectOnly = true,
                    Width = width,
                    Height = height,
                    ObjectOffset = new Vector(-bounds.Left, -bounds.Top),
                    Effect = null, // ignored by RTB.Render today; cleared so a future compositing backend can't double the shadow
                };

                if (g.DropShadowEffect)
                {
                    vis.ShadowBitmap = ShadowRenderer.Render(g, out var shadowPos);
                    vis.ShadowPosition = shadowPos;
                }

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
        public int IndexOf(Predicate<GraphicBase> predicate) => _graphics.FindIndex(predicate);
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
            // recomputed on every property change of every graphic (several times per pointer
            // move during a drag) — plain loop, no LINQ/iterator allocations
            Rect result = default;
            bool first = true;
            for (int i = 0; i < _graphics.Count; i++)
            {
                var item = _graphics[i];
                if (item is GraphicSelectionRectangle)
                    continue;

                var rect = item.Bounds;
                result = first ? rect : result.Union(rect);
                first = false;
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
