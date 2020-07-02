using NAudio.CoreAudioApi;
using NAudio.Wave;
using Screeney.Audio;
using Screeney.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BasicFFEncode;

namespace Screeney
{
    static class Program
    {
        [STAThread]
        unsafe static void Main()
        {
            Clowd.Interop.USER32.SetProcessDPIAware();
            Console.WriteLine("Recording");

            var mm_devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            var device = mm_devices[0];

            string filename = "output.mp4";
            var looback = true;
            var stereo = false;
            var samplerate = 44100;
            var audioFormat = looback ? device.AudioClient.MixFormat : new WaveFormat(samplerate, 16, stereo ? 2 : 1);
            var region = new Rectangle(0, 0, 1920, 1080);
            var framerate = 30;
            var bitrate = Math.Min(region.Width * region.Height * framerate, 6000000);

            var silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            silence.Init(new SilenceProvider(audioFormat));
            silence.Play();

            var settings = new BasicEncoderSettings();
            settings.Video.Width = region.Width;
            settings.Video.Height = region.Height;
            settings.Video.Timebase = new Rational(1, framerate);
            settings.Video.Bitrate = Convert.ToUInt64(bitrate);
            settings.Audio.SampleRate = samplerate;
            settings.Audio.SampleFormat = BasicSampleFormat.FLTP;
            var enc = new BasicEncoder(filename, settings);
            var video = new ManagedGdiCaptureSource(region, 1000 / framerate);
            //WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice()
            var audio = new WasapiLoopbackCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared,
            };

            Stopwatch clock = new Stopwatch();
            int frameCount = 0;
            video.NewFrame += (s, e) =>
            {
                frameCount++;
                using (var vFrameGen = new BasicVideoFrame(1920, 1080, BasicPixelFormat.BGR24))
                using (var vFrameEnc = new BasicVideoFrame(settings.Video.Width, settings.Video.Height, BasicPixelFormat.YUV420P))
                using (var rescaler = new BasicRescaler(vFrameGen, vFrameEnc, BasicRescalerFlags.Lanczos))
                {
                    // copy bitmap data into ffmpeg frame
                    byte* buffer = vFrameGen.GetBuffer(0);
                    var bitmapData = e.Frame.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    uint length = Convert.ToUInt32(bitmapData.Stride * bitmapData.Height);
                    Interop.memcpy(new IntPtr(buffer), bitmapData.Scan0, new UIntPtr(length));
                    e.Frame.UnlockBits(bitmapData);

                    // convert bitmap format and encode
                    rescaler.RescaleFrame(vFrameGen, vFrameEnc);
                    enc.EncodeFrame(vFrameEnc, clock.ElapsedMilliseconds / framerate);
                }
            };
            audio.DataAvailable += (s, e) =>
            {
                //using (var aFrame = new BasicAudioFrame(e.BytesRecorded, settings.Audio.SampleFormat, settings.Audio.ChannelLayout))
                //{
                //    float* buffer = (float*)aFrame.GetBuffer(0);
                //    Marshal.Copy(e.Buffer, 0, new IntPtr(buffer), e.BytesRecorded);
                //    enc.EncodeFrame(aFrame, clock.ElapsedMilliseconds / samplerate);
                //}
            };
            clock.Start();
            video.Start();
            audio.StartRecording();
            int lastFrames = 1;
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"Ellapsed: {clock.Elapsed.TotalSeconds} // Avg: {frameCount / clock.Elapsed.TotalSeconds} // Current: {(frameCount - lastFrames)}");
                lastFrames = frameCount;
            }
            audio.StopRecording();
            audio.Dispose();
            video.Stop();
            clock.Stop();
            silence.Stop();
            silence.Dispose();
            enc.Dispose();

            Console.WriteLine("Done.");
            Console.WriteLine($"Final Ellapsted time: {clock.Elapsed.TotalSeconds} // Frames Captured: {frameCount}");
            Console.Read();
        }
    }
}
