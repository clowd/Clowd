using CsWin32;
using CsWin32.Foundation;
using CsWin32.UI.WindowsAndMessaging;
using CsWin32.Graphics.Gdi;
using CsWin32.Storage.Xps;
using static CsWin32.Constants;
using static CsWin32.PInvoke;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Clowd.PlatformUtil.Windows
{
    public static class GdiScreenCapture
    {
        private static void DrawScreenCursorToDC(HDC hdc, ScreenRect captureArea)
        {
            CURSORINFO cursorInfo = default;
            cursorInfo.cbSize = (uint)Marshal.SizeOf(typeof(CURSORINFO));
            if (GetCursorInfo(ref cursorInfo) && cursorInfo.flags == CURSORINFO_FLAGS.CURSOR_SHOWING)
            {
                using (var hIcon = new DestroyIconSafeHandle(CopyIcon(cursorInfo.hCursor)))
                {
                    if (GetIconInfo(hIcon, out var iconInfo))
                    {
                        // we are responsible for disposing these when we're done
                        using (var hbmColor = new DeleteObjectSafeHandle(iconInfo.hbmColor))
                        using (var hbmMask = new DeleteObjectSafeHandle(iconInfo.hbmMask))
                        {
                            var iconX = cursorInfo.ptScreenPos.x - (int)iconInfo.xHotspot - captureArea.Left;
                            var iconY = cursorInfo.ptScreenPos.y - (int)iconInfo.yHotspot - captureArea.Top;
                            DrawIconEx(hdc, iconX, iconY, (HICON)hIcon.DangerousGetHandle(), 0, 0, 0, default, DI_FLAGS.DI_NORMAL | DI_FLAGS.DI_DEFAULTSIZE);
                        }
                    }
                }
            }
        }

        public static GdiCompatibleBitmap CaptureScreen(ScreenRect captureArea, bool drawCursor)
        {
            int x = captureArea.X, y = captureArea.Y, width = captureArea.Width, height = captureArea.Height;
            GdiCompatibleBitmap bitmap = null;

            HDC screenDC = GetDC(HWND_DESKTOP);

            try
            {
                bitmap = new GdiCompatibleBitmap(width, height);

                if (!BitBlt(bitmap.hdcBitmap, 0, 0, width, height, screenDC, x, y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
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
                ReleaseDC(HWND_DESKTOP, screenDC);
            }
        }

        public static GdiCompatibleBitmap CaptureWindow(IWindow nWin)
        {
            var r1 = nWin.WindowBounds;
            var r2 = nWin.DwmRenderBounds;
            GdiCompatibleBitmap b1 = null;
            GdiCompatibleBitmap b2 = null;

            HDC screenDC = GetDC(HWND_DESKTOP);

            try
            {
                // first we copy the window to b1
                b1 = new GdiCompatibleBitmap(r1.Width, r1.Height);
                if (!PrintWindow(new HWND(nWin.Handle), b1.hdcBitmap, (PRINT_WINDOW_FLAGS)PW_RENDERFULLCONTENT))
                    throw new Win32Exception();

                if (b1.Equals(b2))
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
                if (b1 != null) b1.Dispose();
                if (b2 != null) b2.Dispose();
                throw;
            }
            finally
            {
                ReleaseDC(HWND_DESKTOP, screenDC);
            }
        }
    }
}
