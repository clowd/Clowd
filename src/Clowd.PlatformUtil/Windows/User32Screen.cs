using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.SHCore;

namespace Clowd.PlatformUtil.Windows
{
    /// <summary>
    /// Represents a display device or multiple display devices on a single system.
    /// </summary>
    public record User32Screen : IScreen
    {
        // References:
        // http://referencesource.microsoft.com/#System.Windows.Forms/ndp/fx/src/winforms/Managed/System/WinForms/Screen.cs
        // http://msdn.microsoft.com/en-us/library/windows/desktop/dd145072.aspx
        // http://msdn.microsoft.com/en-us/library/windows/desktop/dd183314.aspx
        // https://github.com/micdenny/WpfScreenHelper
        // https://raw.githubusercontent.com/micdenny/WpfScreenHelper/master/src/WpfScreenHelper/Screen.cs

        private readonly HMONITOR _hMonitor;

        private const int MONITORINFOF_PRIMARY = 0x00000001;

        private User32Screen()
        {
            this.IsVirtual = true;
        }

        private static MONITORINFO GetInfo(HMONITOR h)
        {
            MONITORINFO mfo = default;
            mfo.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
            GetMonitorInfo(h, ref mfo);
            var hr = Marshal.GetLastWin32Error();
            Marshal.ThrowExceptionForHR(hr);
            return mfo;
        }

        private static RECT GetVirtualBounds()
        {
            var x = GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
            var cx = GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN);
            var cy = GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN);
            return new RECT
            {
                left = x,
                top = y,
                right = x + cx,
                bottom = y + cy,
            };
        }

        private unsafe static RECT GetVirtualWorkArea()
        {
            RECT rect;
            SystemParametersInfo(SPI.SPI_GETWORKAREA, 0, (IntPtr)(&rect), 0);
            return rect;
        }

        private User32Screen(HMONITOR hMonitor)
        {
            if (hMonitor == IntPtr.Zero)
                throw new ArgumentNullException(nameof(hMonitor));

            MONITORINFO mfo = GetInfo(hMonitor);
            this.IsPrimary = ((mfo.dwFlags & MonitorInfoFlags.MONITORINFOF_PRIMARY) != 0);
            _hMonitor = hMonitor;
        }

        /// <summary>
        /// Gets the primary display.
        /// </summary>
        public static User32Screen PrimaryScreen => AllScreens.FirstOrDefault(t => t.IsPrimary);

        /// <summary>
        /// Gets the virtual display bounds. On a single monitor system, this will have the same bounds as PrimaryScreen.
        /// On a multi-monitor system, this will be a rectangle that contains the bounds of all displays.
        /// </summary>
        public static User32Screen VirtualScreen => new User32Screen();

        /// <summary>
        /// Gets an enumeration of all displays on the system.
        /// </summary>
        public unsafe static IEnumerable<User32Screen> AllScreens
        {
            get
            {
                List<HMONITOR> displays = new List<HMONITOR>();
                MonitorEnumProc callback = (IntPtr hMon, IntPtr hdc, PRECT rect, IntPtr lp) =>
                {
                    displays.Add(hMon);
                    return true;
                };

                if (!EnumDisplayMonitors(IntPtr.Zero, null, callback, IntPtr.Zero))
                    throw new Win32Exception();

                return displays.Select(d => new User32Screen(d));
            }
        }

        /// <summary>
        /// Retrieves a <see cref="User32Screen"/> for the display that contains the specified point.
        /// </summary>
        public static User32Screen FromPoint(ScreenPoint point) =>
            new User32Screen(MonitorFromPoint((System.Drawing.Point)point, MonitorFlags.MONITOR_DEFAULTTONEAREST));

        /// <summary>
        /// Retrieves a <see cref="User32Screen"/> for the display that contains the center point of the specified rect.
        /// </summary>
        public static User32Screen FromRect(ScreenRect rect) => new User32Screen(MonitorFromRect(rect, MonitorFlags.MONITOR_DEFAULTTONEAREST));

        /// <summary>
        /// Retrieves a <see cref="User32Screen"/> for the display that contains the center point of the specified window.
        /// </summary>
        public static User32Screen FromWindow(User32Window hWnd) => FromWindow(hWnd.Handle);

        /// <summary>
        /// Retrieves a <see cref="User32Screen"/> for the display that contains the center point of the specified window handle.
        /// </summary>
        public static User32Screen FromWindow(IntPtr hWnd) => FromWindow(new HWND(hWnd));

        internal static User32Screen FromWindow(HWND hWnd) => new User32Screen(MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST));

        /// <summary>
        /// Gets the native monitor handle to use when executing interop with user32.dll functions
        /// </summary>
        public IntPtr Handle => (IntPtr)_hMonitor;

        /// <summary>
        /// Gets a value indicating whether a particular display is the primary device.
        /// </summary>
        /// <returns>true if this display is primary; otherwise, false.</returns>
        public bool IsPrimary { get; init; }

        /// <summary>
        /// Gets a value indicating whether this class represents the virtual screen instead of a physical device.
        /// </summary>
        /// <returns>true if this display is virtual; otherwise, false.</returns>
        public bool IsVirtual { get; init; }

        /// <summary>
        /// Gets the bounds of the display.
        /// </summary>
        public ScreenRect Bounds => IsVirtual ? GetVirtualBounds() : GetInfo(_hMonitor).rcMonitor;

        /// <summary>
        /// Gets the working area of the display. The working area is the desktop area of the display, excluding taskbars, docked windows, and docked tool bars.
        /// </summary>
        public ScreenRect WorkingArea => IsVirtual ? GetVirtualWorkArea() : GetInfo(_hMonitor).rcWork;

        public int Index => IsVirtual ? -1 : AllScreens.ToList().IndexOf(this);

        public double PixelDensity
        {
            get
            {
                if (IsVirtual)
                    throw new InvalidOperationException("The virtual desktop (which encompasses all displays) does not have a single DPI, " +
                                                        "it is made up of the DPI of each individual display.");

                if (0 == GetDpiForMonitor(Handle, MONITOR_DPI_TYPE.MDT_DEFAULT, out var dpiX, out var dpiY))
                    return dpiX / 96.0;

                throw new Win32Exception("Unspecified error occurred.");
            }
        }

        /// <summary>
        /// Retrieves the screen bounds and working area as a human-readable string
        /// </summary>
        public override string ToString()
        {
            if (IsVirtual)
                return $"{Bounds.Width}x{Bounds.Height}, virtual screen";

            var workingArea = WorkingArea == Bounds
                ? ""
                : $", working area {WorkingArea.Width}x{WorkingArea.Height} at ({WorkingArea.Left}, {WorkingArea.Top})";
            return $"{Bounds.Width}x{Bounds.Height} at ({Bounds.Left}, {Bounds.Top}){(IsPrimary ? ", primary" : "")}{workingArea}";
        }

        public bool Equals(IScreen other)
        {
            return Equals(other as User32Screen);
        }
    }
}
