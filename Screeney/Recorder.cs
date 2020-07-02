using BasicFFEncode;
using RT.Util;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
    }

    public class RecorderSettings : IRecorderSettings
    {
        public Resolution OutputResolution { get; set; }
        public BitrateMultiplier OutputQuality { get; set; }
        public string OutputDirectory { get; set; }
        public int TargetFramesPerSecond { get; set; }
    }

    public class Recorder
    {
        private readonly IRecorderSettings _settings;

        public Recorder(IRecorderSettings settings)
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
                var aspectMultiplier = captureArea.Height / captureArea.Width;
                targetHeight = resolutionHeightLimit;
                targetWidth = targetWidth * aspectMultiplier;
            }

            //Kush gauge: pixel count x motion factor x 0.07 ÷ 1000 = bit rate in kbps
            //ulong bitrate = (ulong)Math.Round(((decimal)targetHeight * targetWidth * _settings.TargetFramesPerSecond * 3) * (decimal)_settings.OutputQuality * 0.007m, 0);

            ulong bitrate = (ulong)Math.Round(((decimal)targetHeight * targetWidth * _settings.TargetFramesPerSecond) * ((decimal)_settings.OutputQuality / 1000m), 0);

            var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";
            filename = Path.Combine(Path.GetFullPath(_settings.OutputDirectory), filename);

            return new Recording(filename, captureArea, targetWidth, targetHeight, _settings.TargetFramesPerSecond, 8000 * 1000);
        }
    }

    public class Recording : IDisposable
    {
        BasicEncoderSettings settings;
        ThreadedEncoder encoder;
        Thread thread;
        Thread thread2;
        bool _stopRequested = false;
        string filename;
        private readonly ScreenRect captureArea;
        private int fps;
        FpsCounter counter;

        internal Recording(string filename, ScreenRect captureArea, int targetWidth, int targetHeight, int fps, ulong bitrate)
        {
            settings = new BasicEncoderSettings();
            settings.Video.Width = targetWidth;
            settings.Video.Height = targetHeight;
            settings.Video.Timebase = new Rational(1, fps * 10);
            settings.Video.Bitrate = bitrate;
            settings.Video.PixelFormat = BasicPixelFormat.YUV420P;

            counter = new FpsCounter();
            encoder = new ThreadedEncoder(filename, settings, captureArea.Width, captureArea.Height, BasicPixelFormat.BGR24);

            thread = new Thread(captureThread);
            thread.IsBackground = true; // don't prevent program shutting down if the thread is still running

            thread2 = new Thread(reportingThread);
            thread2.IsBackground = true; // don't prevent program shutting down if the thread is still running

            this.filename = filename;
            this.captureArea = captureArea;
            this.fps = fps;
        }

        public void Start()
        {
            thread.Start();
            thread2.Start();
        }

        public string Finish()
        {
            Dispose();
            return filename;
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
            var msPerFrame = 1000 / fps;

            while (true)
            {
                sw.Restart();
                var lastTimestamp = DateTime.UtcNow;

                if (_stopRequested) break;

                VideoFrame frame;
                while ((frame = encoder.GetFreeFrame()) == null)
                    Thread.Yield();

                if (_stopRequested) break;

                frame.Graphics.CopyFromScreen(captureArea.Left, captureArea.Top, 0, 0, frame.Bitmap.Size, CopyPixelOperation.SourceCopy);
                frame.Timestamp = DateTime.UtcNow;

                if ((frame.Timestamp - lastTimestamp).TotalMilliseconds > msPerFrame)
                    Console.WriteLine($"we're late.. by ${(frame.Timestamp - lastTimestamp).TotalMilliseconds - msPerFrame}ms");

                if (_stopRequested) break;

                encoder.QueueFrameToEncode(frame);
                counter.CountFrame();
                if (sw.ElapsedMilliseconds < msPerFrame)
                {
                    Thread.Sleep(msPerFrame - (int)sw.ElapsedMilliseconds - 1);
                }
            }

            encoder.Finish();
        }
    }
}
