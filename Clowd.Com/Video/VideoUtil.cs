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
    static class VideoUtil
    {
        public static int CopyScreenToSamplePtr(IntPtr srcHdc, IntPtr destHdc, Rectangle captureArea, ref GDI32.BitmapInfo m_bmi, ref IMediaSampleImpl _sample)
        {
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
            GDI32.GetDIBits(destHdc, destBitmap, 0, (uint)Math.Abs(captureArea.Height), _ptr, ref m_bmi, 0);

            //clean up
            GDI32.DeleteObject(destBitmap);

            _sample.SetActualDataLength(_sample.GetSize());
            _sample.SetSyncPoint(true);

            return COMHelper.S_OK;
        }


        public static int DrawCursor(IntPtr hdc, Rectangle captureArea)
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
                        //if (Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_LBUTTON) & 0x8000 /*KEY_PRESSED*/) ||
                        //    Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_RBUTTON) & 0x8000 /*KEY_PRESSED*/))
                        //{
                        //    _lastMouseClick = DateTime.Now;
                        //    _lastMouseClickPosition = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
                        //}
                        //const int animationDuration = 400; //ms 
                        //const int animationMaxRadius = 25; //pixels 
                        //var lastClickSpan = Convert.ToInt32((DateTime.Now - _lastMouseClick).TotalMilliseconds);
                        //if (lastClickSpan < animationDuration)
                        //{
                        //    const int maxRadius = animationMaxRadius;
                        //    using (SolidBrush semiTransBrush = new SolidBrush(Color.FromArgb((int)((1 - (lastClickSpan / (double)animationDuration)) * 255), 255, 0, 0)))
                        //    {
                        //        int radius = (int)((lastClickSpan / (double)animationDuration) * maxRadius);
                        //        var rect = new Rectangle(_lastMouseClickPosition.X - radius, _lastMouseClickPosition.Y - radius, radius * 2, radius * 2);
                        //        g.FillEllipse(semiTransBrush, rect);
                        //    }
                        //}

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
