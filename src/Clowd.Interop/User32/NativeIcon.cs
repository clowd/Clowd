using Clowd.Interop.Gdi32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Interop
{
    public class NativeIcon : IDisposable
    {
        /// <summary>
        /// The original handle used to construct this class. This can be used to cache and compare instances of NativeIcon. Please see <see cref="hIcon"/> for a handle that should be used for drawing.
        /// </summary>
        public IntPtr Handle { get; }

        /// <summary>
        /// A cached GDI+ Icon of <see cref="hIcon"/>
        /// </summary>
        public Icon Icon
        {
            get
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(NativeIcon));

                if (_gdiIcon == null)
                    _gdiIcon = System.Drawing.Icon.FromHandle(hIcon);
                return _gdiIcon;
            }
        }

        /// <summary>
        /// A cached GDI+ Bitmap of <see cref="hIcon"/>
        /// </summary>
        public Bitmap Bitmap
        {
            get
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(NativeIcon));

                if (_gdiBitmap == null)
                    _gdiBitmap = Icon.ToBitmap();
                return _gdiBitmap;
            }
        }

        /// <summary>
        /// A cached GDI+ copy of <see cref="hBitmapMask"/>
        /// </summary>
        public Bitmap BitmapMask
        {
            get
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(NativeIcon));

                if (_gdiBitmapMask == null && hBitmapMask != IntPtr.Zero)
                    _gdiBitmapMask = System.Drawing.Bitmap.FromHbitmap(hBitmapMask);
                return _gdiBitmapMask;
            }
        }

        /// <summary>
        /// A cached GDI+ copy of <see cref="hBitmapColor"/>
        /// </summary>
        public Bitmap BitmapColor
        {
            get
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(NativeIcon));

                if (_gdiBitmapColor == null && hBitmapColor != IntPtr.Zero)
                    _gdiBitmapColor = System.Drawing.Bitmap.FromHbitmap(hBitmapColor);
                return _gdiBitmapColor;
            }
        }

        /// <summary>
        /// A handle to a copy of the cursor or icon used to construct this object. Should be used for native drawing operations.
        /// </summary>
        public IntPtr hIcon { get; private set; }

        /// <summary>
        /// The icon bitmask bitmap. If this structure defines a black and white icon, this bitmask is formatted so that the upper half is the icon AND bitmask and the lower half is the icon XOR bitmask. 
        /// Under this condition, the height should be an even multiple of two. If this structure defines a color icon, this mask only defines the AND bitmask of the icon.
        /// </summary>
        public IntPtr hBitmapMask { get; private set; }

        /// <summary>
        /// A handle to the icon color bitmap. This member can be optional if this structure defines a black and white icon. 
        /// The AND bitmask of hbmMask is applied with the SRCAND flag to the destination; subsequently, the color bitmap is applied (using XOR) to the destination by using the SRCINVERT flag.
        /// </summary>
        public IntPtr hBitmapColor { get; private set; }

        /// <summary>
        /// Describes whether this class was constructed with a handle to a native cursor or a native icon
        /// </summary>
        public NativeIconType Type { get; }

        /// <summary>
        /// The y-coordinate of the cursor's hot spot. If this structure defines an icon, the hot spot is always in the center of the icon, and this member is ignored.
        /// </summary>
        public Point Hotspot { get; }

        //public Point CursorPosition { get; private set; }
        //public bool IsCursorShowing { get; private set; }

        /// <summary>
        /// Indicates whether the underlying native and GDI+ managed resources have been disposed of.
        /// </summary>
        public bool IsDisposed { get; private set; }

        private Icon _gdiIcon;
        private Bitmap _gdiBitmap;
        private Bitmap _gdiBitmapMask;
        private Bitmap _gdiBitmapColor;

        private NativeIcon(IntPtr handle)
        {
            var hicon = USER32.CopyIcon(handle);

            ICONINFO iconInfo = default(ICONINFO);
            if (!USER32.GetIconInfo(hicon, out iconInfo))
                throw new Win32Exception();

            Handle = handle;
            hIcon = hicon;
            Type = iconInfo.fIcon ? NativeIconType.Icon : NativeIconType.Cursor;
            Hotspot = new Point(iconInfo.xHotspot, iconInfo.yHotspot);
            hBitmapMask = iconInfo.hbmMask;
            hBitmapColor = iconInfo.hbmColor;
        }

        public static NativeIcon FromHandle(IntPtr handle)
        {
            return new NativeIcon(handle);
        }

        //public static NativeIcon FromSystemCursor()
        //{
        //    CURSORINFO cursorInfo;
        //    cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        //    if (!USER32.GetCursorInfo(out cursorInfo))
        //        throw new Win32Exception();

        //    var icon = new NativeIcon(cursorInfo.hCursor);
        //    icon.CursorPosition = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
        //    icon.IsCursorShowing = cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/;
        //    return icon;
        //}

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;

            _gdiBitmapMask?.Dispose();
            _gdiBitmapColor?.Dispose();
            _gdiBitmap?.Dispose();
            _gdiIcon?.Dispose();

            USER32.DestroyIcon(hIcon);
            hIcon = IntPtr.Zero;

            if (hBitmapMask != IntPtr.Zero)
                GDI32.DeleteObject(hBitmapMask);
            hBitmapMask = IntPtr.Zero;

            if (hBitmapColor != IntPtr.Zero)
                GDI32.DeleteObject(hBitmapColor);
            hBitmapColor = IntPtr.Zero;
        }

        /// <summary>
        /// Will draw the icon image and mask (if specified) to the HDC. Point pt is relative to the icon Hotspot
        /// </summary>
        public void DrawToHdc(IntPtr hdc, Point pt)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(NativeIcon));

            int iconX = pt.X - Hotspot.X;
            int iconY = pt.Y - Hotspot.Y;

            // draw icon. if drawing to a hdc and not an hbitmap, DrawIconEx seems to apply the icon mask properly for monochrome cursors
            USER32.DrawIconEx(hdc, iconX, iconY, hIcon, 0, 0, 0, IntPtr.Zero, DrawIconExFlags.DI_NORMAL | DrawIconExFlags.DI_DEFAULTSIZE);
        }

        /// <summary>
        /// Will draw the icon image and mask (if specified) to the Graphics Context. This is a convenience wrapper around <see cref="DrawToHdc(IntPtr, Point)"/>. 
        /// Point pt is relative to the icon Hotspot.
        /// </summary>
        public void DrawToGraphics(Graphics g, Point pt)
        {
            var hdc = g.GetHdc();
            try
            {
                DrawToHdc(hdc, pt);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        public unsafe void DrawToBitmap(Bitmap bitmap, Point pt)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(NativeIcon));

            int iconX = pt.X - Hotspot.X;
            int iconY = pt.Y - Hotspot.Y;

            // Is this a color cursor or a monochrome one?
            if (hBitmapColor != IntPtr.Zero)
            {
                // color cursor is easy to draw!
                using (Graphics g = Graphics.FromImage(bitmap))
                    g.DrawImage(Bitmap, iconX, iconY);
            }
            else
            {
                // According to the ICONINFO documentation, monochrome cursors (such as I-Beam cursor):
                // The top half of the mask bitmap is the AND mask, and the bottom half of the mask bitmap is the XOR bitmap. 
                // When Windows draws the I-Beam cursor, the top half of this bitmap is first drawn over the desktop with an AND raster operation. 
                // The bottom half of the bitmap is then drawn over top with an XOR raster operation. 
                // Onscreen, The cursor should will appear as the inverse of the content behind it.
                // https://docs.microsoft.com/en-us/windows-hardware/drivers/display/drawing-monochrome-pointers

                // NOTE: BitBlt SRCCPY / SRCINVERT does not work unless operating directly on a native hdc, otherwise it results in an always white cursor 
                // so instead we do direct bitwise operations on the bitmap.

                // TODO this behaves poorly and can throw if the cursor is near screen edges. need to test and fix this

                if (BitmapMask.Height != BitmapColor.Height * 2 || BitmapMask.Width != BitmapColor.Width)
                    throw new InvalidOperationException("Unable to draw icon, mask bitmap must be same width as color bitmap and twice the height");

                var height = BitmapColor.Height;
                var width = BitmapColor.Width;

                const byte bpp = 3;
                var maskBits = BitmapMask.LockBits(new Rectangle(0, 0, BitmapMask.Width, BitmapMask.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var destBits = bitmap.LockBits(new Rectangle(iconX, iconY, width, height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                try
                {
                    byte* mscan0 = (byte*)maskBits.Scan0.ToPointer();
                    byte* dscan0 = (byte*)destBits.Scan0.ToPointer();

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int destX = iconX + x;
                            int destY = iconY + y;
                            if (destX >= bitmap.Width || destY >= bitmap.Height)
                                continue;

                            //data[0] = blue; data[1] = green; data[2] = red;

                            byte* ANDptr = mscan0 + (y * maskBits.Stride) + (x * bpp);
                            byte* XORptr = mscan0 + ((y + height) * maskBits.Stride) + (x * bpp);
                            byte* DESTptr = dscan0 + (destY * destBits.Stride) + (destX * bpp);

                            for (int p = 0; p < 3; p++)
                            {
                                DESTptr[p] &= ANDptr[p];
                                DESTptr[p] ^= XORptr[p];
                            }
                        }
                    }
                }
                finally
                {
                    BitmapMask.UnlockBits(maskBits);
                    bitmap.UnlockBits(destBits);
                }
            }
        }
    }

    public enum NativeIconType
    {
        Cursor = 0,
        Icon = 1,
    }
}
