﻿using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.DwmApi;
using static Vanara.PInvoke.Shell32;
using static Vanara.PInvoke.Gdi32;

namespace Clowd.PlatformUtil.Windows
{
    public unsafe class GdiCompatibleBitmap : BitmapBase
    {
        public nint Handle => (IntPtr)hBitmap;
        //public nint HdcHandle => hdcBitmap;

        internal readonly HDC hdcBitmap;
        internal readonly HBITMAP hBitmap;
        readonly HDC hdcScreen;
        readonly HGDIOBJ hOld;

        public GdiCompatibleBitmap(int width, int height) : base(width, height, BitmapPixelFormat.Rgb24)
        {
            hdcScreen = GetDC(HWND.NULL);
            hdcBitmap = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            hOld = SelectObject(hdcBitmap, hBitmap);
        }

        public GdiCompatibleBitmap(nint bmp) : this(new HBITMAP(bmp))
        { }

        internal GdiCompatibleBitmap(HBITMAP bmp)
        {
            // get bitmap size
            var info = new BITMAP();
            GetObject(bmp, sizeof(BITMAP), (IntPtr)(&info));
            Width = info.bmWidth;
            Height = info.bmHeight;
            SourcePixelFormat = GetPixelFormatForBpp(info.bmBitsPixel);

            // setup
            hdcScreen = GetDC(HWND.NULL);
            hdcBitmap = CreateCompatibleDC(hdcScreen);
            hBitmap = bmp;
            hOld = SelectObject(hdcBitmap, hBitmap);
        }

        public override void CopyToImpl(byte* buffer0, BitmapPixelFormat destFormat)
        {
            var bpp = GetBppForPixelFormat(destFormat);
            var bmiSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)bmiSize,
                biBitCount = bpp,
                biClrImportant = 0,
                biClrUsed = 0,
                biCompression = BitmapCompressionMode.BI_RGB,
                // For RGB DIBs, the image orientation is indicated by the biHeight member of the BITMAPINFOHEADER structure.
                // If biHeight is positive, the image is bottom-up. If biHeight is negative, the image is top-down.
                biHeight = -Height,
                biWidth = Width,
                biPlanes = 1,
                biXPelsPerMeter = 0,
                biYPelsPerMeter = 0,
                biSizeImage = (uint)(Stride * Height),
            };

            using var info = new SafeBITMAPINFO(bmi);

            // copy pixels
            var hr = GetDIBits(hdcBitmap, hBitmap, 0, (uint)Height, (IntPtr)buffer0, info, DIBColorMode.DIB_RGB_COLORS);
            if (hr == 0) // If the function fails, the return value is zero.
                throw new Exception("Unable to copy pixels to bitmap buffer");
        }

        public override void Dispose()
        {
            SelectObject(hdcBitmap, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcBitmap);
            ReleaseDC(HWND.NULL, hdcScreen);
        }

        public GdiCompatibleBitmap Crop(ScreenRect croppedRect)
        {
            int x = croppedRect.X, y = croppedRect.Y, width = croppedRect.Width, height = croppedRect.Height;

            var dest = new GdiCompatibleBitmap(width, height);

            if (BitBlt(dest.hdcBitmap, 0, 0, width, height, hdcBitmap, x, y, RasterOperationMode.SRCCOPY))
            {
                return dest;
            }
            else
            {
                dest.Dispose();
                throw new Win32Exception();
            }
        }

        public GdiCompatibleBitmap MakeGrayscale()
        {
            const int NUM_COLORS = 256;
            var stride = GetStride(8, Width);
            var bmiSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            var colorSize = Marshal.SizeOf<RGBQUAD>();
            var paletteSize = colorSize * NUM_COLORS;

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)bmiSize,
                biBitCount = 8,
                biClrImportant = NUM_COLORS,
                biClrUsed = NUM_COLORS,
                biCompression = BitmapCompressionMode.BI_RGB,
                biHeight = Height,
                biWidth = Width,
                biPlanes = 1,
                biXPelsPerMeter = 0,
                biYPelsPerMeter = 0,
                biSizeImage = (uint)(stride * Height),
            };

            nint bmp = Marshal.AllocHGlobal(bmiSize + paletteSize);
            try
            {
                Marshal.StructureToPtr(bmi, bmp, false);

                // create 256 gray colors
                byte* cptr = (byte*)(bmp + bmiSize);
                for (byte i = 0;; i++)
                {
                    *cptr++ = i; // r
                    *cptr++ = i; // g
                    *cptr++ = i; // b
                    *cptr++ = 0; // not used
                    if (i == byte.MaxValue) break;
                }

                throw new NotImplementedException();
                //void* pixels;
                //var hbitmap = CreateDIBSection(hdcScreen, (BITMAPINFO*)bmp, DIBColorMode.DIB_RGB_COLORS, &pixels, default, 0);
                //var pinb = new GdiCompatibleBitmap(hbitmap);

                //BitBlt(pinb.hdcBitmap, 0, 0, Width, Height, hdcBitmap, 0, 0, ROP_CODE.SRCCOPY);

                //return pinb;
            }
            finally
            {
                Marshal.FreeHGlobal(bmp);
            }
        }
    }
}
