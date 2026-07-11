using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

        public UploadsManager Uploads { get; } = new UploadsManager();

        private readonly Dictionary<Type, object> _singletons = new();

        static PageManager()
        {
            Current = new PageManager();
        }

        private PageManager()
        { }

        public ISettingsPage GetSettingsPage()
        {
            return GetOrCreate<MainWindow>();
        }

        public IScreenCapturePage GetScreenCapturePage()
        {
            // backed by the external Rust capture process (CAPTURE_PROTOCOL.md); the page
            // itself guards against concurrent captures, so a fresh instance per call is fine.
            return new ScreenCapturePage();
        }

        private T GetOrCreate<T>(Action closing = null) where T : IPage
        {
            // _singletons is not synchronized and window creation must happen on the UI
            // thread anyway — fail fast instead of racing to a duplicate window.
            Dispatcher.UIThread.VerifyAccess();

            if (_singletons.TryGetValue(typeof(T), out var cached))
                return (T)cached;

            // self-heal: if a live window of this type exists but is no longer tracked
            // (e.g. an exception in another Closed subscriber dropped our bookkeeping),
            // adopt it rather than opening a second copy.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var open = desktop.Windows.OfType<T>().FirstOrDefault();
                if (open != null)
                {
                    HandleClosing(open, closing);
                    _singletons[typeof(T)] = open;
                    return open;
                }
            }

            var inst = Activator.CreateInstance<T>();

            // the constructor may have pumped the dispatcher and let a re-entrant call
            // (e.g. a second queued tray click) register its own instance first — keep
            // that one and discard ours before it is ever shown.
            if (_singletons.TryGetValue(typeof(T), out var raced))
            {
                (inst as Window)?.Close();
                return (T)raced;
            }

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
                // only evict our own registration — a stale handler must never drop a
                // newer instance's entry (that would allow a duplicate to be created).
                if (_singletons.TryGetValue(typeof(T), out var current) && ReferenceEquals(current, instance))
                    _singletons.Remove(typeof(T));
                if (closing != null)
                    closing();
            });
            instance.Closed += handler;
        }
    }
}
