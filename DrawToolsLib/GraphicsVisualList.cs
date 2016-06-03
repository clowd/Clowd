using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    public class GraphicsVisualList : IList<GraphicBase>
    {
        private List<GraphicBase> _graphics;
        private VisualCollection _visuals;
        private ReaderWriterLockSlim _lock;

        public GraphicsVisualList(Visual parent)
        {
            _lock = new ReaderWriterLockSlim();
            _visuals = new VisualCollection(parent);
            _graphics = new List<GraphicBase>();
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

        public Visual GetItemVisual(int index)
        {
            using (new ReadLockContext(_lock))
                return _visuals[index];
        }

        public void Clear()
        {
            using (new WriteLockContext(_lock))
            {
                _graphics.ForEach(g => g.ResetInvalidateEvent());
                _graphics.Clear();
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
                _graphics.RemoveAt(index);
                _visuals.RemoveAt(index);
                return true;
            }
        }

        public int Count
        {
            get
            {
                return _graphics.Count;
            }
        }

        public bool IsReadOnly
        {
            get
            {
                return ((ICollection<GraphicBase>)_graphics).IsReadOnly;
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
                _graphics[index].ResetInvalidateEvent();
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
