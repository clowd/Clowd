using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Reactive;
using Avalonia.Threading;

namespace Clowd.Util
{
    /// <summary>
    /// Clowd is a tray-resident app: on macOS the dock icon should only exist while a real
    /// window is open (the 1Password model). Avalonia only reads ShowInDock once at startup,
    /// so the NSApplication activation policy is flipped at runtime instead — Regular while
    /// any visible ShowInTaskbar window exists, Accessory (tray/menu-bar only) otherwise.
    /// The recording chrome (BorderWindow, FloatingToolbarWindow) and the dialogs opt out
    /// via ShowInTaskbar="False"; a dock icon appearing mid-recording would also resize the
    /// dock and shift the content being recorded.
    /// </summary>
    internal static class MacDockIcon
    {
        private const long PolicyRegular = 0;
        private const long PolicyAccessory = 1;

        private static IClassicDesktopStyleApplicationLifetime _lifetime;
        private static long _currentPolicy = PolicyAccessory; // matches ShowInDock=false at startup

        public static void Initialize(IClassicDesktopStyleApplicationLifetime lifetime)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            _lifetime = lifetime;

            // fires for every Window instance in the process — the one global hook Avalonia
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

                var policy = anyVisible ? PolicyRegular : PolicyAccessory;
                if (policy == _currentPolicy)
                    return;
                _currentPolicy = policy;

                var nsApp = SendMessage(GetClass("NSApplication"), GetSelector("sharedApplication"));
                SendMessage(nsApp, GetSelector("setActivationPolicy:"), policy);

                // Accessory→Regular alone leaves the app in the background and the new window
                // can open behind the previous frontmost app; explicitly take focus.
                if (policy == PolicyRegular)
                    SendMessage(nsApp, GetSelector("activateIgnoringOtherApps:"), true);
            });
        }

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass(string name);

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector(string name);

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector, long arg);

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] bool arg);
    }
}
