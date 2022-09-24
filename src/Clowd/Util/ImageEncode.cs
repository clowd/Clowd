using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using RT.Util.ExtensionMethods;

namespace Clowd.Util
{
    public static class ImageEncode
    {
        // from wincodec.h
        // unfortunately, webp and heif do not currently support encoding
        static readonly Guid GUID_ContainerFormatBmp = new Guid(0x0af1d87e, 0xfcfe, 0x4188, 0xbd, 0xeb, 0xa7, 0x90, 0x64, 0x71, 0xcb, 0xe3);
        static readonly Guid GUID_ContainerFormatPng = new Guid(0x1b7cfaf4, 0x713f, 0x473c, 0xbb, 0xcd, 0x61, 0x37, 0x42, 0x5f, 0xae, 0xaf);
        static readonly Guid GUID_ContainerFormatIco = new Guid(0xa3a860c4, 0x338f, 0x4c17, 0x91, 0x9a, 0xfb, 0xa4, 0xb5, 0x62, 0x8f, 0x21);
        static readonly Guid GUID_ContainerFormatJpeg = new Guid(0x19e4a5aa, 0x5662, 0x4fc5, 0xa0, 0xc0, 0x17, 0x58, 0x02, 0x8e, 0x10, 0x57);
        static readonly Guid GUID_ContainerFormatTiff = new Guid(0x163bcc30, 0xe2e9, 0x4f0b, 0x96, 0x1d, 0xa3, 0xe9, 0xfd, 0xb7, 0x88, 0xa3);
        static readonly Guid GUID_ContainerFormatGif = new Guid(0x1f8a5601, 0x7d4d, 0x4cbd, 0x9c, 0x82, 0x1b, 0xc8, 0xd4, 0xee, 0xb9, 0xa5);
        static readonly Guid GUID_ContainerFormatWmp = new Guid(0x57a37caa, 0x367a, 0x4540, 0x91, 0x6b, 0xf1, 0x83, 0xc5, 0x09, 0x3a, 0x4b);
        static readonly Guid GUID_ContainerFormatDds = new Guid(0x9967cb95, 0x2e85, 0x4ac8, 0x8c, 0xa2, 0x83, 0xd7, 0xcc, 0xd4, 0x25, 0xc9);
        static readonly Guid GUID_ContainerFormatAdng = new Guid(0xf3ff6d0d, 0x38c0, 0x41c4, 0xb1, 0xfe, 0x1f, 0x38, 0x24, 0xf1, 0x7b, 0x84);
        static readonly Guid GUID_ContainerFormatHeif = new Guid(0xe1e62521, 0x6787, 0x405b, 0xa3, 0x39, 0x50, 0x07, 0x15, 0xb5, 0x76, 0x3f);
        static readonly Guid GUID_ContainerFormatWebp = new Guid(0xe094b0e2, 0x67f2, 0x45b3, 0xb0, 0xea, 0x11, 0x53, 0x37, 0xca, 0x7c, 0xf3);
        static readonly Guid GUID_ContainerFormatRaw = new Guid(0xfe99ce60, 0xf19c, 0x433c, 0xa3, 0xae, 0x00, 0xac, 0xef, 0xa9, 0xca, 0x21);

        static readonly (string ExtName, string ExtFilter, Guid WicEncoderGuid, ImageFormat GdiFormat)[] _encoders = new[]
        {
            ("PNG", "*.png", GUID_ContainerFormatPng, ImageFormat.Png),
            ("JPEG", "*.jpg; *.jpeg; *.jpe; *.jfif", GUID_ContainerFormatJpeg, ImageFormat.Jpeg),
            ("BMP", "*.bmp", GUID_ContainerFormatBmp, ImageFormat.Bmp),
            ("TIFF", "*.tiff; *.tif", GUID_ContainerFormatTiff, ImageFormat.Tiff),
            ("GIF", "*.gif", GUID_ContainerFormatGif, ImageFormat.Gif),
        };

        public static string GetFileDialogFilterString()
        {
            StringBuilder filter = new StringBuilder();
            foreach (var enc in _encoders)
            {
                // https://learn.microsoft.com/en-us/previous-versions/windows/silverlight/dotnet-windows-silverlight/dd459587(v=vs.95)
                filter.Append($"{enc.ExtName} ({enc.ExtFilter})|{enc.ExtFilter}|");
            }
            return filter.ToString().TrimEnd('|');
        }

        public static int GetFileDialogFilterIndexForExtension(string extension)
        {
            return Math.Max(_encoders.IndexOf(e => e.ExtFilter.ContainsIgnoreCase(extension)) + 1, 1);
        }

        public static BitmapEncoder GetEncoderForExtension(string extension)
        {
            var matches = _encoders.Where(e => e.ExtFilter.ContainsIgnoreCase(extension)).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("Can not create image encoder. Unsupported image extension: " + extension);

            BitmapEncoder encoder = BitmapEncoder.Create(matches[0].WicEncoderGuid);
            return encoder;
        }

        public static void WriteBitmapToFile(BitmapFrame frame, string filePath)
        {
            var ext = Path.GetExtension(filePath);
            var encoder = GetEncoderForExtension(Path.GetExtension(filePath));
            encoder.Frames.Add(frame);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        public static void WriteBitmapToStream(BitmapFrame frame, Stream stream, ImageFormat format)
        {
            var matches = _encoders.Where(e => e.GdiFormat == format).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("Can not create image encoder. Unsupported image format: " + format);

            BitmapEncoder encoder = BitmapEncoder.Create(matches[0].WicEncoderGuid);
            encoder.Frames.Add(frame);
            encoder.Save(stream);
        }
    }
}
