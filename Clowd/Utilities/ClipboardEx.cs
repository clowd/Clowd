using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
            return Clipboard.GetImage() ?? GetImageFromFile();
        }

        public static bool SetImage(Bitmap bmp)
        {
            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            IDataObject dataObject = new DataObject();
            dataObject.SetData(DataFormats.Bitmap, bmp, true);
            //dataObject.SetData("PNG", pngBytes, true);
            for (int retry = 0; retry < 10; retry++)
            {
                try
                { Clipboard.SetDataObject(dataObject, true); return true; }
                catch { Thread.Sleep(100); }
            }
            return false;
        }

        public static bool SetImage(BitmapSource bmp)
        {
            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            IDataObject dataObject = new DataObject();
            dataObject.SetData(DataFormats.Bitmap, bmp, true);
            //dataObject.SetData("PNG", pngBytes, true);
            for (int retry = 0; retry < 10; retry++)
            {
                try { Clipboard.SetDataObject(dataObject, true); return true; }
                catch { Thread.Sleep(100); }
            }
            return false;
        }

        public static void AddImageToData(IDataObject dataObject, BitmapSource bmp)
        {
            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            dataObject.SetData(DataFormats.Bitmap, bmp, true);
            //dataObject.SetData("PNG", pngBytes, true);
        }

        private static BitmapSource GetImageFromFile()
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
    }
}
