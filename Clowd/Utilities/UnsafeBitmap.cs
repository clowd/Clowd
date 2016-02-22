using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    public unsafe class UnsafeBitmap : IDisposable
    {
        public ColorBgra* Pointer { get; private set; }
        public bool IsLocked { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public int PixelCount
        {
            get
            {
                return Width * Height;
            }
        }

        private Bitmap bitmap;
        private BitmapData bitmapData;

        public UnsafeBitmap(Bitmap bitmap, bool lockBitmap = false, ImageLockMode imageLockMode = ImageLockMode.ReadWrite)
        {
            this.bitmap = bitmap;
            Width = bitmap.Width;
            Height = bitmap.Height;

            if (lockBitmap)
            {
                Lock(imageLockMode);
            }
        }

        public void Lock(ImageLockMode imageLockMode = ImageLockMode.ReadWrite)
        {
            if (!IsLocked)
            {
                IsLocked = true;
                bitmapData = bitmap.LockBits(new Rectangle(0, 0, Width, Height), imageLockMode, PixelFormat.Format32bppArgb);
                Pointer = (ColorBgra*)bitmapData.Scan0.ToPointer();
            }
        }
        public void Unlock()
        {
            if (IsLocked)
            {
                bitmap.UnlockBits(bitmapData);
                bitmapData = null;
                Pointer = null;
                IsLocked = false;
            }
        }

        public static bool operator ==(UnsafeBitmap bmp1, UnsafeBitmap bmp2)
        {
            return ReferenceEquals(bmp1, bmp2) || bmp1.Equals(bmp2);
        }
        public static bool operator !=(UnsafeBitmap bmp1, UnsafeBitmap bmp2)
        {
            return !(bmp1 == bmp2);
        }

        public override bool Equals(object obj)
        {
            return obj is UnsafeBitmap && Compare((UnsafeBitmap)obj, this);
        }
        public override int GetHashCode()
        {
            return PixelCount;
        }
        public static bool Compare(UnsafeBitmap bmp1, UnsafeBitmap bmp2)
        {
            int pixelCount = bmp1.PixelCount;

            if (pixelCount != bmp2.PixelCount)
            {
                return false;
            }

            bmp1.Lock(ImageLockMode.ReadOnly);
            bmp2.Lock(ImageLockMode.ReadOnly);

            ColorBgra* pointer1 = bmp1.Pointer;
            ColorBgra* pointer2 = bmp2.Pointer;

            for (int i = 0; i < pixelCount; i++)
            {
                if (pointer1->Bgra != pointer2->Bgra)
                {
                    return false;
                }

                pointer1++;
                pointer2++;
            }

            return true;
        }

        public ColorBgra GetPixel(int i)
        {
            return Pointer[i];
        }
        public ColorBgra GetPixel(int x, int y)
        {
            return Pointer[x + y * Width];
        }

        public void SetPixel(int i, ColorBgra color)
        {
            Pointer[i] = color;
        }
        public void SetPixel(int i, uint color)
        {
            Pointer[i] = color;
        }
        public void SetPixel(int x, int y, ColorBgra color)
        {
            Pointer[x + y * Width] = color;
        }
        public void SetPixel(int x, int y, uint color)
        {
            Pointer[x + y * Width] = color;
        }

        public void ClearPixel(int i)
        {
            Pointer[i] = 0;
        }
        public void ClearPixel(int x, int y)
        {
            Pointer[x + y * Width] = 0;
        }

        public void Dispose()
        {
            Unlock();
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct ColorBgra
        {
            [FieldOffset(0)]
            public uint Bgra;

            [FieldOffset(0)]
            public byte Blue;
            [FieldOffset(1)]
            public byte Green;
            [FieldOffset(2)]
            public byte Red;
            [FieldOffset(3)]
            public byte Alpha;

            public const byte SizeOf = 4;

            public ColorBgra(uint bgra)
                : this()
            {
                Bgra = bgra;
            }
            public ColorBgra(byte b, byte g, byte r, byte a = 255)
                : this()
            {
                Blue = b;
                Green = g;
                Red = r;
                Alpha = a;
            }
            public ColorBgra(Color color)
                : this(color.B, color.G, color.R, color.A)
            {
            }

            public static bool operator ==(ColorBgra c1, ColorBgra c2)
            {
                return c1.Bgra == c2.Bgra;
            }
            public static bool operator !=(ColorBgra c1, ColorBgra c2)
            {
                return c1.Bgra != c2.Bgra;
            }
            public override bool Equals(object obj)
            {
                return obj is ColorBgra && ((ColorBgra)obj).Bgra == Bgra;
            }
            public override int GetHashCode()
            {
                unchecked
                {
                    return (int)Bgra;
                }
            }
            public static implicit operator ColorBgra(uint color)
            {
                return new ColorBgra(color);
            }
            public static implicit operator uint(ColorBgra color)
            {
                return color.Bgra;
            }

            public Color ToColor()
            {
                return Color.FromArgb(Alpha, Red, Green, Blue);
            }
            public override string ToString()
            {
                return string.Format("B: {0}, G: {1}, R: {2}, A: {3}", Blue, Green, Red, Alpha);
            }

            public static uint BgraToUInt32(uint b, uint g, uint r, uint a)
            {
                return b + (g << 8) + (r << 16) + (a << 24);
            }
            public static uint BgraToUInt32(byte b, byte g, byte r, byte a)
            {
                return b + ((uint)g << 8) + ((uint)r << 16) + ((uint)a << 24);
            }
        }
    }

}
