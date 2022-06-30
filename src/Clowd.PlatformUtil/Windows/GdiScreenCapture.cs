using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.Gdi32;

namespace Clowd.PlatformUtil.Windows
{
    public static class GdiScreenCapture
    {
        private static void DrawScreenCursorToDC(HDC hdc, ScreenRect captureArea)
        {
            CURSORINFO cursorInfo = default;
            cursorInfo.cbSize = (uint)Marshal.SizeOf(typeof(CURSORINFO));
            if (GetCursorInfo(ref cursorInfo) && cursorInfo.flags == CursorState.CURSOR_SHOWING)
            {
                using (var hIcon = CopyIcon((IntPtr)cursorInfo.hCursor))
                {
                    using var icoInfo = new ICONINFO();
                    if (GetIconInfo(hIcon, icoInfo))
                    {
                        var iconX = cursorInfo.ptScreenPos.X - (int)icoInfo.xHotspot - captureArea.Left;
                        var iconY = cursorInfo.ptScreenPos.Y - (int)icoInfo.yHotspot - captureArea.Top;
                        DrawIconEx(hdc, iconX, iconY, hIcon.DangerousGetHandle(), 0, 0, 0, default, DrawIconExFlags.DI_NORMAL | DrawIconExFlags.DI_DEFAULTSIZE);
                    }
                }
            }
        }

        public static GdiCompatibleBitmap CaptureScreen(ScreenRect captureArea, bool drawCursor)
        {
            int x = captureArea.X, y = captureArea.Y, width = captureArea.Width, height = captureArea.Height;
            GdiCompatibleBitmap bitmap = null;

            HDC screenDC = GetDC(HWND.NULL);

            try
            {
                bitmap = new GdiCompatibleBitmap(width, height);

                if (!BitBlt(bitmap.hdcBitmap, 0, 0, width, height, screenDC, x, y, RasterOperationMode.SRCCOPY | RasterOperationMode.CAPTUREBLT))
                    throw new Win32Exception();

                if (drawCursor)
                    DrawScreenCursorToDC(bitmap.hdcBitmap, captureArea);

                return bitmap;
            }
            catch
            {
                if (bitmap != null) bitmap.Dispose();
                throw;
            }
            finally
            {
                ReleaseDC(HWND.NULL, screenDC);
            }
        }

        public static GdiCompatibleBitmap CaptureWindow(IWindow nWin)
        {
            var r1 = nWin.WindowBounds;
            var r2 = nWin.DwmRenderBounds;
            GdiCompatibleBitmap b1 = null;
            GdiCompatibleBitmap b2 = null;

            HDC screenDC = GetDC(HWND.NULL);

            try
            {
                // first we copy the window to b1
                b1 = new GdiCompatibleBitmap(r1.Width, r1.Height);
                if (!PrintWindow(new HWND(nWin.Handle), b1.hdcBitmap, PW.PW_RENDERFULLCONTENT))
                    throw new Win32Exception();

                if (r1.Equals(r2))
                {
                    // no cropping needed
                    return b1;
                }
                else
                {
                    // crop the image to the desired size
                    var xCropOffset = r2.Left - r1.Left;
                    var yCropOffset = r2.Top - r1.Top;
                    var cropRect = new ScreenRect(xCropOffset, yCropOffset, r2.Width, r2.Height);
                    b2 = b1.Crop(cropRect);

                    b1.Dispose();
                    return b2;
                }
            }
            catch
            {
                b1?.Dispose();
                b2?.Dispose();
                throw;
            }
            finally
            {
                ReleaseDC(HWND.NULL, screenDC);
            }
        }
    }
}
