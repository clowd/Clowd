using BasicFFEncode;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Screeney
{
    //class ThreadedExample
    //{
    //    public static unsafe void Capture(string filename)
    //    {
    //        var settings = new BasicEncoderSettings();
    //        settings.Video.Width = 1920;
    //        settings.Video.Height = 1080;
    //        settings.Video.Timebase = new Rational(1, 2000);
    //        settings.Video.Bitrate = 2000000;
    //        settings.Video.GopSize = 10;
    //        using (var enc = new ThreadedEncoder(filename, settings, 1920, 1080, BasicPixelFormat.BGR24))
    //        {
    //            var start = DateTime.UtcNow;
    //            var fpsStart = DateTime.UtcNow;
    //            var prevFrameTimestamp = DateTime.UtcNow;
    //            var totalFrames = 0;

    //            while ((DateTime.UtcNow - start).TotalSeconds < 25)
    //            {
    //                VideoFrame frame;
    //                while ((frame = enc.GetFreeFrame()) == null)
    //                    Thread.Yield();

    //                if (totalFrames == 9)
    //                    fpsStart = DateTime.UtcNow;
    //                else if (totalFrames > 20)
    //                    Console.Title = $"{DateTime.Now:HH:mm:ss.fff} [{(totalFrames - 10) / (DateTime.UtcNow - fpsStart).TotalSeconds:0.0}]";

    //                frame.Graphics.CopyFromScreen(0, 0, 0, 0, frame.Bitmap.Size, CopyPixelOperation.SourceCopy);
    //                frame.Timestamp = DateTime.UtcNow;

    //                Console.Write($"{1 / (frame.Timestamp - prevFrameTimestamp).TotalSeconds:0.0}   ");
    //                prevFrameTimestamp = frame.Timestamp;
    //                totalFrames++;

    //                enc.QueueFrameToEncode(frame);
    //            }
    //            enc.Finish();
    //        }
    //    }
    //}

    class ThreadedEncoder : IDisposable
    {
        private BasicEncoder _encoder;
        private ConcurrentQueue<VideoFrame> _framesFree = new ConcurrentQueue<VideoFrame>();
        private ConcurrentQueue<VideoFrame> _framesToEncode = new ConcurrentQueue<VideoFrame>();
        private BasicVideoFrame _frameRescaled;
        private BasicRescaler _rescaler;
        private BasicEncoderSettings _settings;
        private AutoResetEvent _sync = new AutoResetEvent(false);
        private Thread _thread;

        public ThreadedEncoder(string filename, BasicEncoderSettings settings, int sourceWidth, int sourceHeight, BasicPixelFormat sourcePixelFormat)
        {
            _settings = settings;
            _encoder = new BasicEncoder(filename, settings);
            _frameRescaled = new BasicVideoFrame(settings.Video.Width, settings.Video.Height, settings.Video.PixelFormat);
            for (int i = 0; i < 10; i++)
                _framesFree.Enqueue(new VideoFrame(sourceWidth, sourceHeight, sourcePixelFormat));
            _rescaler = new BasicRescaler(sourceWidth, sourceHeight, sourcePixelFormat, settings.Video.Width, settings.Video.Height, settings.Video.PixelFormat, BasicRescalerFlags.FastBilinear);

            _thread = new Thread(encodingThread);
            _thread.IsBackground = true; // don't prevent program shutting down if the thread is still running
            _thread.Start();
        }

        public VideoFrame GetFreeFrame()
        {
            VideoFrame frame;
            if (_framesFree.TryDequeue(out frame))
                return frame;
            return null;
        }

        public void QueueFrameToEncode(VideoFrame frame)
        {
            _framesToEncode.Enqueue(frame);
            _sync.Set();
        }

        public void Finish()
        {
            _framesToEncode.Enqueue(null);
            _sync.Set();
            _thread.Join();
        }

        private void encodingThread()
        {
            var firstTimestamp = default(DateTime);
            while (true)
            {
                _sync.WaitOne();
                VideoFrame frame;
                if (!_framesToEncode.TryDequeue(out frame))
                    continue;
                if (frame == null)
                    break;
                var timestamp = frame.Timestamp;
                _rescaler.RescaleFrame(frame, _frameRescaled);
                _framesFree.Enqueue(frame);
                if (firstTimestamp == default(DateTime))
                    firstTimestamp = timestamp;

                long presentation = (long)Math.Round((timestamp - firstTimestamp).TotalSeconds * _settings.Video.Timebase.Den / _settings.Video.Timebase.Num);

                _encoder.EncodeFrame(_frameRescaled, presentation);
            }
            _encoder.Dispose();
            _encoder = null;
        }

        public void Dispose()
        {
            if (_framesFree != null)
                foreach (var frame in _framesFree)
                    frame?.Dispose();
            _framesFree = null;
            if (_framesToEncode != null)
                foreach (var frame in _framesToEncode)
                    frame?.Dispose();
            _framesToEncode = null;
            _frameRescaled?.Dispose();
            _frameRescaled = null;
            _rescaler?.Dispose();
            _rescaler = null;
        }
    }

    class VideoFrame : BasicVideoFrame
    {
        public DateTime Timestamp;
        public Bitmap Bitmap;
        public Graphics Graphics;
        public unsafe VideoFrame(int width, int height, BasicPixelFormat pixelFormat)
            : base(width, height, pixelFormat)
        {
            Bitmap = new Bitmap(width, height, GetStride(0), System.Drawing.Imaging.PixelFormat.Format24bppRgb, (IntPtr)GetBuffer(0));
            Graphics = Graphics.FromImage(Bitmap);
        }
    }
}
