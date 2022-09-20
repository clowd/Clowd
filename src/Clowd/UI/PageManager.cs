using System;
using System.Collections.Generic;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Dialogs.LiveDraw;
using Clowd.UI.Unmanaged;

namespace Clowd.UI
{
    internal sealed class PageManager : SimpleNotifyObject
    {
        public static PageManager Current { get; }

        public ITasksView Tasks { get; } = new TasksViewManager();

        public bool IsVideoCapturePageOpen
        {
            get => _isVideoCapturePageOpen;
            private set => Set(ref _isVideoCapturePageOpen, value);
        }

        private readonly Dictionary<Type, object> _singletons = new();
        private bool _isVideoCapturePageOpen;

        static PageManager()
        {
            Current = new PageManager();
        }

        private PageManager()
        { }

        public void CreateNewVideoCapturePage(ScreenRect region = null)
        {
            // if there is already an open video window, just ignore.
            if (_singletons.ContainsKey(typeof(VideoCaptureWindow))) return;
            GetOrCreate<VideoCaptureWindow>(closing: () => IsVideoCapturePageOpen = false).Open(region);
            IsVideoCapturePageOpen = true;
        }

        public IVideoCapturePage GetExistingVideoCapturePage()
        {
            if (_singletons.TryGetValue(typeof(VideoCaptureWindow), out var wnd))
                return wnd as VideoCaptureWindow;

            return null;
        }

        public ILiveDrawPage GetLiveDrawPage()
        {
            return GetOrCreate<LiveDrawWindow>();
        }

        public ISettingsPage GetSettingsPage()
        {
            return GetOrCreate<MainWindow>();
        }

        public IScreenCapturePage GetScreenCapturePage()
        {
            App.Analytics.ScreenView(nameof(CaptureWindow));
            return new StaticCaptureWrapper();
        }

        private T GetOrCreate<T>(Action closing = null) where T : IPage
        {
            if (_singletons.ContainsKey(typeof(T)))
                return (T)_singletons[typeof(T)];

            var inst = Activator.CreateInstance<T>();
            HandleClosing(inst, closing);

            App.Analytics.ScreenView(typeof(T).Name);

            _singletons[typeof(T)] = inst;
            return inst;
        }

        private void HandleClosing<T>(T instance, Action closing) where T : IPage
        {
            EventHandler handler = null;
            handler = new EventHandler((s, ev) =>
            {
                instance.Closed -= handler;
                if (_singletons.ContainsKey(typeof(T)))
                    _singletons.Remove(typeof(T));
                if (closing != null)
                    closing();
            });
            instance.Closed += handler;
        }

        private class StaticCaptureWrapper : IScreenCapturePage
        {
            public event EventHandler Closed;

            private bool _opened;
            private bool _closed;
            private DateTime _timingStart;

            public void Open(ScreenRect captureArea)
            {
                if (_opened) return;

                _opened = true;
                _timingStart = DateTime.UtcNow;
                CaptureWindow.Disposed += CaptureWindowOnDisposed;
                CaptureWindow.Loaded += CaptureWindowOnLoaded;

                var settings = SettingsRoot.Current.Capture;
                CaptureWindow.Show(new CaptureWindowOptions
                {
                    AccentColor = AppStyles.AccentColor,
                    TipsDisabled = settings.HideTipsPanel,
                    InitialRect = captureArea,
                    ObstructedWindowDisabled = !settings.DetectWindows,
                    CopyCursorToClipboard = settings.ScreenshotWithCursor,
                });
            }

            private void CaptureWindowOnLoaded(object sender, CaptureWindowLoadedEventArgs e)
            {
                var time = DateTime.UtcNow - _timingStart;
                App.Analytics.Timing("prntscr", e.PrimaryGpu, (int)time.TotalMilliseconds);
            }

            public void Close()
            {
                if (_opened && !_closed)
                {
                    CaptureWindow.Close();
                }
            }

            private void CaptureWindowOnDisposed(object sender, EventArgs e)
            {
                _closed = true;
                CaptureWindow.Disposed -= CaptureWindowOnDisposed;
                Closed?.Invoke(this, e);
            }
        }
    }
}
