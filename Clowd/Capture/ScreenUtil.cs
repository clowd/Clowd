using Clowd.Interop;
using Clowd.Interop.Gdi32;
using NReco.VideoConverter;
using PropertyChanged;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    public sealed class ScreenUtil : IDisposable
    {
        //private IntPtr _screenDC = IntPtr.Zero;
        //private IntPtr _targetDC = IntPtr.Zero;
        private readonly object _lock = new object();
        private bool _isDisposed = false;
        private const ushort BITS_PER_PIXEL = 24;

        public ScreenUtil()
        {
            // allocate unmanaged resources
            //_screenDC = USER32.GetWindowDC(IntPtr.Zero);
            //if (_screenDC == IntPtr.Zero)
            //    throw new Exception("Unable to retrieve reference to screen hDC");

            //_targetDC = GDI32.CreateCompatibleDC(_screenDC);
            //if (_screenDC == IntPtr.Zero)
            //    throw new Exception("Unable to create new screen-compatible in-memory hDC");
        }

        //public Bitmap CaptureScreenGdiPlus(ScreenRect? bounds = null, bool captureCursor = true)
        //{
        //    Rectangle captureArea = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();
        //    IntPtr destBitmap = IntPtr.Zero;
        //    lock (_lock)
        //    {
        //        EnsureNotDisposed();
        //        try
        //        {
        //            destBitmap = CopyScreenToNewHBitmap(_screenDC, _targetDC, captureArea, captureCursor);
        //            return Bitmap.FromHbitmap(destBitmap);
        //        }
        //        finally
        //        {
        //            if (destBitmap != IntPtr.Zero)
        //                GDI32.DeleteObject(destBitmap);
        //        }
        //    }
        //}

        public BitmapSource CaptureScreenWpf(ScreenRect? bounds = null, bool captureCursor = true, System.Diagnostics.Stopwatch sw = null)
        {
            Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI start");
            // allocate unmanaged resources
            var _screenDC = USER32.GetWindowDC(IntPtr.Zero);
            if (_screenDC == IntPtr.Zero)
                throw new Exception("Unable to retrieve reference to screen hDC");

            var _targetDC = GDI32.CreateCompatibleDC(_screenDC);
            if (_screenDC == IntPtr.Zero)
                throw new Exception("Unable to create new screen-compatible in-memory hDC");
            Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI hdc allocated");

            Rectangle captureArea = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();
            IntPtr destBitmap = IntPtr.Zero;
            var bitmapSize = GetBitmapSize(captureArea);

            lock (_lock)
            {
                EnsureNotDisposed();
                try
                {
                    destBitmap = CopyScreenToNewHBitmap(_screenDC, _targetDC, captureArea, captureCursor);
                    Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI screen copied");

                    var writable = new WriteableBitmap(
                        captureArea.Width,
                        captureArea.Height,
                        96,
                        96,
                        System.Windows.Media.PixelFormats.Bgr24,
                        null);

                    writable.Lock();
                    Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI WritableBitmap created");

                    CopyBitmapDIBitsToBuffer(destBitmap, _targetDC, writable.BackBuffer, captureArea.Width, captureArea.Height, bitmapSize.stride);
                    Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI copied to buffer");

                    writable.AddDirtyRect(new System.Windows.Int32Rect(0, 0, captureArea.Width, captureArea.Height));

                    writable.Unlock();
                    writable.Freeze();
                    Console.WriteLine($"+{sw?.ElapsedMilliseconds}ms - GDI frozen");
                    return writable;
                }
                finally
                {
                    if (destBitmap != IntPtr.Zero)
                        GDI32.DeleteObject(destBitmap);

                    if (_targetDC != IntPtr.Zero)
                        GDI32.DeleteDC(_targetDC);

                    if (_screenDC != IntPtr.Zero)
                        USER32.ReleaseDC(IntPtr.Zero, _screenDC);
                }
            }
        }

        //public Bitmap PrintWindowGdiPlus(IntPtr hWnd)
        //{
        //    if (!USER32.GetWindowRect(hWnd, out RECT normalWindowBoundsNative))
        //        throw new Win32Exception();

        //    Rectangle windowBoundsNormal = normalWindowBoundsNative;
        //    Rectangle windowBoundsTrue = USER32EX.GetTrueWindowBounds(hWnd);
        //    var xCropOffset = windowBoundsTrue.Left - windowBoundsNormal.Left;
        //    var yCropOffset = windowBoundsTrue.Top - windowBoundsNormal.Top;

        //    IntPtr destBitmap = IntPtr.Zero;

        //    lock (_lock)
        //    {
        //        EnsureNotDisposed();
        //        try
        //        {
        //            destBitmap = CopyWindowToNewHBitmap(hWnd, _screenDC, _targetDC, windowBoundsNormal);
        //            var gdip = Bitmap.FromHbitmap(destBitmap);

        //            if (xCropOffset == 0 && yCropOffset == 0)
        //            {
        //                return gdip;
        //            }
        //            else
        //            {
        //                using (gdip)
        //                {
        //                    var croppingRectangle = new Rectangle(xCropOffset, yCropOffset, windowBoundsTrue.Width, windowBoundsTrue.Height);
        //                    return gdip.Crop(croppingRectangle);
        //                }
        //            }
        //        }
        //        finally
        //        {
        //            if (destBitmap != IntPtr.Zero)
        //                GDI32.DeleteObject(destBitmap);
        //        }
        //    }
        //}

        public BitmapSource PrintWindowWpf(IntPtr hWnd)
        {
            if (!USER32.GetWindowRect(hWnd, out RECT normalWindowBoundsNative))
                throw new Win32Exception();

            // allocate unmanaged resources
            var _screenDC = USER32.GetWindowDC(IntPtr.Zero);
            if (_screenDC == IntPtr.Zero)
                throw new Exception("Unable to retrieve reference to screen hDC");

            var _targetDC = GDI32.CreateCompatibleDC(_screenDC);
            if (_screenDC == IntPtr.Zero)
                throw new Exception("Unable to create new screen-compatible in-memory hDC");

            Rectangle windowBoundsNormal = normalWindowBoundsNative;
            Rectangle windowBoundsTrue = USER32EX.GetTrueWindowBounds(hWnd);
            var xCropOffset = windowBoundsTrue.Left - windowBoundsNormal.Left;
            var yCropOffset = windowBoundsTrue.Top - windowBoundsNormal.Top;

            IntPtr destBitmap = IntPtr.Zero;
            var bitmapSize = GetBitmapSize(windowBoundsNormal);

            lock (_lock)
            {
                EnsureNotDisposed();
                try
                {
                    destBitmap = CopyWindowToNewHBitmap(hWnd, _screenDC, _targetDC, windowBoundsNormal);

                    var writable = new WriteableBitmap(
                       windowBoundsNormal.Width,
                       windowBoundsNormal.Height,
                       96,
                       96,
                       System.Windows.Media.PixelFormats.Bgr24,
                       null);

                    writable.Lock();

                    CopyBitmapDIBitsToBuffer(destBitmap, _targetDC, writable.BackBuffer, windowBoundsNormal.Width, windowBoundsNormal.Height, bitmapSize.stride);

                    writable.AddDirtyRect(new System.Windows.Int32Rect(0, 0, windowBoundsNormal.Width, windowBoundsNormal.Height));
                    writable.Unlock();
                    writable.Freeze();

                    if (xCropOffset == 0 && yCropOffset == 0)
                    {
                        return writable;
                    }
                    else
                    {
                        var croppingRectangle = new System.Windows.Int32Rect(xCropOffset, yCropOffset, windowBoundsTrue.Width, windowBoundsTrue.Height);
                        var croppedSource = new CroppedBitmap(writable, croppingRectangle);
                        croppedSource.Freeze();
                        return croppedSource;
                    }
                }
                finally
                {
                    if (_targetDC != IntPtr.Zero)
                        GDI32.DeleteDC(_targetDC);

                    if (_screenDC != IntPtr.Zero)
                        USER32.ReleaseDC(IntPtr.Zero, _screenDC);

                    if (destBitmap != IntPtr.Zero)
                        GDI32.DeleteObject(destBitmap);
                }
            }
        }

        public void EnsureNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ScreenUtil));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
            }

            //if (_targetDC != IntPtr.Zero)
            //    GDI32.DeleteDC(_targetDC);

            //if (_screenDC != IntPtr.Zero)
            //    USER32.ReleaseDC(IntPtr.Zero, _screenDC);
        }

        private (int stride, int size) GetBitmapSize(Rectangle captureArea)
        {
            const int bytesPerPixel = (BITS_PER_PIXEL + 7) / 8;
            int stride = 4 * ((captureArea.Width * bytesPerPixel + 3) / 4);
            int bmpSize = stride * captureArea.Height;
            return (stride, bmpSize);
        }

        private void CopyBitmapDIBitsToBuffer(IntPtr hBitmap, IntPtr hMemDC, IntPtr destBuffer, int width, int height, int stride)
        {
            var bmi = new BitmapInfo();
            bmi.bmiHeader = new BITMAPINFOHEADER();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biBitCount = BITS_PER_PIXEL;
            // For RGB DIBs, the image orientation is indicated by the biHeight member of the BITMAPINFOHEADER structure.
            // If biHeight is positive, the image is bottom-up. If biHeight is negative, the image is top-down.
            bmi.bmiHeader.biHeight = -height;
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biCompression = BitmapCompressionMode.BI_RGB;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biSizeImage = (uint)(stride * height);

            // copy bitmap bits to buffer while also converting to device independent bits of the specified format
            var getdiresult = GDI32.GetDIBits(hMemDC, hBitmap, 0, (uint)height, destBuffer, ref bmi, DIBColorMode.DIB_RGB_COLORS);
            if (getdiresult == 0) // If the function fails, the return value is zero.
                throw new Exception("Unable to copy device independent bits to bitmap buffer");
        }

        private static IntPtr CopyWindowToNewHBitmap(IntPtr hWnd, IntPtr screenDC, IntPtr targetDC, Rectangle windowBounds)
        {
            IntPtr hBitmap = GDI32.CreateCompatibleBitmap(screenDC, windowBounds.Width, windowBounds.Height);
            if (hBitmap == IntPtr.Zero)
                throw new Exception("Unable to create compatible bitmap");

            // select the hBitmap into our memdc
            var hOld = GDI32.SelectObject(targetDC, hBitmap);

            if (hOld == IntPtr.Zero)
                throw new Exception("Unable to select hBitmap into destContext");

            // window to to memdc
            if (!USER32.PrintWindow(hWnd, targetDC, PrintWindowDrawingOptions.PW_RENDERFULLCONTENT))
                throw new Win32Exception();

            // deselect bitmap / restore original selection
            GDI32.SelectObject(targetDC, hOld);

            return hBitmap;
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
                using (var nIcon = NativeIcon.FromHandle(cursorInfo.hCursor))
                {
                    var cursorPos = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
                    var iconRect = new Rectangle(cursorInfo.ptScreenPos.x - nIcon.Hotspot.X, cursorInfo.ptScreenPos.y - nIcon.Hotspot.Y, nIcon.Icon.Width, nIcon.Icon.Height);

                    if (captureArea.IntersectsWith(iconRect))
                        nIcon.DrawToHdc(hdc, cursorPos);
                }
            }
        }
    }
}
