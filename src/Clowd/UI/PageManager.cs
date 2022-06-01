using System;
using System.Collections.Generic;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Unmanaged;

namespace Clowd.UI
{
    internal class PageManager
    {
        public static PageManager Current { get; private set; }

        private readonly Dictionary<Type, object> _singletons = new Dictionary<Type, object>();

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
            GetOrCreate<VideoCaptureWindow>().Open(region);
        }
        
        public IVideoCapturePage GetExistingVideoCapturePage()
        {
            return GetOrCreate<VideoCaptureWindow>(false);
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
            return new StaticCaptureWrapper();
        }

        private T GetOrCreate<T>(bool canCreate = true) where T : IPage
        {
            if (_singletons.ContainsKey(typeof(T)))
                return (T)_singletons[typeof(T)];

            if (!canCreate) return default;
            
            var inst = Activator.CreateInstance<T>();
            HandleClosing(inst);

            _singletons[typeof(T)] = inst;
            return inst;
        }

        private void HandleClosing<T>(T instance) where T : IPage
        {
            EventHandler handler = null;
            handler = new EventHandler((s, ev) =>
            {
                instance.Closed -= handler;
                if (_singletons.ContainsKey(typeof(T)))
                    _singletons.Remove(typeof(T));
            });
            instance.Closed += handler;
        }

        private class StaticCaptureWrapper : IScreenCapturePage
        {
            public event EventHandler Closed; // TODO

            public void Close()
            {
                CaptureWindow.Close();
            }

            public void Dispose()
            {
                Close();
            }

            public void Open(ScreenRect captureArea)
            {
                CaptureWindow.Show(new CaptureWindowOptions
                {
                    AccentColor = AppStyles.AccentColor,
                    TipsDisabled = SettingsRoot.Current.Capture.HideTipsPanel,
                    InitialRect = captureArea,
                });
            }
        }
    }
}
