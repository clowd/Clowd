using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Clowd.Video.Playback;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Triple-buffered <see cref="WriteableBitmap"/> pool behind an <see cref="Image"/>. The
    /// engine's present thread locks a free bitmap (WriteableBitmap.Lock is legal off the UI
    /// thread in Avalonia) and sws_scales BGRA directly into it; CompleteFrame posts the
    /// Image.Source swap. With one bitmap displayed and at most one swap pending, a third is
    /// always available — BeginFrame blocking briefly is the natural backpressure when the UI
    /// thread stalls.
    /// </summary>
    public sealed class WriteableBitmapFrameSink : IFrameSink, IDisposable
    {
        private const int PoolSize = 3;

        private readonly Image _image;
        private readonly object _sync = new object();
        private readonly WriteableBitmap[] _pool = new WriteableBitmap[PoolSize];
        private readonly bool[] _busy = new bool[PoolSize]; // displayed or pending a swap
        private int _displayed = -1;
        private int _width, _height;
        private int _version; // bumped when the pool is rebuilt at a new size
        private bool _disposed;

        public WriteableBitmapFrameSink(Image image)
        {
            _image = image;
        }

        /// <summary>Media pts of the most recently displayed frame (UI thread).</summary>
        public TimeSpan LastPresentedPts { get; private set; }

        public FrameTarget BeginFrame(int width, int height)
        {
            int index;
            lock (_sync)
            {
                if (_disposed)
                    return default;

                if (width != _width || height != _height)
                {
                    // (re)create the pool at the new size. The displayed bitmap may still be
                    // referenced by the Image/renderer — release it on the UI thread instead.
                    _version++;
                    for (int i = 0; i < PoolSize; i++)
                    {
                        if (_pool[i] != null)
                        {
                            if (i == _displayed)
                            {
                                var old = _pool[i];
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (ReferenceEquals(_image.Source, old))
                                        _image.Source = null;
                                    old.Dispose();
                                });
                            }
                            else
                            {
                                _pool[i].Dispose();
                            }
                        }

                        _pool[i] = null;
                        _busy[i] = false;
                    }

                    _displayed = -1;
                    _width = width;
                    _height = height;
                }

                index = TakeFreeSlotLocked();
                while (index < 0)
                {
                    // UI thread hasn't consumed the pending swap yet — wait for it (bounded).
                    if (!Monitor.Wait(_sync, 250) || _disposed)
                        return default;
                    index = TakeFreeSlotLocked();
                }

                _pool[index] ??= new WriteableBitmap(
                    new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            var fb = _pool[index].Lock();
            return new FrameTarget(fb.Address, fb.RowBytes, width, height, new LockToken(index, _version, fb));
        }

        private int TakeFreeSlotLocked()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_busy[i] && i != _displayed)
                {
                    _busy[i] = true;
                    return i;
                }
            }

            return -1;
        }

        public void CompleteFrame(in FrameTarget target, TimeSpan pts)
        {
            if (target.Token is not LockToken token)
                return;

            token.Framebuffer.Dispose(); // unlock (decode thread — legal)

            Dispatcher.UIThread.Post(() =>
            {
                lock (_sync)
                {
                    if (_disposed || token.Version != _version || _pool[token.Index] == null)
                    {
                        // pool was resized/disposed while the swap was in flight; the slot's
                        // busy flag was already reset by the rebuild.
                        if (!_disposed && token.Version == _version)
                        {
                            _busy[token.Index] = false;
                            Monitor.PulseAll(_sync);
                        }

                        return;
                    }

                    int previous = _displayed;
                    _displayed = token.Index;
                    if (previous >= 0)
                        _busy[previous] = false;
                    Monitor.PulseAll(_sync);
                }

                LastPresentedPts = pts;
                _image.Source = _pool[token.Index];
            }, DispatcherPriority.Render);
        }

        private sealed record LockToken(int Index, int Version, ILockedFramebuffer Framebuffer);

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                Monitor.PulseAll(_sync);
            }

            // bitmaps are disposed on the UI thread after the Image lets go of them.
            Dispatcher.UIThread.Post(() =>
            {
                _image.Source = null;
                lock (_sync)
                {
                    for (int i = 0; i < PoolSize; i++)
                    {
                        _pool[i]?.Dispose();
                        _pool[i] = null;
                    }
                }
            });
        }
    }
}
