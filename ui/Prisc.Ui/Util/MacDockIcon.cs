using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Reactive;
using Avalonia.Threading;
using Prisc.Interop;

namespace Prisc.Util
{
    /// <summary>
    /// Prisc is a tray-resident app: on macOS the dock icon should only exist while a real
    /// window is open (the 1Password model). Avalonia only reads ShowInDock once at startup,
    /// so the NSApplication activation policy is flipped at runtime instead: Regular while
    /// any visible ShowInTaskbar window exists, Accessory (tray/menu-bar only) otherwise.
    /// The recording chrome (BorderWindow, FloatingTrayWindow) and the dialogs opt out
    /// via ShowInTaskbar="False"; a dock icon appearing mid-recording would also resize the
    /// dock and shift the content being recorded.
    /// </summary>
    internal static class MacDockIcon
    {
        private static IClassicDesktopStyleApplicationLifetime _lifetime;
        private static ActivationPolicy _currentPolicy = ActivationPolicy.Accessory; // matches ShowInDock=false at startup

        public static void Initialize(IClassicDesktopStyleApplicationLifetime lifetime)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            _lifetime = lifetime;

            // fires for every Window instance in the process: the one global hook Avalonia
            // offers for "a window was shown or hidden" without touching each window class.
            Window.IsVisibleProperty.Changed.Subscribe(
                new AnonymousObserver<AvaloniaPropertyChangedEventArgs<bool>>(e => Update(e.Sender as Window)));

            Update(null);
        }

        private static void Update(Window changed)
        {
            if (_lifetime == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                // the sender may not be in lifetime.Windows yet when its IsVisible flips true
                var windows = _lifetime.Windows.Concat(changed is null ? [] : [changed]);
                bool anyVisible = windows.Any(w => w.IsVisible && w.ShowInTaskbar);

                var policy = anyVisible ? ActivationPolicy.Regular : ActivationPolicy.Accessory;
                if (policy == _currentPolicy)
                    return;

                // Accessory to Regular alone leaves the app in the background and the new window
                // can open behind the previous frontmost app, so the same call takes focus.
                try
                {
                    MacApp.SetActivationPolicy(policy, activateIfRegular: true);
                    // cached only once AppKit took it, so a refused or failed flip is retried on
                    // the next visibility change instead of being skipped as already applied.
                    _currentPolicy = policy;
                }
                catch (Exception ex)
                {
                    SentryConfig.CaptureHandled(ex, "dock.activation-policy");
                }
            });
        }
    }
}
