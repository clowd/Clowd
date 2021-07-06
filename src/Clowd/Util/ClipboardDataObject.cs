using Clowd.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Clowd.Util
{
    public class ClipboardDataObject
    {
        private static string[] _knownImageExt = new[] {
            ".png", ".jpg", ".jpeg",".jpe", ".bmp",
            ".gif", ".tif", ".tiff", ".ico" };
        private readonly IDataObject _data;
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private const string DATAFORMAT_PNG = "PNG";
        private const string DATAFORMAT_V5BITMAP = "Format17";

        public ClipboardDataObject()
        {
            _data = new DataObject();
        }

        private ClipboardDataObject(IDataObject data)
        {
            this._data = data;
        }

        public bool ContainsImage()
        {
            var hasImgFormat = ContainsDataFormat(
                DATAFORMAT_PNG,
                DATAFORMAT_V5BITMAP,
                DataFormats.Bitmap,
                DataFormats.Dib,
                typeof(BitmapSource).FullName
            );

            if (hasImgFormat)
                return true;

            // check if there is an image in the file drop list
            var fileDropList = GetFileDropList();
            if (fileDropList != null && fileDropList.Length == 1)
            {
                var filePath = fileDropList[0];
                if (File.Exists(filePath) && _knownImageExt.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ContainsText()
        {
            return ContainsDataFormat(DataFormats.UnicodeText, DataFormats.Text);
        }

        public bool ContainsFileDropList()
        {
            return ContainsDataFormat(DataFormats.FileDrop);
        }

        public bool ContainsDataFormat(params string[] check)
        {
            var formats = _data.GetFormats(false);
            return formats.Any(f => check.Contains(f));
        }

        public void SetImage(BitmapSource bitmapSource)
        {
            // PNG
            var ms = new MemoryStream();
            bitmapSource.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            SetDataFormat(DATAFORMAT_PNG, ms);

            // BITMAPSOURCE / BITMAP
            SetDataFormat(typeof(BitmapSource), bitmapSource);
            SetDataFormat(DataFormats.Bitmap, bitmapSource, true); // auto convert to HBITMAP

            // TODO
            // DIB and Format17
            // https://csharp.hotexamples.com/site/file?hash=0x87ea649c2fa030d7197ab0d0185dff7d256bfda77c9534b5d66fb5776430bea0&fullName=WpfLibrary/Thinknet.ControlLibrary/CursorGenerator.cs&project=jprofi/MSProjects
        }

        public void SetText(string text)
        {
            SetDataFormat(DataFormats.UnicodeText, text);
        }

        public void SetFileDropList(string[] files)
        {
            SetDataFormat(DataFormats.FileDrop, files, true);
        }

        public void SetDataFormat(string format, object obj, bool autoConvert = false)
        {
            _data.SetData(format, obj, autoConvert);
        }

        public void SetDataFormat(Type format, object obj)
        {
            _data.SetData(format, obj);
        }

        public BitmapSource GetImage()
        {
            // get a list of data formats on the clipboard, not including any formats that DataObject can auto-convert.
            // we will try a few formats that are capable of preserving transparency, in order of reliability.
            var formats = _data.GetFormats(false);

            if (formats.Contains(DATAFORMAT_PNG))
            {
                var pngStream = GetDataFormat<Stream>(DATAFORMAT_PNG);
                if (pngStream != null)
                {
                    var decoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
                    return decoder.Frames[0];
                }
            }

            if (formats.Contains(typeof(BitmapSource).FullName))
            {
                var bitmapSource = GetDataFormat<BitmapSource>(typeof(BitmapSource));
                if (bitmapSource != null)
                {
                    return bitmapSource;
                }
            }

            if (formats.Contains(DATAFORMAT_V5BITMAP))
            {
                var dib5stream = GetDataFormat<MemoryStream>(DATAFORMAT_V5BITMAP);
                var bytes = dib5stream.ToArray();
                var bitmapSource = CF_DIBV5ToBitmap(bytes);
                if (bitmapSource != null)
                {
                    return bitmapSource;
                }
            }

            if (formats.Contains(DataFormats.Dib))
            {
                var dibStream = GetDataFormat<MemoryStream>(DataFormats.Dib);
                if (dibStream != null)
                {
                    var bitmapSource = ImageFromClipboardDib(dibStream.ToArray());
                    if (bitmapSource != null)
                    {
                        return bitmapSource;
                    }
                }
            }

            if (formats.Contains(DataFormats.Bitmap))
            {
                // We try this format last as it's very likely to give us an empty image due to compatibility issues with other applications
                // WPF will automatically convert HBITMAP to a BitmapSource when requesting DataFormats.Bitmap
                // https://referencesource.microsoft.com/#PresentationCore/Core/CSharp/System/Windows/DataObject.cs,3324
                var bitmapSource = GetDataFormat<BitmapSource>(DataFormats.Bitmap);
                if (bitmapSource != null)
                {
                    return bitmapSource;
                }
            }

            // if - we have failed to decode a bitmap so far 
            //    - there is a single file in the file drop list
            //    - the file in the file drop list is an image (file name ends with image extension)
            //    - the file exists on disk
            var fileDropList = GetFileDropList();
            if (fileDropList != null && fileDropList.Length == 1)
            {
                var filePath = fileDropList[0];
                if (File.Exists(filePath) && _knownImageExt.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return new BitmapImage(new Uri(filePath));
                }
            }

            return null;
        }

        public string GetText()
        {
            return GetDataFormat<string>(DataFormats.UnicodeText);
        }

        public string[] GetFileDropList()
        {
            return GetDataFormat<string[]>(DataFormats.FileDrop);
        }

        public T GetDataFormat<T>(string format) where T : class
        {
            return GetDataFormat(format) as T;
        }

        public T GetDataFormat<T>(Type format) where T : class
        {
            return GetDataFormat(format) as T;
        }

        public object GetDataFormat(string format)
        {
            return _data.GetData(format);
        }

        public object GetDataFormat(Type format)
        {
            return _data.GetData(format);
        }

        public Task<bool> SetClipboardData(FrameworkElement parent = null)
        {
            return DoClipboardTask(() =>
            {
                Clipboard.SetDataObject(_data, true);
                return true;
            }, parent);
        }

        public static async Task<ClipboardDataObject> GetClipboardData(FrameworkElement parent = null)
        {
            var data = await DoClipboardTask(Clipboard.GetDataObject, parent);
            if (data == null)
                return null;

            return new ClipboardDataObject(data);
        }

        private static async Task<T> DoClipboardTask<T>(Func<T> task, FrameworkElement owner)
        {
            // Clipboard operations are already re-tried internally, we don't want to add much to this 
            Exception error = null;
            Process p = null;

            await _semaphore.WaitAsync();
            try
            {
                try
                {
                    return task();
                }
                catch (Exception ex)
                {
                    error = ex;
                    try
                    {
                        var proc = GetProcessHoldingClipboard();
                        if (proc != null)
                            p = proc;
                    }
                    catch { }
                }
            }
            finally
            {
                _semaphore.Release();
            }

            string footer = null;

            if (p != null)
                footer = $"Process '{p.ProcessName}' (pid {p.Id}) is locking the clipboard.";

            bool shouldRetry = await NiceDialog.ShowPromptAsync(
                owner,
                NiceDialogIcon.Error,
                error.Message,
                "Unable to access the clipboard",
                "Retry?",
                "Cancel",
                NiceDialogIcon.Information,
                footer);

            return shouldRetry
                ? await DoClipboardTask(task, owner)
                : default(T);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static Process GetProcessHoldingClipboard()
        {
            IntPtr hwnd = GetOpenClipboardWindow();

            if (hwnd == IntPtr.Zero)
                return null;

            uint processId;
            uint threadId = GetWindowThreadProcessId(hwnd, out processId);

            return Process.GetProcessById((int)processId);
        }

        // https://stackoverflow.com/a/14335591/184746
        private static BitmapSource CF_DIBV5ToBitmap(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            BitmapSource bsource = null;
            Bitmap bitmap = null;
            try
            {
                var bmi = (BITMAPV5HEADER)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(BITMAPV5HEADER));

                bitmap = new Bitmap(
                    (int)bmi.bV5Width,
                    (int)bmi.bV5Height,
                    -(int)(bmi.bV5SizeImage / bmi.bV5Height),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                    new IntPtr(handle.AddrOfPinnedObject().ToInt32() + bmi.bV5Size + (bmi.bV5Height - 1) * (int)(bmi.bV5SizeImage / bmi.bV5Height)));

                bsource = bitmap.ToBitmapSource();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (bitmap != null)
                    bitmap.Dispose();
                handle.Free();
            }
            return bsource;
        }

        // collected from various answers by Nyerguds
        // https://stackoverflow.com/a/46424800/184746
        // https://stackoverflow.com/a/43967594/184746
        private static BitmapSource ImageFromClipboardDib(Byte[] dibBytes)
        {
            UInt32 ReadIntFromByteArray(Byte[] data, Int32 startIndex, Int32 bytes, Boolean littleEndian)
            {
                Int32 lastByte = bytes - 1;
                if (data.Length < startIndex + bytes)
                    throw new ArgumentOutOfRangeException("startIndex", "Data array is too small to read a " + bytes + "-byte value at offset " + startIndex + ".");
                UInt32 value = 0;
                for (Int32 index = 0; index < bytes; index++)
                {
                    Int32 offs = startIndex + (littleEndian ? index : lastByte - index);
                    value += (UInt32)(data[offs] << (8 * index));
                }
                return value;
            }

            Bitmap BuildImage(Byte[] sourceData, Int32 width, Int32 height, Int32 stride, PixelFormat pixelFormat, Color[] palette, Color? defaultColor)
            {
                Bitmap newImage = new Bitmap(width, height, pixelFormat);
                BitmapData targetData = newImage.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, newImage.PixelFormat);
                Int32 newDataWidth = ((Image.GetPixelFormatSize(pixelFormat) * width) + 7) / 8;
                // Compensate for possible negative stride on BMP format.
                Boolean isFlipped = stride < 0;
                stride = Math.Abs(stride);
                // Cache these to avoid unnecessary getter calls.
                Int32 targetStride = targetData.Stride;
                Int64 scan0 = targetData.Scan0.ToInt64();
                for (Int32 y = 0; y < height; y++)
                    Marshal.Copy(sourceData, y * stride, new IntPtr(scan0 + y * targetStride), newDataWidth);
                newImage.UnlockBits(targetData);
                // Fix negative stride on BMP format.
                if (isFlipped)
                    newImage.RotateFlip(RotateFlipType.Rotate180FlipX);
                // For indexed images, set the palette.
                if ((pixelFormat & PixelFormat.Indexed) != 0 && palette != null)
                {
                    ColorPalette pal = newImage.Palette;
                    for (Int32 i = 0; i < pal.Entries.Length; i++)
                    {
                        if (i < palette.Length)
                            pal.Entries[i] = palette[i];
                        else if (defaultColor.HasValue)
                            pal.Entries[i] = defaultColor.Value;
                        else
                            break;
                    }
                    newImage.Palette = pal;
                }
                return newImage;
            }


            if (dibBytes == null || dibBytes.Length < 4)
                return null;

            try
            {
                Int32 headerSize = (Int32)ReadIntFromByteArray(dibBytes, 0, 4, true);
                // Only supporting 40-byte DIB from clipboard
                if (headerSize != 40)
                    return null;
                Byte[] header = new Byte[40];
                Array.Copy(dibBytes, header, 40);
                Int32 imageIndex = headerSize;
                Int32 width = (Int32)ReadIntFromByteArray(header, 0x04, 4, true);
                Int32 height = (Int32)ReadIntFromByteArray(header, 0x08, 4, true);
                Int16 planes = (Int16)ReadIntFromByteArray(header, 0x0C, 2, true);
                Int16 bitCount = (Int16)ReadIntFromByteArray(header, 0x0E, 2, true);
                //Compression: 0 = RGB; 3 = BITFIELDS.
                Int32 compression = (Int32)ReadIntFromByteArray(header, 0x10, 4, true);
                // Not dealing with non-standard formats.
                if (planes != 1 || (compression != 0 && compression != 3))
                    return null;
                PixelFormat fmt;
                switch (bitCount)
                {
                    case 32:
                        fmt = PixelFormat.Format32bppRgb;
                        break;
                    case 24:
                        fmt = PixelFormat.Format24bppRgb;
                        break;
                    case 16:
                        fmt = PixelFormat.Format16bppRgb555;
                        break;
                    default:
                        return null;
                }
                if (compression == 3)
                    imageIndex += 12;
                if (dibBytes.Length < imageIndex)
                    return null;
                Byte[] image = new Byte[dibBytes.Length - imageIndex];
                Array.Copy(dibBytes, imageIndex, image, 0, image.Length);
                // Classic stride: fit within blocks of 4 bytes.
                Int32 stride = (((((bitCount * width) + 7) / 8) + 3) / 4) * 4;
                if (compression == 3)
                {
                    UInt32 redMask = ReadIntFromByteArray(dibBytes, headerSize + 0, 4, true);
                    UInt32 greenMask = ReadIntFromByteArray(dibBytes, headerSize + 4, 4, true);
                    UInt32 blueMask = ReadIntFromByteArray(dibBytes, headerSize + 8, 4, true);
                    // Fix for the undocumented use of 32bppARGB disguised as BITFIELDS. Despite lacking an alpha bit field,
                    // the alpha bytes are still filled in, without any header indication of alpha usage.
                    // Pure 32-bit RGB: check if a switch to ARGB can be made by checking for non-zero alpha.
                    // Admitted, this may give a mess if the alpha bits simply aren't cleared, but why the hell wouldn't it use 24bpp then?
                    if (bitCount == 32 && redMask == 0xFF0000 && greenMask == 0x00FF00 && blueMask == 0x0000FF)
                    {
                        // Stride is always a multiple of 4; no need to take it into account for 32bpp.
                        for (Int32 pix = 3; pix < image.Length; pix += 4)
                        {
                            // 0 can mean transparent, but can also mean the alpha isn't filled in, so only check for non-zero alpha,
                            // which would indicate there is actual data in the alpha bytes.
                            if (image[pix] == 0)
                                continue;
                            fmt = PixelFormat.Format32bppPArgb;
                            break;
                        }
                    }
                    else
                        // Could be supported with a system that parses the colour masks,
                        // but I don't think the clipboard ever uses these anyway.
                        return null;
                }
                using (Bitmap bitmap = BuildImage(image, width, height, stride, fmt, null, null))
                {
                    // This is bmp; reverse image lines.
                    bitmap.RotateFlip(RotateFlipType.Rotate180FlipX);
                    var bitmapSource = bitmap.ToBitmapSource();
                    return bitmapSource;
                }
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPV5HEADER
        {
            public uint bV5Size;
            public int bV5Width;
            public int bV5Height;
            public UInt16 bV5Planes;
            public UInt16 bV5BitCount;
            public uint bV5Compression;
            public uint bV5SizeImage;
            public int bV5XPelsPerMeter;
            public int bV5YPelsPerMeter;
            public UInt16 bV5ClrUsed;
            public UInt16 bV5ClrImportant;
            public UInt16 bV5RedMask;
            public UInt16 bV5GreenMask;
            public UInt16 bV5BlueMask;
            public UInt16 bV5AlphaMask;
            public UInt16 bV5CSType;
            public IntPtr bV5Endpoints;
            public UInt16 bV5GammaRed;
            public UInt16 bV5GammaGreen;
            public UInt16 bV5GammaBlue;
            public UInt16 bV5Intent;
            public UInt16 bV5ProfileData;
            public UInt16 bV5ProfileSize;
            public UInt16 bV5Reserved;
        }
    }
}
