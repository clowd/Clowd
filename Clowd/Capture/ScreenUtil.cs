using Clowd.Interop;
using Clowd.Interop.Gdi32;
using NReco.VideoConverter;
using PropertyChanged;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Clowd.Utilities
{
    public static class ScreenUtil
    {
        public static Bitmap CaptureScreenGdi(ScreenRect? bounds = null, bool captureCursor = true)
        {
            Rectangle captureArea = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();

            // declare unmanaged resources. we need to clean these up later
            IntPtr screenDC = IntPtr.Zero;
            IntPtr targetDC = IntPtr.Zero;
            IntPtr destBitmap = IntPtr.Zero;

            try
            {
                // allocate unmanaged resources
                screenDC = USER32.GetWindowDC(IntPtr.Zero);
                if (screenDC == IntPtr.Zero)
                    throw new Exception("Unable to retrieve reference to screen hDC");

                targetDC = GDI32.CreateCompatibleDC(screenDC);
                if (screenDC == IntPtr.Zero)
                    throw new Exception("Unable to create new screen-compatible in-memory hDC");

                // capture screen
                destBitmap = CopyScreenToNewHBitmap(screenDC, targetDC, captureArea, captureCursor);

                return Bitmap.FromHbitmap(destBitmap);
            }
            finally
            {
                if (destBitmap != IntPtr.Zero)
                    GDI32.DeleteObject(destBitmap);

                if (targetDC != IntPtr.Zero)
                    GDI32.DeleteDC(targetDC);

                if (screenDC != IntPtr.Zero)
                    USER32.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public static BitmapSource CaptureScreenWpf(ScreenRect? bounds = null, bool captureCursor = true)
        {
            Rectangle captureArea = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();

            // declare unmanaged resources. we need to clean these up later
            IntPtr buffer = IntPtr.Zero;
            IntPtr screenDC = IntPtr.Zero;
            IntPtr targetDC = IntPtr.Zero;
            IntPtr destBitmap = IntPtr.Zero;

            // calculate bitmap size for 24bpp
            ushort bitsPerPixel = 24;
            int bytesPerPixel = (bitsPerPixel + 7) / 8;
            int stride = 4 * ((captureArea.Width * bytesPerPixel + 3) / 4);
            int bmpSize = stride * captureArea.Height;

            try
            {
                // allocate unmanaged resources
                buffer = Marshal.AllocHGlobal(bmpSize);

                screenDC = USER32.GetWindowDC(IntPtr.Zero);
                if (screenDC == IntPtr.Zero)
                    throw new Exception("Unable to retrieve reference to screen hDC");

                targetDC = GDI32.CreateCompatibleDC(screenDC);
                if (screenDC == IntPtr.Zero)
                    throw new Exception("Unable to create new screen-compatible in-memory hDC");

                // capture screen
                destBitmap = CopyScreenToNewHBitmap(screenDC, targetDC, captureArea, captureCursor);

                var bmi = new BitmapInfo();
                bmi.bmiHeader = new BITMAPINFOHEADER();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biBitCount = bitsPerPixel;
                // For RGB DIBs, the image orientation is indicated by the biHeight member of the BITMAPINFOHEADER structure.
                // If biHeight is positive, the image is bottom-up. If biHeight is negative, the image is top-down.
                bmi.bmiHeader.biHeight = -captureArea.Height;
                bmi.bmiHeader.biWidth = captureArea.Width;
                bmi.bmiHeader.biCompression = BitmapCompressionMode.BI_RGB;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biSizeImage = (uint)bmpSize;

                // copy bitmap bits to buffer while also converting to device independent bits of the specified format
                var getdiresult = GDI32.GetDIBits(targetDC, destBitmap, 0, (uint)captureArea.Height, buffer, ref bmi, DIBColorMode.DIB_RGB_COLORS);
                if (getdiresult == 0) // If the function fails, the return value is zero.
                    throw new Exception("Unable to copy device independent bits to bitmap buffer");

                // create a new bitmapsource, passing in buffer as scan0
                return BitmapSource.Create(
                    captureArea.Width,
                    captureArea.Height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgr24,
                    null,
                    buffer,
                    bmpSize,
                    stride
                );
            }
            finally
            {
                if (destBitmap != IntPtr.Zero)
                    GDI32.DeleteObject(destBitmap);

                if (targetDC != IntPtr.Zero)
                    GDI32.DeleteDC(targetDC);

                if (screenDC != IntPtr.Zero)
                    USER32.ReleaseDC(IntPtr.Zero, screenDC);

                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr CopyScreenToNewHBitmap(IntPtr screenDC, IntPtr targetDC, Rectangle captureArea, bool captureCursor)
        {
            IntPtr hBitmap = GDI32.CreateCompatibleBitmap(screenDC, captureArea.Width, captureArea.Height);
            if (hBitmap == IntPtr.Zero)
                throw new Exception("Unable to create compatible bitmap");

            // select the hBitmap into our memdc
            var hOld = GDI32.SelectObject(targetDC, hBitmap);

            if (hOld == IntPtr.Zero)
                throw new Exception("Unable to select hBitmap into destContext");

            // copy screen to memdc
            if (!GDI32.BitBlt(targetDC, 0, 0, captureArea.Width, captureArea.Height, screenDC, captureArea.X, captureArea.Y, TernaryRasterOperations.SRCCOPY | TernaryRasterOperations.CAPTUREBLT))
                throw new Win32Exception();

            if (captureCursor)
                DrawCursorToDC(targetDC, captureArea);

            // deselect bitmap / restore original selection
            GDI32.SelectObject(targetDC, hOld);

            return hBitmap;
        }

        private static void DrawCursorToDC(IntPtr hdc, Rectangle captureArea)
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));

            if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
            {
                var hicon = USER32.CopyIcon(cursorInfo.hCursor);
                ICONINFO iconInfo = default(ICONINFO);
                try
                {
                    if (USER32.GetIconInfo(hicon, out iconInfo))
                    {
                        int iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot) - captureArea.X;
                        int iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot) - captureArea.Y;

                        if (iconX > captureArea.Width || iconY > captureArea.Height)
                        {
                            // mouse is out of bounds
                            return;
                        }

                        // draw icon. if drawing to a hdc and not an hbitmap, DrawIconEx seems to apply the icon mask properly for monochrome cursors
                        USER32.DrawIconEx(hdc, iconX, iconY, hicon, 0, 0, 0, IntPtr.Zero, DrawIconExFlags.DI_NORMAL | DrawIconExFlags.DI_DEFAULTSIZE);
                    }
                }
                finally
                {
                    USER32.DestroyIcon(hicon);

                    if (iconInfo.hbmColor != IntPtr.Zero)
                        GDI32.DeleteObject(iconInfo.hbmColor);

                    if (iconInfo.hbmMask != IntPtr.Zero)
                        GDI32.DeleteObject(iconInfo.hbmColor);
                }
            }
        }
    }
}
