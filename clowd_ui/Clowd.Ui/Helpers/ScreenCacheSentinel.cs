using System;
using Avalonia.Controls;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// Keeps Avalonia's Win32 screens cache from going stale while Clowd is tray-resident.
    ///
    /// Avalonia invalidates that cache only from a live window's WM_DISPLAYCHANGE handler (the
    /// hidden message window ignores WM_DISPLAYCHANGE), so a display change while no window is
    /// open — sleep/wake, monitor hotplug, or the logon-time 1024x768 fallback monitor — leaves
    /// the cache stale for the rest of the process. Avalonia then mis-places everything that
    /// consults it internally, most visibly the tray context menu, whose flip-above-the-cursor
    /// constraint never fits the stale bounds and leaves the menu running off screen.
    /// Upstream: https://github.com/AvaloniaUI/Avalonia/issues/22217.
    ///
    /// The fix is one never-shown Window held for the life of the process: Avalonia creates its
    /// HWND in the constructor, WM_DISPLAYCHANGE is broadcast to all top-level windows visible or
    /// not, and the handler refreshes the cache through Avalonia's own per-window path. Unshown,
    /// it starts no renderer and never appears in the desktop lifetime's window list.
    ///
    /// This complements <see cref="DesktopScreens"/>: that keeps Clowd's own placement math off
    /// the cache entirely; this keeps the cache itself fresh for the consumers inside Avalonia
    /// that Clowd cannot redirect.
    /// </summary>
    internal static class ScreenCacheSentinel
    {
        private static Window _sentinel;

        public static void Initialize()
        {
            if (!OperatingSystem.IsWindows() || _sentinel != null)
                return;

            _sentinel = new Window
            {
                Title = "Clowd screen-change sentinel",
                ShowInTaskbar = false,
                ShowActivated = false,
            };
        }
    }
}
