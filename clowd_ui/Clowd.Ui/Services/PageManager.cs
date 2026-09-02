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
        SettingsRecording,
        SettingsShareRegion,
        SettingsEditor,
        SettingsUploads,
        About,
    }

    /// <summary>
    /// Which capture the user asked for, chosen by the hotkey / menu that fired.
    /// Maps to the Rust capturer's <c>--capture-mode</c> flag: <see cref="Region"/>
    /// opens free selection, while <see cref="Screen"/> and <see cref="Window"/>
    /// pre-select the active monitor / foreground window for confirmation.
    /// </summary>
    public enum CaptureMode
    {
        Region,
        Screen,
        Window,
    }

    /// <summary>
    /// What the region the user is about to pick is FOR. The overlay looks and behaves identically
    /// in all three cases — the user drags a rectangle — but a confirmed selection dispatches a
    /// different action, so the intent has to travel with the launch: it picks the capturer's flag
    /// (<c>--video</c> / <c>--share</c>, CAPTURE_PROTOCOL.md §1.1) and, just as importantly, decides
    /// whether the warm standby capturer may service the request at all. Standby was spawned with
    /// neither flag, so it can only ever take a plain screenshot.
    /// <para>Deliberately an enum rather than the pair of bools it replaced: the two are mutually
    /// exclusive (clap rejects both flags together), and a <c>bool video, bool share</c> signature
    /// makes the illegal combination expressible at every call site.</para>
    /// </summary>
    public enum RegionIntent
    {
        /// <summary>An ordinary screenshot — the overlay's own buttons decide what happens next.</summary>
        Capture,

        /// <summary>Pick a region to record (DESIGN §3.1); a confirmed selection dispatches the
        /// video action immediately.</summary>
        Video,

        /// <summary>Pick a region to mirror into a window a meeting app can share; a confirmed
        /// selection dispatches the share action immediately.</summary>
        Share,
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
        void Open(CaptureMode mode, RegionIntent intent = RegionIntent.Capture);
    }

    public interface IVideoCapturePage : IPage
    {
        void Open(ScreenRect region, double cornerRadius, string sessionDir);
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

        public IVideoCapturePage GetVideoCapturePage()
        {
            // self-guarding via VideoCapturePage.ActiveInstance, same rationale as screenshots.
            return new VideoCapturePage();
        }

        public IShareRegionPage GetShareRegionPage()
        {
            // self-guarding via ShareRegionPage.ActiveInstance, which is also how the app-exit path
            // and the Share Region tray/hotkey toggle reach a live share — same rationale as
            // recordings and scrolling captures.
            return new ShareRegionPage();
        }

        public IScrollCapturePage GetScrollCapturePage()
        {
            // self-guarding via ScrollCapturePage.ActiveInstance, which is also how the app-exit
            // path reaches an in-flight run — same rationale as recordings.
            return new ScrollCapturePage();
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
