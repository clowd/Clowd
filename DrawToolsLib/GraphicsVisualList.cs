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
        public int VisualCount => _graphics.Count + _extraVisuals.Count;
        public bool IsReadOnly => ((ICollection<GraphicBase>)_graphics).IsReadOnly;

        private readonly List<GraphicBase> _graphics;
        private readonly VisualCollection _visuals;
        private readonly ReaderWriterLockSlim _lock;
        private readonly DrawingCanvas _parent;

        private readonly List<UIElement> _extraVisuals;
        private Dictionary<GraphicBase, List<UIElement>> _extraLookup;


        public GraphicsVisualList(DrawingCanvas parent)
        {
            _parent = parent;
            _lock = new ReaderWriterLockSlim();
            _visuals = new VisualCollection(parent);
            _graphics = new List<GraphicBase>();
            _extraVisuals = new List<UIElement>();
            _extraLookup = new Dictionary<GraphicBase, List<UIElement>>();
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
                return _extraVisuals[index - _visuals.Count];
            }
        }

        internal void RegisterSubElement(GraphicBase graphic, UIElement subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                List<UIElement> vlist;
                if (!_extraLookup.ContainsKey(graphic))
                    vlist = _extraLookup[graphic] = new List<UIElement>();
                else
                    vlist = _extraLookup[graphic];

                if (!vlist.Contains(subVisual))
                {
                    vlist.Add(subVisual);
                    _extraVisuals.Add(subVisual);
                    _parent.Children.Add(subVisual);
                }
            }
        }

        internal void RemoveSubElement(GraphicBase graphic, UIElement subVisual)
        {
            using (new WriteLockContext(_lock))
            {
                RemoveSubElementUnsafe(graphic, subVisual);
            }
        }

        private void RemoveSubElementUnsafe(GraphicBase graphic, UIElement subVisual)
        {
            if (_extraLookup.ContainsKey(graphic))
            {
                var vlist = _extraLookup[graphic];
                if (vlist.Remove(subVisual))
                {
                    _parent.Children.Remove(subVisual);
                    _extraVisuals.Remove(subVisual);
                }
            }
        }

        private void RemoveSubElementsUnsafe(GraphicBase graphic)
        {
            if (_extraLookup.ContainsKey(graphic))
            {
                var vlist = _extraLookup[graphic];
                vlist.ToList().ForEach(v => RemoveSubElementUnsafe(graphic, v));
                _extraLookup.Remove(graphic);
            }
        }

        public void Clear()
        {
            using (new WriteLockContext(_lock))
            {
                _graphics.ForEach(g => g.ResetInvalidateEvent());
                _graphics.Clear();
                _extraVisuals.Clear();
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
