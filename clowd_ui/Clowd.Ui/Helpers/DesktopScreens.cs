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

        /// <summary>The panel's current refresh rate in Hz, or 0 when the platform would not say
        /// (a failed query, or a display mode that reports none — some built-in and variable-rate
        /// panels do). Callers treat 0 as "unknown", never as a rate.</summary>
        public double RefreshRate { get; init; }
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
                RefreshRate = OperatingSystem.IsMacOS() ? MacRefreshRate(screen.Bounds) : 0,
            };
        }

        /// <summary>
        /// The refresh rate of the CoreGraphics display under the centre of <paramref name="bounds"/>
        /// (CG points, the same space Avalonia's macOS screen bounds are in). Avalonia's Screen has
        /// no refresh rate of its own, so the display is looked up again by position. 0 when the
        /// lookup fails or the mode reports none — CGDisplayModeGetRefreshRate is documented to return
        /// 0 for displays that do not expose one, which includes some built-in panels.
        /// </summary>
        private static double MacRefreshRate(PixelRect bounds)
        {
            try
            {
                var point = new CGPoint { X = bounds.X + bounds.Width / 2.0, Y = bounds.Y + bounds.Height / 2.0 };
                var displays = new uint[1];
                if (CGGetDisplaysWithPoint(point, 1, displays, out var count) != 0 || count == 0)
                    return 0;

                var mode = CGDisplayCopyDisplayMode(displays[0]);
                if (mode == IntPtr.Zero)
                    return 0;

                try
                {
                    var hz = CGDisplayModeGetRefreshRate(mode);
                    return hz > 0 && !Double.IsInfinity(hz) ? hz : 0;
                }
                finally
                {
                    CGDisplayModeRelease(mode);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return 0;
            }
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
                RefreshRate = WindowsRefreshRate(hMonitor),
            };
        }

        /// <summary>
        /// The monitor's current refresh rate from its active display mode: the device name from
        /// GetMonitorInfo's EX variant, then EnumDisplaySettings for that device's current settings.
        /// 0 when either call fails, and also for the documented 0 / 1 sentinel values, which mean
        /// "the hardware default" rather than a rate. Note the field is an integer: a 59.94 Hz panel
        /// reports 59 or 60 depending on the driver, which is why callers fold near-equal rates.
        /// </summary>
        private static double WindowsRefreshRate(IntPtr hMonitor)
        {
            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(hMonitor, ref info) || String.IsNullOrEmpty(info.szDevice))
                return 0;

            var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode))
                return 0;

            return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0;
        }

        private static PixelRect ToPixelRect(RECT r) =>
            new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top);

        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint MONITORINFOF_PRIMARY = 1;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const int ENUM_CURRENT_SETTINGS = -1;

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

        /// <summary>MONITORINFO plus the device name EnumDisplaySettings wants ("\\.\DISPLAY1").</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEXW
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        /// <summary>DEVMODEW laid out in full (220 bytes) so dmSize is right; only dmDisplayFrequency
        /// is read.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint
        {
            public double X;
            public double Y;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphics)]
        private static extern int CGGetDisplaysWithPoint(CGPoint point, uint maxDisplays, [Out] uint[] displays, out uint matchingDisplayCount);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

        [DllImport(CoreGraphics)]
        private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

        [DllImport(CoreGraphics)]
        private static extern void CGDisplayModeRelease(IntPtr mode);
    }
}
