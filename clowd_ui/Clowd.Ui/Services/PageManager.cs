using System;
using System.Collections.Generic;
using System.Diagnostics;
using Clowd.PlatformUtil;

namespace Clowd
{
    public enum SettingsPageTab
    {
        RecentSessions,
        SettingsGeneral,
        SettingsHotkeys,
        SettingsCapture,
        SettingsEditor,
        SettingsUploads,
        SettingsVideo,
        About,
    }

    public interface IPage
    {
        event EventHandler Closed;
        void Close();
    }

    public interface ISettingsPage : IPage
    {
        void Open(SettingsPageTab? selectedTab = null);
    }

    public interface ILiveDrawPage : IPage
    {
        void Open();
    }

    public interface IScreenCapturePage : IPage
    {
        void Open(ScreenRect captureArea);
    }
}

namespace Clowd.UI
{
    internal sealed class PageManager : SimpleNotifyObject
    {
        public static PageManager Current { get; }

        public ITasksView Tasks { get; } = new TasksViewManager();

        private readonly Dictionary<Type, object> _singletons = new();

        static PageManager()
        {
            Current = new PageManager();
        }

        private PageManager()
        { }

        public ILiveDrawPage GetLiveDrawPage()
        {
            return new StubLiveDrawPage();
        }

        public ISettingsPage GetSettingsPage()
        {
            return GetOrCreate<MainWindow>();
        }

        public IScreenCapturePage GetScreenCapturePage()
        {
            return new StubScreenCapturePage();
        }

        private T GetOrCreate<T>(Action closing = null) where T : IPage
        {
            if (_singletons.ContainsKey(typeof(T)))
                return (T)_singletons[typeof(T)];

            var inst = Activator.CreateInstance<T>();
            HandleClosing(inst, closing);

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

        // screen capture is provided by the separate Rust capture process in this build —
        // this stub just logs and immediately reports itself closed.
        private sealed class StubScreenCapturePage : IScreenCapturePage
        {
            public event EventHandler Closed;

            public void Open(ScreenRect captureArea)
            {
                Debug.WriteLine($"IScreenCapturePage.Open({captureArea}) — screen capture is not available in this build.");
                Closed?.Invoke(this, EventArgs.Empty);
            }

            public void Close()
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        // LiveDraw was dropped in the Avalonia migration.
        private sealed class StubLiveDrawPage : ILiveDrawPage
        {
            public event EventHandler Closed;

            public void Open()
            {
                Debug.WriteLine("ILiveDrawPage.Open() — LiveDraw is not available in this build.");
                Closed?.Invoke(this, EventArgs.Empty);
            }

            public void Close()
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
