using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clowd.Utilities
{
    public class ClipboardEx
    {
        private static string[] _knownImageExt = new[] {
            ".png", ".jpg", ".jpeg",".jpe", ".bmp",
            ".gif", ".tif", ".tiff", ".ico" };

        public static BitmapSource GetImage()
        {
            return GetImageFromClipboard() ?? GetImageFromFile();
        }
        //public static Bitmap GetImageWF()
        //{
        //    return GetImageFromClipboard()?.ToBitmapWF() ?? GetImageFromFileWF();
        //}

        public static void SetImage(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                System.Windows.Forms.IDataObject dataObject = new System.Windows.Forms.DataObject();
                dataObject.SetData("PNG", false, ms);
                System.Windows.Forms.Clipboard.SetDataObject(dataObject, false);
            }
        }
        public static void SetImage(BitmapSource bmp)
        {
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;
                System.Windows.Forms.IDataObject dataObject = new System.Windows.Forms.DataObject();
                dataObject.SetData("PNG", false, ms);
                System.Windows.Forms.Clipboard.SetDataObject(dataObject, false);
            }
        }

        public static Bitmap GetImageFromClipboard2()
        {
            if (Clipboard.ContainsImage())
            {
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return null;
                if (dataObject.GetDataPresent(DataFormats.Dib))
                {
                    var dib = (MemoryStream)Clipboard.GetData(DataFormats.Dib);
                    var dibArray = dib.ToArray();
                    BITMAPINFOHEADER infoHeader = BinaryStructConverter.FromByteArray<BITMAPINFOHEADER>(dibArray);
                    var gch = GCHandle.Alloc(dibArray, GCHandleType.Pinned);
                    try
                    {
                        var infoPtr = gch.AddrOfPinnedObject();
                        var pixPtr = new IntPtr((long)infoPtr + 40);
                        MethodInfo mi = typeof(Bitmap).GetMethod("FromGDIplus", BindingFlags.Static | BindingFlags.NonPublic);
                        if (mi == null)
                            return null;
                        IntPtr pBmp = IntPtr.Zero;
                        int status = GdipCreateBitmapFromGdiDib(infoPtr, pixPtr, ref pBmp);
                        if ((status == 0) && (pBmp != IntPtr.Zero))
                            return (Bitmap)mi.Invoke(null, new object[] { pBmp });
                        else
                            return null;
                    }
                    finally
                    {
                        gch.Free();
                    }
                }
                return new Bitmap(System.Windows.Forms.Clipboard.GetImage());
            }
            return null;
        }
        public static Bitmap GetImageFromClipboard3()
        {
            if (Clipboard.ContainsImage())
            {
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return null;
                if (dataObject.GetDataPresent(DataFormats.Dib))
                {
                    var dibStream = (MemoryStream)Clipboard.GetData(DataFormats.Dib);
                    var dib = dibStream.ToArray();
                    BITMAPINFOHEADER infoHeader = BinaryStructConverter.FromByteArray<BITMAPINFOHEADER>(dib);
                    var height = infoHeader.biHeight;
                    var width = infoHeader.biWidth;
                    bool topDownScan = false;
                    if (height < 0)
                    {
                        height = Math.Abs(height);
                        topDownScan = true;
                    }

                    //var headerSize = 52;
                    var headerSize = infoHeader.biSize;

                    var gch = GCHandle.Alloc(dib, GCHandleType.Pinned);
                    Bitmap bmp = null;
                    try
                    {
                        var ptr = new IntPtr((long)gch.AddrOfPinnedObject() + headerSize);
                        bmp = new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb, ptr);
                        if (!topDownScan)
                            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
                        return new Bitmap(bmp);
                    }
                    finally
                    {
                        gch.Free();
                        bmp?.Dispose();
                    }
                }
                //return new Bitmap(System.Windows.Forms.Clipboard.GetImage());
            }
            return null;
        }

        private static BitmapSource GetImageFromClipboard()
        {
            if (Clipboard.ContainsImage())
            {
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return null;
                if (dataObject.GetDataPresent(DataFormats.Dib))
                {
                    var dib = (MemoryStream)Clipboard.GetData(DataFormats.Dib);
                    //return DibToBitmap(dib);
                    return null;
                    //var dib = ((MemoryStream)Clipboard.GetData(DataFormats.Dib)).ToArray();
                    //var width = BitConverter.ToInt32(dib, 4);
                    //var height = BitConverter.ToInt32(dib, 8);
                    //var bpp = BitConverter.ToInt16(dib, 14);
                    //if (bpp == 32)
                    //{
                    //    var gch = GCHandle.Alloc(dib, GCHandleType.Pinned);
                    //    Bitmap bmp = null;
                    //    try
                    //    {
                    //        var ptr = new IntPtr((long)gch.AddrOfPinnedObject() + 40);
                    //        bmp = new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                    //            ptr);
                    //        bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    //        return new Bitmap(bmp);
                    //    }
                    //    finally
                    //    {
                    //        gch.Free();
                    //        bmp?.Dispose();
                    //    }
                    //}
                }
                return Clipboard.GetImage();
            }
            return null;
        }

        public static BitmapSource GetImageFromFile()
        {
            if (Clipboard.ContainsFileDropList())
            {
                var collection = Clipboard.GetFileDropList();
                if (collection.Count == 1)
                {
                    var file = collection[0];
                    if (_knownImageExt.Any(k => file.EndsWith(k)))
                        return new BitmapImage(new Uri(file));
                }
            }
            return null;
        }
        private static Bitmap GetImageFromFileWF()
        {
            if (Clipboard.ContainsFileDropList())
            {
                var collection = Clipboard.GetFileDropList();
                if (collection.Count == 1)
                {
                    var file = collection[0];
                    if (_knownImageExt.Any(k => file.EndsWith(k)))
                        return new Bitmap(file);
                }
            }
            return null;
        }

        //private static BitmapSource DibToBitmap(MemoryStream ms)
        //{
        //    if (ms != null)
        //    {
        //        byte[] dibBuffer = new byte[ms.Length];
        //        ms.Read(dibBuffer, 0, dibBuffer.Length);

        //        BITMAPINFOHEADER infoHeader =
        //            BinaryStructConverter.FromByteArray<BITMAPINFOHEADER>(dibBuffer);

        //        int fileHeaderSize = Marshal.SizeOf(typeof(BITMAPFILEHEADER));
        //        int infoHeaderSize = infoHeader.biSize;
        //        int fileSize = fileHeaderSize + infoHeader.biSize + infoHeader.biSizeImage;

        //        BITMAPFILEHEADER fileHeader = new BITMAPFILEHEADER();
        //        fileHeader.bfType = BITMAPFILEHEADER.BM;
        //        fileHeader.bfSize = fileSize;
        //        fileHeader.bfReserved1 = 0;
        //        fileHeader.bfReserved2 = 0;
        //        fileHeader.bfOffBits = fileHeaderSize + infoHeaderSize + infoHeader.biClrUsed * 4;

        //        byte[] fileHeaderBytes = BinaryStructConverter.ToByteArray<BITMAPFILEHEADER>(fileHeader);

        //        MemoryStream msBitmap = new MemoryStream();
        //        msBitmap.Write(fileHeaderBytes, 0, fileHeaderSize);
        //        msBitmap.Write(dibBuffer, 0, dibBuffer.Length);
        //        msBitmap.Seek(0, SeekOrigin.Begin);

        //        return BitmapFrame.Create(msBitmap);
        //    }
        //    return null;
        //}
        public static Bitmap CreateBitmapFromDib(Stream dib)
        {
            // We create a new Bitmap File in memory.
            // This is the easiest way to convert a DIB to Bitmap.
            // No PInvoke needed.
            BinaryReader reader = new BinaryReader(dib);

            int headerSize = reader.ReadInt32();
            int pixelSize = (int)dib.Length - headerSize;
            int fileSize = 14 + headerSize + pixelSize;

            MemoryStream bmp = new MemoryStream(fileSize);
            BinaryWriter writer = new BinaryWriter(bmp);

            // 1. Write Bitmap File Header:			 
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write((int)0);
            writer.Write(14 + headerSize);

            // 2. Copy the DIB 
            dib.Position = 0;
            byte[] data = new byte[(int)dib.Length];
            dib.Read(data, 0, (int)dib.Length);
            writer.Write(data, 0, (int)data.Length);

            // 3. Create a new Bitmap from our new stream:
            bmp.Position = 0;
            return new Bitmap(bmp);
        }
        //public static Bitmap WithStream(IntPtr dibPtr)
        //{
        //    BITMAPFILEHEADER fh = new BITMAPFILEHEADER();
        //    Type bmiTyp = typeof(BITMAPINFOHEADER);
        //    BITMAPINFOHEADER bmi = (BITMAPINFOHEADER)Marshal.PtrToStructure(dibPtr, bmiTyp);
        //    if (bmi.biSizeImage == 0)
        //        bmi.biSizeImage = ((((bmi.biWidth * bmi.biBitCount) + 31) & ~31) >> 3) * Math.Abs(bmi.biHeight);
        //    if ((bmi.biClrUsed == 0) && (bmi.biBitCount < 16))
        //        bmi.biClrUsed = 1 << bmi.biBitCount;

        //    int fhSize = Marshal.SizeOf(typeof(BITMAPFILEHEADER));
        //    int dibSize = bmi.biSize + (bmi.biClrUsed * 4) + bmi.biSizeImage;  // info + rgb + pixels

        //    fh.bfType = BITMAPFILEHEADER.BM;
        //    fh.bfSize = fhSize + dibSize;                             // final file size
        //    fh.bfOffBits = fhSize + bmi.biSize + (bmi.biClrUsed * 4); // offset to pixels

        //    //byte[] data = new byte[fh.bfSize];                    // file-sized byte[] 
        //    //RawSerializeInto(fh, data);                     // serialize BITMAPFILEHEADER into byte[]
        //    byte[] data = BinaryStructConverter.ToByteArray<BITMAPFILEHEADER>(fh);
        //    Array.Resize(ref data, fh.bfSize);

        //    Marshal.Copy(dibPtr, data, fhSize, dibSize);        // mem-copy DIB into byte[]

        //    MemoryStream stream = new MemoryStream(data);       // file-sized stream
        //    Bitmap tmp = new Bitmap(stream);                    // 'tmp' is wired to stream (unfortunately)
        //    Bitmap result = new Bitmap(tmp);                    // 'result' is a copy (stand-alone)
        //    tmp.Dispose(); tmp = null;
        //    stream.Close(); stream = null; data = null;
        //    return result;
        //}

        [DllImport("gdiplus.dll", SetLastError = true)]
        static extern int GdipCreateBitmapFromGdiDib(IntPtr bminfo, IntPtr pixdat, ref IntPtr image);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1, ArraySubType = UnmanagedType.Struct)]
            public RGBQUAD[] bmiColors;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct BITMAPFILEHEADER
        {
            public static readonly short BM = 0x4d42; // BM

            public short bfType;
            public int bfSize;
            public short bfReserved1;
            public short bfReserved2;
            public int bfOffBits;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;

            public void Init()
            {
                biSize = (uint)Marshal.SizeOf(this);
            }
        }

        private static class BinaryStructConverter
        {
            public static T FromByteArray<T>(byte[] bytes) where T : struct
            {
                IntPtr ptr = IntPtr.Zero;
                try
                {
                    int size = Marshal.SizeOf(typeof(T));
                    ptr = Marshal.AllocHGlobal(size);
                    Marshal.Copy(bytes, 0, ptr, size);
                    object obj = Marshal.PtrToStructure(ptr, typeof(T));
                    return (T)obj;
                }
                finally
                {
                    if (ptr != IntPtr.Zero)
                        Marshal.FreeHGlobal(ptr);
                }
            }
            public static byte[] ToByteArray<T>(T obj) where T : struct
            {
                IntPtr ptr = IntPtr.Zero;
                try
                {
                    int size = Marshal.SizeOf(typeof(T));
                    ptr = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(obj, ptr, true);
                    byte[] bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, size);
                    return bytes;
                }
                finally
                {
                    if (ptr != IntPtr.Zero)
                        Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }
    public class BitmapFromDibStream : Stream
    {
        Stream dib = null;
        byte[] header = null;
        public BitmapFromDibStream(Stream dib)
        {
            this.dib = dib;
            makeHeader();
        }
        private void makeHeader()
        {
            BinaryReader reader = new BinaryReader(dib);

            int headerSize = reader.ReadInt32();
            int pixelSize = (int)dib.Length - headerSize;
            int fileSize = 14 + headerSize + pixelSize;

            MemoryStream bmp = new MemoryStream(14);
            BinaryWriter writer = new BinaryWriter(bmp);



            /* Get the palette size
                   * The Palette size is stored as an int32 at offset 32
                   * Actually stored as number of colours, so multiply by 4
                   */
            dib.Position = 32;
            int paletteSize = 4 * reader.ReadInt32();

            // Get the palette size from the bbp if none was specified
            if (paletteSize == 0)
            {
                /* Get the bits per pixel
                     * The bits per pixel is store as an int16 at offset 14
                     */
                dib.Position = 14;
                int bpp = reader.ReadInt16();

                // Only set the palette size if the bpp < 16
                if (bpp < 16)
                    paletteSize = 4 * (2 << (bpp - 1));
            }

            // 1. Write Bitmap File Header:			 
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write((int)0);
            writer.Write(14 + headerSize + paletteSize);
            header = bmp.GetBuffer();
            writer.Close();
            dib.Position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {

            int dibCount = count;
            int dibOffset = offset - 14;
            int result = 0;
            if (_position < 14)
            {
                int headerCount = Math.Min(count + (int)_position, 14);
                Array.Copy(header, _position, buffer, offset, headerCount);
                dibCount -= headerCount;
                _position += headerCount;
                result = headerCount;
            }
            if (_position > 14)
            {
                result += dib.Read(buffer, offset + result, dibCount);
                _position = 14 + dib.Position;
            }
            return (int)result;
        }
        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override void Flush()
        {

        }

        public override long Length
        {
            get { return 14 + dib.Length; }
        }

        private long _position = 0;
        public override long Position
        {
            get { return _position; }
            set
            {
                _position = value;
                if (_position > 14)
                    dib.Position = _position - 14;
            }
        }



        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new Exception("The method or operation is not implemented.");
        }

        public override void SetLength(long value)
        {
            throw new Exception("The method or operation is not implemented.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new Exception("The method or operation is not implemented.");
        }
    }
}
