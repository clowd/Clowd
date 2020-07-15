using Clowd.Interop;
using Clowd.Interop.Gdi32;
using NReco.VideoConverter;
using PropertyChanged;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    public static class ScreenUtil
    {
        public static Bitmap Capture(ScreenRect? bounds = null, bool captureCursor = false)
        {
            Rectangle rect = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();

            var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.X, rect.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                if (captureCursor)
                {
                    DrawCursor(g, new System.Drawing.Point(rect.X, rect.Y));
                }
            }
            return bitmap;
        }

        public static Bitmap CaptureActiveWindow(bool captureCursor = false)
        {
            var foreground = USER32.GetForegroundWindow();
            var bounds = USER32EX.GetTrueWindowBounds(foreground);
            return Capture(ScreenRect.FromSystem(bounds), captureCursor);
        }

        private static void DrawCursor(Graphics g, Point origin)
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
            {
                var hicon = USER32.CopyIcon(cursorInfo.hCursor);
                ICONINFO iconInfo;
                int iconX, iconY;
                if (USER32.GetIconInfo(hicon, out iconInfo))
                {
                    iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot) - origin.X;
                    iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot) - origin.Y;

                    // Is this a color cursor or a monochrome one?
                    if (iconInfo.hbmColor != IntPtr.Zero)
                    {
                        using (Icon curIcon = Icon.FromHandle(hicon))
                        using (Bitmap curBitmap = curIcon.ToBitmap())
                            g.DrawImage(curBitmap, iconX, iconY);
                    }
                    else
                    {
                        //According to the ICONINFO documentation, monochrome cursors (such as I-Beam cursor):
                        //The top half of the mask bitmap is the AND mask, and the bottom half of the mask bitmap is the XOR bitmap. 
                        //When Windows draws the I-Beam cursor, the top half of this bitmap is first drawn over the desktop with an AND raster operation. 
                        //The bottom half of the bitmap is then drawn over top with an XOR raster operation. 
                        //Onscreen, The cursor should will appear as the inverse of the content behind it.
#warning This cursor should appear as the inverse of the content behind, but is currently being rendered completely white regardless of background.
                        using (Bitmap maskBitmap = Bitmap.FromHbitmap(iconInfo.hbmMask))
                        {
                            Graphics desktopGraphics = Graphics.FromHwnd(USER32.GetDesktopWindow());
                            IntPtr desktopHdc = desktopGraphics.GetHdc();
                            IntPtr maskHdc = GDI32.CreateCompatibleDC(desktopHdc);
                            IntPtr oldPtr = GDI32.SelectObject(maskHdc, maskBitmap.GetHbitmap());

                            var resultHdc = g.GetHdc();
                            var size = maskBitmap.Width;
                            GDI32.BitBlt(resultHdc, iconX, iconY, size, size, maskHdc, 0, 0, TernaryRasterOperations.SRCAND);
                            GDI32.BitBlt(resultHdc, iconX, iconY, size, size, maskHdc, 0, size, TernaryRasterOperations.SRCINVERT);
                            g.ReleaseHdc(resultHdc);

                            IntPtr newPtr = GDI32.SelectObject(maskHdc, oldPtr);
                            GDI32.DeleteObject(newPtr);
                            GDI32.DeleteDC(maskHdc);

                            desktopGraphics.ReleaseHdc(desktopHdc);
                        }
                    }
                    USER32.DestroyIcon(hicon);
                }
            }
        }

        public static LiveScreenRecording PrepareVideoRecording(ScreenRect? bounds = null)
        {
            Rectangle rect = (bounds ?? ScreenTools.VirtualScreen.Bounds).ToSystem();
            return new LiveScreenRecording(rect);
        }

        public class LiveScreenRecording
        {
            public string FileName { get; private set; }

            private readonly Rectangle bounds;
            private readonly VideoSettings settings;
            private readonly FFMpegConverter ffmpeg;
            private Task runner;
            private StringBuilder log = new StringBuilder();

            public LiveScreenRecording(Rectangle bounds)
            {
                this.bounds = bounds;
                settings = App.Current.Settings.VideoSettings;
                ffmpeg = new FFMpegConverter();
                ffmpeg.LogReceived += (s, e) =>
                {
                    Console.WriteLine(e.Data);
                    log.AppendLine(e.Data);
                };

                //ffmpeg 
                //-f gdigrab -i desktop -framerate 30 -offset_x 10 -offset_y 20 -video_size 640x480 -show_region 1
            }

            public async Task Start()
            {
                var args = String.Join(" ", cli_VideoSource(), cli_FilterGraph(), cli_VideoCodecAndOutput());

                // run in a background thread
                runner = Task.Factory.StartNew(() => ffmpeg.Invoke(args), TaskCreationOptions.LongRunning);

                // wait a bit and then check if we failed to start recording, lets throw an exception
                await Task.Delay(1000);
                if (runner.Exception != null)
                    throw runner.Exception;
            }

            public async Task Stop()
            {
                ffmpeg.Stop();
                await runner;
            }

            private string cli_FilterGraph()
            {
                List<string> filters = new List<string>();
                if (settings.MaxResolution != MaxResolution.Uncapped && bounds.Height > (int)settings.MaxResolution)
                {
                    // keep aspect ratio but limit height to specified max resolution
                    filters.Add("scale=-1:" + (int)settings.MaxResolution);
                }

                //if (false)
                //{
                //    filters.Add("mpdecimate");
                //    filters.Add("framerate=" + settings.TargetFramesPerSecond);
                //}

                if (filters.Any())
                {
                    return $"-filter:v \"{String.Join(",", filters)}\"";
                }
                else return null;
            }

            private string cli_VideoSource()
            {
                //return $"-f gdigrab -framerate {settings.TargetFramesPerSecond} -offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -show_region 1 -draw_mouse {(settings.ShowCursor ? "1" : "0")} -i desktop";
                return $"-f gdigrab -offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -show_region 1 -draw_mouse {(settings.ShowCursor ? "1" : "0")} -i desktop";
            }

            private string cli_VideoCodecAndOutput()
            {
                string codec = "";
                string extension = "";
                switch (settings.VideoCodec)
                {
                    case CaptureVideoCodec.H264:
                        codec = codec_H264();
                        extension = "mp4";
                        break;
                    default:
                        throw new NotImplementedException();
                }

                var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + extension;
                filename = Path.Combine(Path.GetFullPath(settings.OutputDirectory), filename);
                this.FileName = filename;

                //ulong bitrate = (ulong)Math.Round(((decimal)bounds.Width * bounds.Height * settings.TargetFramesPerSecond) * ((decimal)settings.OutputQuality / 1000m), 0);
                return $"{codec} -n \"{filename}\"";
            }

            private string codec_H264()
            {
                string codec = "-codec:v libx264";
                if (settings.HardwareAcceleration)
                {
                    ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DisplayConfiguration");

                    string graphicsCard = string.Empty;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        foreach (PropertyData property in mo.Properties)
                        {
                            if (property.Name == "Description")
                            {
                                graphicsCard = property.Value.ToString();
                            }
                        }
                    }

                    if (graphicsCard.ToUpper().Contains("NVIDIA"))
                    {
                        codec = "-codec:v h264_nvenc";
                    }

                    if (graphicsCard.ToUpper().Contains("AMD"))
                    {
                        codec = "-codec:v h264_amf";
                    }
                }

                return codec + " -preset veryfast -tune animation";
            }
        }
    }
}
