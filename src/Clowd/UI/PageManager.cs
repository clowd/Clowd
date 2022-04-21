using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Unmanaged;
using Clowd.Util;
using Clowd.Video;

namespace Clowd.UI
{
    internal class PageManager
    {
        public static PageManager Current { get; private set; }

        private readonly Dictionary<Type, object> _singletons = new Dictionary<Type, object>();

        private readonly IScopedLog _log = new DefaultScopedLog(Constants.ClowdAppName);

        static PageManager()
        {
            Current = new PageManager();
        }

        private PageManager()
        { }

        public IVideoCapturePage CreateVideoCapturePage()
        {
            if (_singletons.ContainsKey(typeof(VideoCaptureWindow)))
                throw new InvalidOperationException("Not allowed retrieve open video pages, and only one can be open at a time.");

            var obsPath = Path.Combine(AppContext.BaseDirectory, "obs-express");
            var obs = new ObsCapturer(_log, obsPath);
            var inst = new VideoCaptureWindow(obs);
            HandleClosing(inst);
            
            _singletons[typeof(VideoCaptureWindow)] = inst;
            return inst;
        }

        public ILiveDrawPage GetLiveDrawPage()
        {
            return GetOrCreate<AntFu7.LiveDraw.LiveDrawWindow>();
        }

        public ISettingsPage GetSettingsPage()
        {
            return GetOrCreate<MainWindow>();
        }

        public IScreenCapturePage GetScreenCapturePage()
        {
            return new StaticCaptureWrapper();
        }

        private T GetOrCreate<T>() where T : IPage
        {
            if (_singletons.ContainsKey(typeof(T)))
                return (T)_singletons[typeof(T)];

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

            public void Open()
            {
                CaptureWindow.Show(new CaptureWindowOptions
                {
                    AccentColor = AppStyles.AccentColor,
                    TipsDisabled = SettingsRoot.Current.Capture.HideTipsPanel,
                });
            }

            public void Open(ScreenRect captureArea)
            {
                Open();
            }

            public void Open(IntPtr captureWindow)
            {
                Open();
            }
        }
    }
}
