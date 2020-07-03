using BasicFFEncode;
using Clowd.Interop;
using Clowd.Interop.Gdi32;
using RT.Util;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Screeney
{
    public enum Resolution : int
    {
        Actual = 0,
        LOW_480p = 480,
        HD_720p = 720,
        HD_1080p = 1080,
        HD_1440p = 1440,
    }
    public enum BitrateMultiplier : int
    {
        Low = 75,
        Medium = 100,
        High = 150,
    }
    public interface IRecorderSettings
    {
        Resolution OutputResolution { get; set; }
        BitrateMultiplier OutputQuality { get; set; }
        string OutputDirectory { get; set; }
        int TargetFramesPerSecond { get; set; }
        bool ShowCursor { get; set; }

    }

    public class ScreeneyRecorderSettings : IRecorderSettings
    {
        public Resolution OutputResolution { get; set; }
        public BitrateMultiplier OutputQuality { get; set; }
        public string OutputDirectory { get; set; }
        public int TargetFramesPerSecond { get; set; }
        public bool ShowCursor { get; set; }
    }

    public class ScreeneyRecorder
    {
        private readonly IRecorderSettings _settings;

        public ScreeneyRecorder(IRecorderSettings settings)
        {
            this._settings = settings;
        }

        public Recording OpenCapture(ScreenRect captureArea)
        {
            int targetWidth = captureArea.Width, targetHeight = captureArea.Height;

            int resolutionHeightLimit = captureArea.Height;
            if (_settings.OutputResolution > 0)
                resolutionHeightLimit = (int)_settings.OutputResolution;

            if (targetHeight > resolutionHeightLimit)
            {
                var aspectMultiplier = (double)captureArea.Width / captureArea.Height;
                targetHeight = resolutionHeightLimit;
                targetWidth = (int)Math.Round(targetHeight * aspectMultiplier, MidpointRounding.ToEven);
            }

            //Kush gauge: pixel count x motion factor x 0.07 ÷ 1000 = bit rate in kbps
            //ulong bitrate = (ulong)Math.Round(((decimal)targetHeight * targetWidth * _settings.TargetFramesPerSecond * 3) * (decimal)_settings.OutputQuality * 0.07m, 0);

            ulong bitrate = (ulong)Math.Round(((decimal)targetHeight * targetWidth * _settings.TargetFramesPerSecond) * ((decimal)_settings.OutputQuality / 1000m), 0);

            var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";
            filename = Path.Combine(Path.GetFullPath(_settings.OutputDirectory), filename);

            return new Recording(filename, captureArea, targetWidth, targetHeight, _settings.TargetFramesPerSecond, bitrate, true);
        }
    }

    public class Recording : IDisposable
    {
        public string FileName { get; }

        private BasicEncoderSettings settings;
        private ThreadedEncoder encoder;
        private Thread thread;
        private Thread thread2;
        private bool _stopRequested = false;
        private readonly ScreenRect captureArea;
        private int fps;
        private readonly bool cursor;
        private FpsCounter counter;

        internal Recording(string filename, ScreenRect captureArea, int targetWidth, int targetHeight, int fps, ulong bitrate, bool cursor)
        {
            settings = new BasicEncoderSettings();
            settings.Video.Width = targetWidth;
            settings.Video.Height = targetHeight;
            settings.Video.Timebase = new Rational(1, fps * 10);
            settings.Video.Bitrate = bitrate;
            settings.Video.PixelFormat = BasicPixelFormat.YUV420P;
            settings.Video.GopSize = 300;

            counter = new FpsCounter();
            encoder = new ThreadedEncoder(filename, settings, captureArea.Width, captureArea.Height, BasicPixelFormat.BGR24);

            thread = new Thread(captureThread);
            thread.IsBackground = true; // don't prevent program shutting down if the thread is still running

            thread2 = new Thread(reportingThread);
            thread2.IsBackground = true; // don't prevent program shutting down if the thread is still running

            this.FileName = filename;
            this.captureArea = captureArea;
            this.fps = fps;
            this.cursor = cursor;
        }

        public void Start()
        {
            thread.Start();
            thread2.Start();
        }

        public void Finish()
        {
            Dispose();
        }

        public void Dispose()
        {
            _stopRequested = true;
            thread.Join();
            thread2.Join();
            encoder.Dispose();
        }

        private void reportingThread()
        {
            while (true)
            {
                if (_stopRequested) break;
                Thread.Sleep(1000);
                Console.WriteLine($"FPS - {counter.AverageFps}");
            }
        }

        private void captureThread()
        {
            var sw = new System.Diagnostics.Stopwatch();
            var msPerFrame = 1000d / fps;
            int relaxMs = 4;
            int frameCount = 1;

            double timeLeft() { return msPerFrame - (sw.ElapsedTicks / 10000d); }
            void relax()
            {
                var time = timeLeft();
                if (time > relaxMs)
                {
                    var sleepTime = (int)Math.Max(0, Math.Floor(time - relaxMs));
                    if (sleepTime < relaxMs) return;
                    Thread.Sleep(sleepTime);
                    time = Math.Floor(timeLeft());
                    if (time < 0) relaxMs *= 2;
                    else if (time < 4) relaxMs++;
                    else if (time > 10 && relaxMs > 4) relaxMs--;
                }
            }

            VideoFrame frame = encoder.GetFreeFrame();

            while (true)
            {
                sw.Restart();

                if (_stopRequested) break;

                frame.Graphics.CopyFromScreen(captureArea.Left, captureArea.Top, 0, 0, frame.Bitmap.Size, CopyPixelOperation.SourceCopy);
                frame.Timestamp = DateTime.UtcNow;

                //if (cursor) drawCursor(frame.Graphics);
                if (cursor) DrawCursor(frame);

                if (_stopRequested) break;

                // don't encode the first second of frames - helps us determine optimal capture parameters for this system
                //if (frameCount >= fps)
                //{
                encoder.QueueFrameToEncode(frame);
                while ((frame = encoder.GetFreeFrame()) == null)
                    Thread.Yield();
                //}

                counter.CountFrame();

                // wait for frame time to elapse so we can achieve near-perfect fps etc.
                while (((sw.ElapsedTicks + 0) / 10000d) < msPerFrame)
                    relax();

                frameCount++;
            }

            encoder.Finish();
        }

        private DateTime _lastMouseClick = DateTime.Now.AddSeconds(-5);
        private Point _lastMouseClickPosition = new Point(0, 0);

        private unsafe void DrawCursor(VideoFrame frame)
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
            {
                var hicon = USER32.CopyIcon(cursorInfo.hCursor);
                ICONINFO iconInfo;
                int iconX, iconY;
                try
                {
                    if (USER32.GetIconInfo(hicon, out iconInfo))
                    {
                        iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot) - captureArea.Left;
                        iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot) - captureArea.Top;

                        if (iconX < 0 || iconX > captureArea.Width || iconY < 0 || iconY > captureArea.Height)
                        {
                            // mouse is out of bounds
                            return;
                        }

                        // draw click animation
                        if (Convert.ToBoolean(USER32.GetKeyState(VirtualKeyStates.VK_LBUTTON) & 0x8000 /*KEY_PRESSED*/) ||
                            Convert.ToBoolean(USER32.GetKeyState(VirtualKeyStates.VK_RBUTTON) & 0x8000 /*KEY_PRESSED*/))
                        {
                            _lastMouseClick = DateTime.Now;
                            _lastMouseClickPosition = new Point(cursorInfo.ptScreenPos.x - captureArea.Left, cursorInfo.ptScreenPos.y - captureArea.Top);
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
                                frame.Graphics.FillEllipse(semiTransBrush, rect);
                            }
                        }

                        // Is this a color cursor or a monochrome one?
                        if (iconInfo.hbmColor != IntPtr.Zero)
                        {
                            // color cursor
                            using (Icon curIcon = Icon.FromHandle(hicon))
                            using (Bitmap curBitmap = curIcon.ToBitmap())
                                frame.Graphics.DrawImage(curBitmap, iconX, iconY);
                        }
                        else
                        {
                            // According to the ICONINFO documentation, monochrome cursors (such as I-Beam cursor):
                            // The top half of the mask bitmap is the AND mask, and the bottom half of the mask bitmap is the XOR bitmap. 
                            // When Windows draws the I-Beam cursor, the top half of this bitmap is first drawn over the desktop with an AND raster operation. 
                            // The bottom half of the bitmap is then drawn over top with an XOR raster operation. 
                            // Onscreen, The cursor should will appear as the inverse of the content behind it.
                            // https://docs.microsoft.com/en-us/windows-hardware/drivers/display/drawing-monochrome-pointers
                            using (Bitmap maskBitmap = Bitmap.FromHbitmap(iconInfo.hbmMask))
                            {
                                var size = maskBitmap.Width;
                                byte bpp = 3;
                                var maskBits = maskBitmap.LockBits(new Rectangle(0, 0, maskBitmap.Width, maskBitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                                var destBits = frame.Bitmap.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                                byte* mscan0 = (byte*)maskBits.Scan0.ToPointer();
                                byte* dscan0 = (byte*)destBits.Scan0.ToPointer();

                                for (int y = 0; y < size; y++)
                                {
                                    for (int x = 0; x < size; x++)
                                    {
                                        int destX = iconX + x;
                                        int destY = iconY + y;
                                        if (destX >= frame.Bitmap.Width || destY >= frame.Bitmap.Height)
                                            continue;

                                        //data[0] = blue; data[1] = green; data[2] = red;

                                        byte* ANDptr = mscan0 + (y * maskBits.Stride) + (x * bpp);
                                        byte* XORptr = mscan0 + ((y + size) * maskBits.Stride) + (x * bpp);
                                        byte* DESTptr = dscan0 + (destY * destBits.Stride) + (destX * bpp);

                                        for (int p = 0; p < 3; p++)
                                        {
                                            DESTptr[p] &= ANDptr[p];
                                            DESTptr[p] ^= XORptr[p];
                                        }
                                    }
                                }

                                maskBitmap.UnlockBits(maskBits);
                                frame.Bitmap.UnlockBits(destBits);
                            }
                        }
                    }
                }
                finally
                {
                    USER32.DestroyIcon(hicon);
                }
            }
        }
    }
}
