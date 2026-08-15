using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;

namespace Clowd.UI.Helpers
{
    /// <summary>Lightweight toast notifications backed by Avalonia's WindowNotificationManager,
    /// with one manager cached per host window (the Semi theme styles the NotificationCard). All
    /// members must be called from the UI thread — callers ensure this.</summary>
    public static class Toast
    {
        private static readonly ConditionalWeakTable<Window, WindowNotificationManager> _managers = new();

        public static void Show(Window window, string message, NotificationType type = NotificationType.Success)
        {
            if (window == null || String.IsNullOrEmpty(message))
                return;

            var manager = _managers.GetValue(window, static w => new WindowNotificationManager(w)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            });

            manager.Show(new Notification(null, message, type, TimeSpan.FromSeconds(2.5)));
        }

        /// <summary>A reasonable window to host a toast or borrow a clipboard from when the caller
        /// has no window of its own: the active visible window, else any visible one.</summary>
        public static Window GetActiveOrMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow
                       ?? desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
                       ?? desktop.Windows.FirstOrDefault(w => w.IsVisible);
            }

            return null;
        }

        /// <summary>Best-effort clipboard access for code with no window of its own: borrows the
        /// clipboard of any open window.</summary>
        public static IClipboard GetPrimaryClipboard() => GetActiveOrMainWindow()?.Clipboard;
    }
}
