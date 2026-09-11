using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Helpers
{
    /// <summary>A monitor, in the same shapes Avalonia's <see cref="Screens"/> exposes: physical
    /// pixel rectangles on Windows, CG points (logical units) on macOS.</summary>
    internal sealed record DesktopScreen
    {
        public PixelRect Bounds { get; init; }
        public PixelRect WorkingArea { get; init; }
        public double Scaling { get; init; } = 1.0;
        public bool IsPrimary { get; init; }
    }

    /// <summary>
    /// Monitor lookup for window-placement math. Use this instead of <see cref="Window.Screens"/>.
    ///
    /// Avalonia caches its screen list process-wide and, on Windows, invalidates it only from a
    /// window procedure: each live window's WM_DISPLAYCHANGE, or the hidden message window's
    /// WM_SETTINGCHANGE(SPI_SETWORKAREA) — the message window ignores WM_DISPLAYCHANGE itself.
    /// Clowd runs tray-only for long stretches with no Avalonia window alive, so a display change
    /// in that state is never observed and the cache goes stale for the rest of the process. In
    /// the worst case the cache holds the 1024x768 fallback monitor Windows reports while the
    /// real display is still handshaking at logon, and every subsequent placement clamps into the
    /// top-left corner of the desktop (the share-region toolbar bug of 2026-09-11).
    ///
    /// On Windows every call here therefore asks user32 directly — two uncached syscalls, always
    /// current. On macOS the Avalonia cache is trusted: libAvaloniaNative refreshes it from
    /// applicationDidChangeScreenParameters:, an app-level notification that needs no window, so
    /// the Windows failure mode does not exist there and a native NSScreen detour would be all
    /// risk and no fix.
    /// </summary>
    internal static class DesktopScreens
    {
        /// <summary>The monitor containing the point, or null if the point is on none — the same
        /// contract as Screens.ScreenFromPoint. The point is in physical px on Windows / CG points
        /// on macOS, matching capture-space regions.</summary>
        public static DesktopScreen FromPoint(WindowBase window, PixelPoint point)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.ScreenFromPoint(point));

            var hMonitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONULL);
            return FromHMonitor(hMonitor);
        }

        /// <summary>The monitor with the largest intersection with the rect, or null if it touches
        /// none — the same contract as Screens.ScreenFromBounds.</summary>
        public static DesktopScreen FromRect(WindowBase window, PixelRect rect)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.ScreenFromBounds(rect));

            var native = new RECT { left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom };
            var hMonitor = MonitorFromRect(ref native, MONITOR_DEFAULTTONULL);
            return FromHMonitor(hMonitor);
        }

        /// <summary>The primary monitor, or null if none can be enumerated.</summary>
        public static DesktopScreen Primary(WindowBase window)
        {
            if (!OperatingSystem.IsWindows())
                return FromAvalonia(window?.Screens?.Primary);

            // the primary monitor always contains the desktop origin
            return FromHMonitor(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));
        }

        /// <summary>Every monitor, freshly enumerated on Windows. Empty rather than null when
        /// enumeration fails.</summary>
        public static IReadOnlyList<DesktopScreen> All(WindowBase window)
        {
            if (!OperatingSystem.IsWindows())
            {
                var screens = window?.Screens?.All;
                if (screens == null)
                    return Array.Empty<DesktopScreen>();
                var mapped = new List<DesktopScreen>(screens.Count);
                foreach (var s in screens)
                {
                    var m = FromAvalonia(s);
                    if (m != null)
                        mapped.Add(m);
                }
                return mapped;
            }

            var list = new List<DesktopScreen>();
            var handle = GCHandle.Alloc(list);
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    static (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr lParam) =>
                    {
                        if (GCHandle.FromIntPtr(lParam).Target is List<DesktopScreen> target
                            && FromHMonitor(hMonitor) is { } screen)
                            target.Add(screen);
                        return true;
                    }, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
            return list;
        }

        private static DesktopScreen FromAvalonia(Avalonia.Platform.Screen screen)
        {
            if (screen == null)
                return null;
            return new DesktopScreen
            {
                Bounds = screen.Bounds,
                WorkingArea = screen.WorkingArea,
                Scaling = screen.Scaling,
                IsPrimary = screen.IsPrimary,
            };
        }

        private static DesktopScreen FromHMonitor(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero)
                return null;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref info))
                return null;

            // effective DPI mirrors Avalonia's WinScreen scaling; a failed query means 100%
            var scaling = 1.0;
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                scaling = dpiX / 96.0;

            return new DesktopScreen
            {
                Bounds = ToPixelRect(info.rcMonitor),
                WorkingArea = ToPixelRect(info.rcWork),
                Scaling = scaling,
                IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
            };
        }

        private static PixelRect ToPixelRect(RECT r) =>
            new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top);

        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint MONITORINFOF_PRIMARY = 1;
        private const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
