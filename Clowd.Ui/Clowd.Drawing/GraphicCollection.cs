using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    /// <summary>
    /// Holds the set of graphics that make up a drawing. In the WPF original
    /// this also owned a <c>VisualCollection</c> + per-graphic
    /// <c>DrawingVisual</c>; in the Avalonia port we draw everything from
    /// <c>DrawingCanvas.Render</c>, so this collection is a pure model that
    /// raises a <see cref="Changed"/> event whenever the host needs to
    /// re-render.
    /// </summary>
    public sealed class GraphicCollection : SimpleNotifyObject, ICollection<GraphicBase>
    {
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

        /// <summary>
        /// Effective DPI / control scale used when sizing selection handles.
        /// The host (DrawingCanvas) sets this and re-renders when it changes.
        /// </summary>
        public DpiScale Dpi
        {
            get => _dpi;
            set
            {
                if (Set(ref _dpi, value))
                {
                    RaiseChanged();
                }
            }
        }

        /// <summary>
        /// Fired whenever the canvas should re-render: collection mutations,
        /// any graphic property change, or DPI changes.
        /// </summary>
        public event EventHandler? Changed;

        private Rect _contentBounds;
        private DpiScale _dpi = DpiScale.Default;
        private GraphicBase[] _selectedItems = Array.Empty<GraphicBase>();
        private readonly List<GraphicBase> _graphics;

        public GraphicCollection()
        {
            _graphics = new List<GraphicBase>();
        }

        public void Add(GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            _graphics.Add(graphic);
            graphic.PropertyChanged += GraphicPropertyChanged;
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
            RaiseChanged();
        }

        public void Insert(int index, GraphicBase graphic)
        {
            if (this.Any(f => f.Id == graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            _graphics.Insert(index, graphic);
            graphic.PropertyChanged += GraphicPropertyChanged;
            InvalidateBounds();
            if (graphic.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
            RaiseChanged();
        }

        private void GraphicPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            InvalidateBounds();
            if (e.PropertyName == nameof(GraphicBase.IsSelected))
                InvalidateSelected();
            RaiseChanged();
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
            g.PropertyChanged -= GraphicPropertyChanged;
            g.DisconnectFromParent();
            _graphics.RemoveAt(index);
            InvalidateBounds();
            if (g.IsSelected) InvalidateSelected();
            OnPropertyChanged(nameof(Count));
            RaiseChanged();
        }

        public void Clear()
        {
            foreach (var g in _graphics)
            {
                if (g != null)
                {
                    g.PropertyChanged -= GraphicPropertyChanged;
                    g.DisconnectFromParent();
                }
            }
            _graphics.Clear();
            InvalidateBounds();
            InvalidateSelected();
            OnPropertyChanged(nameof(Count));
            RaiseChanged();
        }

        public GraphicBase this[int index] => _graphics[index];

        public bool Contains(GraphicBase graphic) => _graphics.Contains(graphic);
        public void CopyTo(GraphicBase[] array, int arrayIndex) => _graphics.CopyTo(array, arrayIndex);
        public int IndexOf(GraphicBase item) => _graphics.IndexOf(item);
        public IEnumerator<GraphicBase> GetEnumerator() => _graphics.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _graphics.GetEnumerator();

        private void InvalidateSelected()
        {
            var selected = _graphics.Where(g => g.IsSelected).ToArray();
            if (!selected.SequenceEqual(SelectedItems))
            {
                SelectedItems = selected;
            }
        }

        private void InvalidateBounds()
        {
            ContentBounds = GetArtworkBounds();
        }

        private Rect GetArtworkBounds()
        {
            if (_graphics.Count == 0)
                return default;

            var artwork = _graphics.Where(g => !g.IsScaffolding);

            Rect result = default;
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

        // ---- Z-order operations ----

        public void MoveToFront(GraphicBase g)
        {
            var idx = _graphics.IndexOf(g);
            if (idx < 0 || idx == _graphics.Count - 1) return;
            _graphics.RemoveAt(idx);
            _graphics.Add(g);
            RaiseChanged();
        }

        public void MoveToBack(GraphicBase g)
        {
            var idx = _graphics.IndexOf(g);
            if (idx <= 0) return;
            _graphics.RemoveAt(idx);
            _graphics.Insert(0, g);
            RaiseChanged();
        }

        public void MoveForward(GraphicBase g)
        {
            var idx = _graphics.IndexOf(g);
            if (idx < 0 || idx == _graphics.Count - 1) return;
            _graphics.RemoveAt(idx);
            _graphics.Insert(idx + 1, g);
            RaiseChanged();
        }

        public void MoveBackward(GraphicBase g)
        {
            var idx = _graphics.IndexOf(g);
            if (idx <= 0) return;
            _graphics.RemoveAt(idx);
            _graphics.Insert(idx - 1, g);
            RaiseChanged();
        }

        public GraphicBase[] GetGraphicList(bool selectedOnly)
        {
            if (selectedOnly)
            {
                return _graphics
                    .Where(g => !g.IsScaffolding)
                    .Where(g => g.IsSelected)
                    .ToArray();
            }
            else
            {
                return _graphics
                    .Where(g => !g.IsScaffolding)
                    .ToArray();
            }
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
