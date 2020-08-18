using DirectShow;
using Sonic;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Clowd.Com.Video
{
    class GdiFrameProvider : IFrameProvider
    {
        private IntPtr _srcContext = IntPtr.Zero;
        private IntPtr _destContext = IntPtr.Zero;
        private DateTime _lastMouseClick = DateTime.Now.AddSeconds(-5);
        private Point _lastMouseClickPosition = new Point(0, 0);
        private CaptureProperties _properties;
        private GDI32.BitmapInfo _bmi;

        public GdiFrameProvider()
        {
            _srcContext = USER32.GetWindowDC(IntPtr.Zero);
            _destContext = GDI32.CreateCompatibleDC(_srcContext);
            _properties = new CaptureProperties();
            _bmi = GetBitmapInfo(_properties);
        }

        public int CopyScreenToSamplePtr(ref IMediaSampleImpl _sample)
        {
            var srcHdc = _srcContext;
            var destHdc = _destContext;
            var captureArea = new Rectangle(_properties.X, _properties.Y, _properties.PixelWidth, _properties.PixelHeight);

            if (srcHdc == IntPtr.Zero || destHdc == IntPtr.Zero)
                return COMHelper.E_FAIL;

            IntPtr _ptr;
            _sample.GetPointer(out _ptr);

            // Copy screen to native bitmap
            IntPtr destBitmap = GDI32.CreateCompatibleBitmap(srcHdc, captureArea.Width, captureArea.Height);
            IntPtr hOld = GDI32.SelectObject(destHdc, destBitmap);
            GDI32.BitBlt(destHdc, 0, 0, captureArea.Width, captureArea.Height, srcHdc, captureArea.X, captureArea.Y, GDI32.TernaryRasterOperations.SRCCOPY | GDI32.TernaryRasterOperations.CAPTUREBLT);

            // draw cursor
            DrawCursor(destHdc, captureArea);

            //restore old selection (deselect destBitmap)
            GDI32.SelectObject(destHdc, hOld);

            //copy destBitmap bits to _ptr
            GDI32.GetDIBits(destHdc, destBitmap, 0, (uint)Math.Abs(captureArea.Height), _ptr, ref _bmi, 0);

            //clean up
            GDI32.DeleteObject(destBitmap);

            return COMHelper.S_OK;
        }

        public void Dispose()
        {
            if (_srcContext != IntPtr.Zero)
            {
                USER32.ReleaseDC(IntPtr.Zero, _srcContext);
                _srcContext = IntPtr.Zero;
            }

            if (_destContext != IntPtr.Zero)
            {
                GDI32.DeleteDC(_destContext);
                _destContext = IntPtr.Zero;
            }
        }

        public int SetCaptureProperties(CaptureProperties properties)
        {
            _properties = properties.Clone();
            _bmi = GetBitmapInfo(properties);
            return COMHelper.S_OK;
        }

        public int GetCaptureProperties(out CaptureProperties properties)
        {
            properties = _properties.Clone();
            return COMHelper.S_OK;
        }

        private GDI32.BitmapInfo GetBitmapInfo(CaptureProperties properties)
        {
            GDI32.BitmapInfo bmi = new GDI32.BitmapInfo();
            bmi.bmiHeader = new BitmapInfoHeader();

            bmi.bmiHeader.BitCount = properties.BitCount;
            bmi.bmiHeader.Height = properties.PixelHeight;
            bmi.bmiHeader.Width = properties.PixelWidth;
            bmi.bmiHeader.Compression = COMHelper.BI_RGB;
            bmi.bmiHeader.Planes = 1;
            bmi.bmiHeader.ImageSize = properties.Size;

            return bmi;
        }

        public int DrawCursor(IntPtr hdc, Rectangle captureArea)
        {
            USER32.CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(USER32.CURSORINFO));

            if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
            {
                if (hdc == IntPtr.Zero)
                    return COMHelper.E_FAIL;

                var g = Graphics.FromHdc(hdc);
                var hicon = USER32.CopyIcon(cursorInfo.hCursor);
                USER32.ICONINFO iconInfo;
                int iconX, iconY;
                try
                {
                    if (USER32.GetIconInfo(hicon, out iconInfo))
                    {
                        iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot) - captureArea.X;
                        iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot) - captureArea.Y;

                        if (iconX > captureArea.Width || iconY > captureArea.Height)
                        {
                            // mouse is out of bounds
                            return COMHelper.S_OK;
                        }

                        // draw click animation
                        if (Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_LBUTTON) & 0x8000 /*KEY_PRESSED*/) ||
                            Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_RBUTTON) & 0x8000 /*KEY_PRESSED*/))
                        {
                            _lastMouseClick = DateTime.Now;
                            _lastMouseClickPosition = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
                        }
                        const int animationDuration = 400; //ms 
                        const int animationMaxRadius = 25; //pixels 
                        var lastClickSpan = Convert.ToInt32((DateTime.Now - _lastMouseClick).TotalMilliseconds);
                        if (lastClickSpan < animationDuration)
                        {
                            const int maxRadius = animationMaxRadius;
                            using (SolidBrush semiTransBrush = new SolidBrush(Color.FromArgb((int)((1 - (lastClickSpan / (double)animationDuration)) * 255), 255, 0, 0)))
                            {
                                int radius = (int)((lastClickSpan / (double)animationDuration) * maxRadius);
                                var rect = new Rectangle(_lastMouseClickPosition.X - radius, _lastMouseClickPosition.Y - radius, radius * 2, radius * 2);
                                g.FillEllipse(semiTransBrush, rect);
                            }
                        }

                        // draw icon. if drawing to a hdc and not an hbitmap, DrawIconEx seems to apply the icon mask properly for monochrome cursors
                        USER32.DrawIconEx(hdc, iconX, iconY, hicon, 0, 0, 0, IntPtr.Zero, USER32.DrawIconExFlags.DI_NORMAL | USER32.DrawIconExFlags.DI_DEFAULTSIZE);
                    }
                }
                catch
                {
                    return COMHelper.E_FAIL;
                }
                finally
                {
                    g.Dispose();
                    USER32.DestroyIcon(hicon);
                }
            }

            return COMHelper.S_OK;
        }
    }
}
