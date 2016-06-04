using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    public class GraphicsVisualList : IList<GraphicBase>
    {
        public int Count => _graphics.Count;
        public int VisualCount => _graphics.Count + _extraLookup.Values.Sum(sub => sub.VisualCount);
        public bool IsReadOnly => ((ICollection<GraphicBase>)_graphics).IsReadOnly;

        private readonly List<GraphicBase> _graphics;
        private readonly VisualCollection _visuals;
        private readonly ReaderWriterLockSlim _lock;
        private readonly DrawingCanvas _parent;

        private Dictionary<GraphicBase, SubElementContainer> _extraLookup;


        public GraphicsVisualList(DrawingCanvas parent)
        {
            _parent = parent;
            _lock = new ReaderWriterLockSlim();
            _visuals = new VisualCollection(parent);
            _graphics = new List<GraphicBase>();
            _extraLookup = new Dictionary<GraphicBase, SubElementContainer>();
        }

        public IEnumerator<GraphicBase> GetEnumerator()
        {
            return _graphics.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _graphics.GetEnumerator();
        }

        public void Add(GraphicBase graphic)
        {
            using (new WriteLockContext(_lock))
            {
                var vis = new DrawingVisual();
                _graphics.Add(graphic);
                _visuals.Add(vis);
                graphic.Invalidated += (sender, args) => DrawGraphic(graphic, vis);
                DrawGraphic(graphic, vis);
            }
        }

        public Visual GetVisual(int index)
        {
            using (new ReadLockContext(_lock))
            {
                if (index < _visuals.Count)
                    return _visuals[index];

                var subElementArray = _extraLookup.Values.ToArray();
                int searchIndex = _visuals.Count;

                // loop through all extra visuals first
                foreach (var sub in subElementArray)
                {
                    for (int visualIndex = 0; visualIndex < sub.Visuals.Count; visualIndex++)
                    {
                        if (searchIndex == index)
                            return sub.Visuals[visualIndex];

                        searchIndex++;
                    }
                }

                // then through all extra elements
                foreach (var sub in subElementArray)
                {
                    for (int elementIndex = 0; elementIndex < sub.Elements.Count; elementIndex++)
                    {
                        if (searchIndex == index)
                            return sub.Elements[elementIndex];

                        searchIndex++;
                    }
                }

                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        internal void RegisterSubElement(GraphicBase graphic, UIElement subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                SubElementContainer container;
                if (!_extraLookup.ContainsKey(graphic))
                    container = _extraLookup[graphic] = new SubElementContainer(_parent);
                else
                    container = _extraLookup[graphic];

                container.AddItemUnsafe(subVisual);
            }
        }

        internal void RemoveSubElement(GraphicBase graphic, UIElement subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                if (_extraLookup.ContainsKey(graphic))
                {
                    _extraLookup[graphic].RemoveItemUnsafe(subVisual);
                }
            }
        }

        internal void RegisterSubElement(GraphicBase graphic, Visual subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                SubElementContainer container;
                if (!_extraLookup.ContainsKey(graphic))
                    container = _extraLookup[graphic] = new SubElementContainer(_parent);
                else
                    container = _extraLookup[graphic];

                container.AddItemUnsafe(subVisual);
            }
        }

        internal void RemoveSubElement(GraphicBase graphic, Visual subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                if (_extraLookup.ContainsKey(graphic))
                {
                    _extraLookup[graphic].RemoveItemUnsafe(subVisual);
                }
            }
        }

        private void RemoveSubElementsUnsafe(GraphicBase graphic)
        {
            if (_extraLookup.ContainsKey(graphic))
            {
                var container = _extraLookup[graphic];
                container.RemoveAllUnsafe();
                _extraLookup.Remove(graphic);
            }
        }

        public void Clear()
        {
            using (new WriteLockContext(_lock))
            {
                _graphics.ForEach(g => g.ResetInvalidateEvent());
                _graphics.Clear();
                _extraLookup.Values.ToList().ForEach(cont => cont.RemoveAllUnsafe());
                _extraLookup.Clear();
                _visuals.Clear();
            }
        }

        public bool Contains(GraphicBase item)
        {
            using (new ReadLockContext(_lock))
                return _graphics.Contains(item);
        }

        public void CopyTo(GraphicBase[] array, int arrayIndex)
        {
            using (new ReadLockContext(_lock))
                _graphics.CopyTo(array, arrayIndex);
        }

        public bool Remove(GraphicBase item)
        {
            using (new WriteLockContext(_lock))
            {
                var index = _graphics.IndexOf(item);
                if (index < 0)
                    return false;

                _graphics[index].ResetInvalidateEvent();
                RemoveSubElementsUnsafe(item);
                _graphics.RemoveAt(index);
                _visuals.RemoveAt(index);
                return true;
            }
        }

        public int IndexOf(GraphicBase item)
        {
            using (new ReadLockContext(_lock))
                return _graphics.IndexOf(item);
        }

        public void Insert(int index, GraphicBase item)
        {
            using (new WriteLockContext(_lock))
            {
                var vis = new DrawingVisual();
                _graphics.Insert(index, item);
                _visuals.Insert(index, vis);
                item.Invalidated += (sender, args) => DrawGraphic(item, vis);
                DrawGraphic(item, vis);
            }
        }

        public void RemoveAt(int index)
        {
            using (new WriteLockContext(_lock))
            {
                _graphics[index]?.ResetInvalidateEvent();
                RemoveSubElementsUnsafe(_graphics[index]);
                _graphics.RemoveAt(index);
                _visuals.RemoveAt(index);
            }
        }

        public GraphicBase this[int index]
        {
            get
            {
                using (new ReadLockContext(_lock))
                    return _graphics[index];
            }
            set
            {
                using (new WriteLockContext(_lock))
                {
                    _graphics[index].ResetInvalidateEvent();
                    RemoveSubElementsUnsafe(_graphics[index]);
                    _graphics.RemoveAt(index);
                    _visuals.RemoveAt(index);
                    var vis = new DrawingVisual();
                    _graphics.Insert(index, value);
                    _visuals.Insert(index, vis);
                    value.Invalidated += (sender, args) => DrawGraphic(value, vis);
                    DrawGraphic(value, vis);
                }
            }
        }

        private class SubElementContainer
        {
            public int VisualCount => Visuals.Count + Elements.Count;

            private readonly DrawingCanvas _canvas;
            public VisualCollection Visuals { get; }
            public List<UIElement> Elements { get; }

            public SubElementContainer(DrawingCanvas canvas)
            {
                Visuals = new VisualCollection(canvas);
                Elements = new List<UIElement>();
                _canvas = canvas;
            }

            public void AddItemUnsafe(Visual item)
            {
                if (Visuals.Contains(item))
                    return;

                Visuals.Add(item);
            }
            public void AddItemUnsafe(UIElement item)
            {
                if (Elements.Contains(item))
                    return;

                Elements.Add(item);
                _canvas.Children.Add(item);
            }

            public void RemoveItemUnsafe(Visual item)
            {
                if (!Visuals.Contains(item))
                    return;

                Visuals.Remove(item);
            }
            public void RemoveItemUnsafe(UIElement item)
            {
                if (!Elements.Contains(item))
                    return;

                Elements.Remove(item);
                _canvas.Children.Remove(item);
            }

            public void RemoveAllUnsafe()
            {
                Visuals.Cast<Visual>().ToList().ForEach(RemoveItemUnsafe);
                Elements.ToList().ForEach(RemoveItemUnsafe);
            }
        }

        private void DrawGraphic(GraphicBase g, DrawingVisual v)
        {
            v.Effect = g.Effect;
            using (var c = v.RenderOpen())
            {
                g.Draw(c);
            }
        }

        private class ReadLockContext : IDisposable
        {
            private readonly ReaderWriterLockSlim _lockSlim;
            private bool _locked;

            public ReadLockContext(ReaderWriterLockSlim lockSlim)
            {
                _lockSlim = lockSlim;
                _lockSlim.EnterReadLock();
                _locked = true;
            }

            public void Dispose()
            {
                if (_locked)
                {
                    _lockSlim.ExitReadLock();
                    _locked = false;
                }
            }
        }

        private class WriteLockContext : IDisposable
        {
            private readonly ReaderWriterLockSlim _lockSlim;
            private bool _locked;

            public WriteLockContext(ReaderWriterLockSlim lockSlim)
            {
                _lockSlim = lockSlim;
                _lockSlim.EnterWriteLock();
                _locked = true;
            }

            public void Dispose()
            {
                if (_locked)
                {
                    _lockSlim.ExitWriteLock();
                    _locked = false;
                }
            }
        }
    }
}
