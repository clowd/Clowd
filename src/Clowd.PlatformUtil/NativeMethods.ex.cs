using System;
using System.Drawing;
using System.Runtime.InteropServices;
using CsWin32.Graphics.Gdi;
using CsWin32.UI.WindowsAndMessaging;
using CsWin32.UI.HiDpi;
using Clowd.PlatformUtil;

namespace CsWin32
{
    internal static partial class PInvoke // User32
    {
        //[DllImport("User32", SetLastError = true)]
        //internal static extern bool GetIconInfo(SafeHandle hIcon, out ICONINFO piconinfo);

        //[DllImport("User32", SetLastError = true)]
        //internal static extern bool GetIconInfo(HICON hIcon, out ICONINFO piconinfo);
    }

    internal static partial class PInvoke // Shcore
    {
        [DllImport("Shcore")]
        public static extern int GetDpiForMonitor(nint hMonitor, MONITOR_DPI_TYPE dpiType, ref uint dpiX, ref uint dpiY);

        [DllImport("Shcore")]
        public static extern int SetProcessDpiAwareness(DPI_AWARENESS value);

        [DllImport("Shcore")]
        public static extern int GetProcessDpiAwareness(nint hProcess, ref DPI_AWARENESS value);
    }

    public enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    namespace Graphics.Gdi
    {
        internal readonly partial struct HGDIOBJ
        {
            public static implicit operator HGDIOBJ(HPEN value) => new HGDIOBJ(value);
            public static implicit operator HPEN(HGDIOBJ value) => new HPEN(value);

            public static implicit operator HGDIOBJ(HBRUSH value) => new HGDIOBJ(value);
            public static implicit operator HBRUSH(HGDIOBJ value) => new HBRUSH(value);

            public static implicit operator HGDIOBJ(HBITMAP value) => new HGDIOBJ(value);
            public static implicit operator HBITMAP(HGDIOBJ value) => new HBITMAP(value);

            public static implicit operator HGDIOBJ(SafeHandle value) => new HGDIOBJ(value.DangerousGetHandle());
        }

        internal partial struct CreatedHDC
        {
            public static implicit operator HDC(CreatedHDC value) => new HDC(value);
        }
    }

    namespace UI.WindowsAndMessaging
    {
        internal readonly partial struct HCURSOR
        {
            public static implicit operator HCURSOR(HICON value) => new HCURSOR(value);
            public static implicit operator HICON(HCURSOR value) => new HICON(value);
        }
    }

    namespace Foundation
    {
        internal partial struct POINT
        {
            public static implicit operator Point(POINT value) => new Point(value.x, value.y);
            public static implicit operator POINT(Point value) => new POINT { x = value.X, y = value.Y };

            public static implicit operator ScreenPoint(POINT value) => new ScreenPoint(value.x, value.y);
            public static implicit operator POINT(ScreenPoint value) => new POINT { x = value.X, y = value.Y };
        }

        internal partial struct RECT
        {
            public static implicit operator Rectangle(RECT value) => Rectangle.FromLTRB(value.left, value.top, value.right, value.bottom);
            public static implicit operator RECT(Rectangle value) => new RECT { left = value.Left, top = value.Top, right = value.Right, bottom = value.Bottom };

            public static implicit operator ScreenRect(RECT value) => ScreenRect.FromLTRB(value.left, value.top, value.right, value.bottom);
            public static implicit operator RECT(ScreenRect value) => new RECT { left = value.Left, top = value.Top, right = value.Right, bottom = value.Bottom };
        }
    }
}
