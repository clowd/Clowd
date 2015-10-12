using Clowd.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace Clowd.Capture
{
    public static class ScreenUtil
    {
        public static Rectangle ScreenBounds => new Rectangle(0, 0, SystemInformation.VirtualScreen.Width, SystemInformation.VirtualScreen.Height);

        public static IEnumerable<CapturedWindow> GetVisibleWindows()
        {
            var visibleWindows = new List<CapturedWindow>();
            var windows = GetCapturedWindows().OrderBy(win => win.ZOrder).ToArray();
            for (int i = 0; i < windows.Count(); i++)
            {
                if (!visibleWindows.Any(win => win.WindowRect.Contains(windows[i].WindowRect)))
                {
                    visibleWindows.Add(windows[i]);
                }

            }
            return visibleWindows;
        }

        public static IEnumerable<CapturedWindow> GetCapturedWindows()
        {
            IntPtr shellWindow = USER32.GetShellWindow();

            List<CapturedWindow> windows = new List<CapturedWindow>();
            USER32.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == shellWindow) return true;
                if (!USER32.IsWindowVisible(hWnd)) return true;

                int length = USER32.GetWindowTextLength(hWnd);
                if (length == 0) return true;

                WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf(placement);
                USER32.GetWindowPlacement(hWnd, ref placement);

                if (placement.showCmd == (uint)ShowWindowCmd.SW_HIDE || placement.showCmd == (uint)ShowWindowCmd.SW_MINIMIZE)
                    return true;

                StringBuilder builder = new StringBuilder(length);
                USER32.GetWindowText(hWnd, builder, length + 1);
                int zorder = USER32EX.GetWindowZOrder(hWnd);
                var rect = USER32EX.GetWindowRectangle(hWnd);
                if (rect.X < 0 && placement.showCmd == (uint)ShowWindowCmd.SW_MAXIMIZE)
                {
                    int diff = rect.X / -1;
                    rect = new Rectangle(rect.X + diff, rect.Y + diff, rect.Width - (diff * 2), rect.Height - (diff * 2));
                }
                windows.Add(new CapturedWindow(builder.ToString(), zorder, rect));
                return true;

            }, IntPtr.Zero);
            return windows;
        }
        public class CapturedWindow
        {
            public CapturedWindow(string title, int zorder, Rectangle bounds)
            {
                Title = title;
                ZOrder = zorder;
                WindowRect = bounds;
            }
            public string Title { get; private set; }
            public Rectangle WindowRect { get; private set; }
            public int ZOrder { get; private set; }

            public override string ToString()
            {
                return $"{Title} - [{ZOrder}] [x:{WindowRect.X} y:{WindowRect.Y} w:{WindowRect.Width} h: {WindowRect.Height}]";
            }
        }
        public static Bitmap Capture(Rectangle? bounds = null, bool captureCursor = false)
        {
            var rect = bounds == null ? ScreenBounds : bounds.GetValueOrDefault();

            if (captureCursor)
            {
                return CaptureCursor(rect.X, rect.Y, rect.Width, rect.Height);
            }
            else
            {
                return CaptureRegular(rect.X, rect.Y, rect.Width, rect.Height);
            }
        }
        private static Bitmap CaptureRegular(int x, int y, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(x, y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            }
            return bitmap;
        }
        private static Bitmap CaptureCursor(int x, int y, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(x, y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

                CURSORINFO cursorInfo;
                cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));

                if (USER32.GetCursorInfo(out cursorInfo))
                {
                    if (cursorInfo.flags == (uint)CURSORFLAGS.CURSOR_SHOWING)
                    {
                        var iconPointer = USER32.CopyIcon(cursorInfo.hCursor);
                        ICONINFO iconInfo;
                        int iconX, iconY;

                        if (USER32.GetIconInfo(iconPointer, out iconInfo))
                        {
                            iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot);
                            iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot);

                            USER32.DrawIcon(g.GetHdc(), iconX, iconY, cursorInfo.hCursor);

                            g.ReleaseHdc();
                        }
                    }
                }
            }
            return bitmap;
        }
    }
}
