using System;

namespace Clowd.PlatformUtil
{
    public enum BitmapPixelFormat
    {
        Indexed1 = 1,
        Indexed4 = 4,
        Indexed8 = 8,
        Rgb16 = 16,
        Rgb24 = 24,
        Argb32 = 32,
    }

    public unsafe interface IBitmap : IDisposable
    {
        BitmapPixelFormat SourcePixelFormat { get; }
        ushort BitsPerPixel { get; }
        int Width { get; }
        int Height { get; }
        int Stride { get; }
        int Size { get; }

        void CopyTo(IntPtr buffer0, int bufferSize, BitmapPixelFormat destFormat);
        void CopyTo(byte* buffer0, int bufferSize, BitmapPixelFormat destFormat);
        void CopyTo(byte[] buffer, BitmapPixelFormat destFormat);

        byte[] CopyPixelsToArray();
    }

    public abstract unsafe class BitmapBase : IBitmap
    {
        public static int GetStride(ushort bbp, int width)
            => (bbp * width + 31) / 32 * 4;

        public static ushort GetBppForPixelFormat(BitmapPixelFormat format) => format switch
        {
            BitmapPixelFormat.Indexed1 => 1,
            BitmapPixelFormat.Indexed4 => 4,
            BitmapPixelFormat.Indexed8 => 8,
            BitmapPixelFormat.Rgb16 => 16,
            BitmapPixelFormat.Rgb24 => 24,
            BitmapPixelFormat.Argb32 => 32,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        public static BitmapPixelFormat GetPixelFormatForBpp(ushort bpp) => bpp switch
        {
            1 => BitmapPixelFormat.Indexed1,
            4 => BitmapPixelFormat.Indexed4,
            8 => BitmapPixelFormat.Indexed8,
            16 => BitmapPixelFormat.Rgb16,
            24 => BitmapPixelFormat.Rgb24,
            32 => BitmapPixelFormat.Argb32,
            _ => throw new ArgumentOutOfRangeException(nameof(bpp)),
        };

        public static int GetSize(int width, int height, BitmapPixelFormat fmt)
            => height * GetStride(GetBppForPixelFormat(fmt), width);

        public virtual BitmapPixelFormat SourcePixelFormat { get; init; }

        public virtual int Width { get; init; }

        public virtual int Height { get; init; }

        public virtual ushort BitsPerPixel => GetBppForPixelFormat(SourcePixelFormat);

        public virtual int Stride => GetStride(BitsPerPixel, Width);

        public virtual int Size => Height * Stride;

        protected BitmapBase() { }

        protected BitmapBase(int width, int height, BitmapPixelFormat sourceFmt)
        {
            Width = width;
            Height = height;
            SourcePixelFormat = sourceFmt;
        }

        ~BitmapBase()
        {
            Dispose();
        }

        public virtual void CopyTo(IntPtr buffer0, int bufferSize, BitmapPixelFormat destFormat)
        {
            CopyTo((byte*)buffer0, bufferSize, destFormat);
        }

        public virtual void CopyTo(byte[] buffer, BitmapPixelFormat destFormat)
        {
            fixed (byte* b0 = buffer)
                CopyTo(b0, buffer.Length, destFormat);
        }

        public virtual void CopyTo(byte* buffer0, int bufferSize, BitmapPixelFormat destFormat)
        {
            var neededSpace = GetSize(Width, Height, destFormat);
            if (neededSpace < bufferSize)
                throw new InvalidOperationException($"Not enough space in target buffer. Needed >= {neededSpace}, Actual = {bufferSize}.");

            CopyToImpl(buffer0, destFormat);
        }

        public abstract void CopyToImpl(byte* buffer0, BitmapPixelFormat destFormat);

        public virtual byte[] CopyPixelsToArray()
        {
            byte[] buf = new byte[Size];
            fixed (byte* b0 = buf)
                CopyToImpl(b0, SourcePixelFormat);
            return buf;
        }

        public abstract void Dispose();
    }
}
