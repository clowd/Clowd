using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// https://gist.github.com/Timwi/53fddd66c033e8ff13c1a652714cc0d3
namespace CursorGenerator
{
    public sealed class IconFile
    {
        private List<Bitmap> _bitmaps = new List<Bitmap>();
        private List<ushort> _hotspotsX = new List<ushort>();
        private List<ushort> _hotspotsY = new List<ushort>();

        public void Add(Bitmap bmp, ushort hotspotX = 0, ushort hotspotY = 0)
        {
            if (bmp == null)
                throw new ArgumentNullException("bmp");
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                throw new ArgumentException("PixelFormat must be Format32bppArgb.", "bmp");
            if (bmp.Width < 1 || bmp.Height < 1)
                throw new ArgumentException("Bitmap size cannot be zero.", "bmp");
            if (_bitmaps.Any(b => b.Width == bmp.Width && b.Height == bmp.Height))
                throw new ArgumentException("A bitmap with that size has already been added.", "bmp");
            if (_bitmaps.Count >= 255)
                throw new InvalidOperationException("You can’t have more than 255 bitmaps in an icon.");
            _bitmaps.Add(bmp);
            _hotspotsX.Add(hotspotX);
            _hotspotsY.Add(hotspotY);
        }

        public enum FileFormat : ushort
        {
            Ico = 1,
            Cur = 2
        }

        public void Save(string file, string pngCrush = null, FileFormat format = FileFormat.Ico)
        {
            if (file == null)
                throw new ArgumentNullException("file");
            if (_bitmaps.Count == 0)
                throw new InvalidOperationException("No bitmaps added.");

            using (var f = File.Open(file, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(f, Encoding.UTF8))
            {
                bw.Write((ushort)0);    // Reserved
                bw.Write((ushort)format);
                bw.Write((ushort)_bitmaps.Count);

                var offsets = new List<long>();
                var threads = new List<Thread>();
                var encodedBitmaps = new byte[_bitmaps.Count][];

                for (int i = 0; i < _bitmaps.Count; i++)
                {
                    var bmp = _bitmaps[i];

                    // Write info header
                    bw.Write((byte)(bmp.Width > 255 ? 0 : bmp.Width));         // Width
                    bw.Write((byte)(bmp.Height > 255 ? 0 : bmp.Height));     // Height
                    bw.Write((byte)0);     // "Colors"
                    bw.Write((byte)0);     // Reserved
                    bw.Write((ushort)(format == FileFormat.Cur ? _hotspotsX[i] : 0));     // "Planes"
                    bw.Write((ushort)(format == FileFormat.Cur ? _hotspotsY[i] : 32));    // BPP
                    offsets.Add(f.Position);
                    bw.Write((int)0);  // placeholder for length of image
                    bw.Write((int)0);  // placeholder for file offset of image

                    // Generate bitmap
                    var j = i;
                    var thread = new Thread(() =>
                    {
                        if (pngCrush == null)
                        {
                            using var mem = new MemoryStream();
                            bmp.Save(mem, ImageFormat.Png);
                            var bmpArr = mem.ToArray();
                            lock (encodedBitmaps)
                                encodedBitmaps[j] = bmpArr;
                        }
                        else
                        {
                            var tmpFile1 = Path.GetTempFileName();
                            var tmpFile2 = Path.GetTempFileName();
                            bmp.Save(tmpFile1, ImageFormat.Png);
                            var p = Process.Start(pngCrush, $@"-brute -reduce -rem alla ""{tmpFile1}"" ""{tmpFile2}""");
                            p.WaitForExit();
                            var bmpArr = File.ReadAllBytes(tmpFile2);
                            lock (encodedBitmaps)
                                encodedBitmaps[j] = bmpArr;
                            File.Delete(tmpFile1);
                            File.Delete(tmpFile2);
                        }
                    });
                    thread.Start();
                    threads.Add(thread);
                }

                foreach (var thread in threads)
                    thread.Join();

                for (int i = 0; i < _bitmaps.Count; i++)
                {
                    var offset = f.Position;
                    f.Seek(offsets[i], SeekOrigin.Begin);
                    bw.Write((int)encodedBitmaps[i].Length);
                    bw.Write((int)offset);
                    f.Seek(offset, SeekOrigin.Begin);
                    f.Write(encodedBitmaps[i]);
                }
            }
        }
    }
}
