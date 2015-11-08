using AForge.Video;
using Accord.Video.FFMPEG;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NReco.VideoConverter;
using Screeney.Audio;
using Screeney.Video;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Screeney
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ScreeneyRecorder rec = new ScreeneyRecorder(new Rectangle(0, 0, 1920, 1080));

            var a_devices = rec.GetAudioSources();
            var a_loopback = a_devices.Where(dev => dev.IsLoopback).First();
            var v_devices = rec.GetVideoSources();
            var v_gdi = v_devices.First();

            rec.InitAudio(a_loopback);
            rec.InitVideo(v_gdi, 30);

            rec.RecordingUpdate += Rec_RecordingUpdate;
            rec.StartRecording();
            Thread.Sleep(30000);
            rec.StopRecording();
            Console.WriteLine("done recording");

            string f = @"C:\Users\Caelan\Desktop\test\test123.mp4";
            if (File.Exists(f))
                File.Delete(f);

            //var encoder = rec.StartEncoding(f);
            //encoder.Wait();
            Console.WriteLine("done encoding");

            rec.Dispose();
            Console.Read();
            //var dir = @"C:\Users\Caelan\Desktop\test";
            //var cmd = $"-i \"{Path.Combine(dir, "test2.mp3")}\" -i \"{Path.Combine(dir, "test3.mp3")}\" -filter_complex amix=inputs=2:duration=longest -y \"{Path.Combine(dir, "TESTOUTMERGE.mp3")}\"";
            //new ScreeneyEncoder("ffmpeg.exe", cmd);
            //Console.WriteLine("out");
            //Console.Read();

            //var dir = @"C:\Users\Caelan\Desktop\test";
            //var input = new string[] { Path.Combine(dir, "test2.mp3"), Path.Combine(dir, "test3.mp3") };
            //FFMpegConverterEx ex = new FFMpegConverterEx();
            //ex.ConvertProgress += (sender, e) =>
            //{
            //    Console.WriteLine(e.Processed + " / " + e.TotalDuration);
            //};
            //var aout = Path.Combine(dir, "YAYAYA.mp3");
            //ex.MergeAudio(input, aout);
            //Console.WriteLine("Done merge");

            ////ffmpeg - i test.avi - i test.wav - c:v libx264 -s hd720 - c:a libmp3lame -preset veryslow output.mp4
            //OutputSettings s = new OutputSettings();
            //s.AudioCodec = "libmp3lame";
            //s.AudioSampleRate = 22050;
            //s.VideoFrameSize = FrameSize.hd720;
            //s.VideoCodec = "libx264";
            //s.CustomOutputArgs = "-preset veryslow";
            //ex.CompileVideo(aout, Path.Combine(dir, "test1.avi"), Path.Combine(dir, "final.mp4"), s);

            //Console.WriteLine("done");
            //Console.Read();
        }
        public class ScreeneyEncoder
        {
            internal ScreeneyEncoder(string ffmpegPath, string cmdArgs)
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.Arguments = cmdArgs;
                psi.CreateNoWindow = true;
                psi.FileName = ffmpegPath;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;

                Process p = Process.Start(psi);
                while (!p.HasExited)
                {
                    var s = p.StandardError.ReadLine();
                    if (!String.IsNullOrWhiteSpace(s))
                        Console.WriteLine(s);
                }
            }

        }

        private static void Rec_RecordingUpdate(object sender, CaptureUpdateEventArgs e)
        {
            Console.WriteLine(e.ToString());
        }
        //static void Main()
        //{
        //    Clowd.Interop.USER32.SetProcessDPIAware();
        //    Console.WriteLine("Recording");

        //    var mm_devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        //    var device = mm_devices[0];

        //    var looback = true;
        //    var stereo = false;
        //    var audioFormat = looback ? device.AudioClient.MixFormat : new WaveFormat(44100, 16, stereo ? 2 : 1);
        //    var region = new Rectangle(0, 0, 1920, 1080);
        //    var framerate = 30;
        //    var bitrate = Math.Min(region.Width * region.Height * framerate, 6000000);
        //    var videoFileName = "test.mp4";
        //    var audioFileName = "test.wav";

        //    var silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        //    silence.Init(new SilenceProvider(audioFormat));
        //    silence.Play();

        //    var videoWriter = new VideoFileWriter();
        //    videoWriter.Open(videoFileName, region.Width, region.Height, framerate, VideoCodec.MPEG4, bitrate);
        //    var audioWriter = new WaveFileWriter(audioFileName, audioFormat);
        //    var video = new GdiCaptureSource(region, 1000 / framerate);
        //    var audio = new WasapiLoopbackCapture(device) { ShareMode = AudioClientShareMode.Shared };

        //    Stopwatch clock = new Stopwatch();
        //    int frameCount = 0;
        //    video.NewFrame += (s, e) =>
        //    {
        //        frameCount++;
        //        //videoWriter.WriteVideoFrame(e.Frame, clock.Elapsed);
        //    };
        //    audio.DataAvailable += (s, e) =>
        //    {
        //        //audioWriter.Write(e.Buffer, 0, e.BytesRecorded);
        //    };
        //    clock.Start();
        //    video.Start();
        //    audio.StartRecording();
        //    int lastFrames = 1;
        //    for (int i = 0; i < 15; i++)
        //    {
        //        Thread.Sleep(1000);
        //        Console.WriteLine($"Ellapsed: {clock.Elapsed.TotalSeconds} // Avg: {frameCount / clock.Elapsed.TotalSeconds} // Current: {(frameCount - lastFrames)}");
        //        lastFrames = frameCount;
        //    }
        //    audio.StopRecording();
        //    audio.Dispose();
        //    video.Stop();
        //    clock.Stop();
        //    videoWriter.Close();
        //    audioWriter.Close();
        //    silence.Stop();
        //    silence.Dispose();

        //    Console.WriteLine("Done.");
        //    Console.WriteLine($"Final Ellapsted time: {clock.Elapsed.TotalSeconds} // Frames Captured: {frameCount}");
        //}
        //static void asd()
        //{
        //    var audioDevices = new List<AudioCaptureDevice>();
        //    foreach (var audioViewModel in AudioCaptureDevices)
        //    {
        //        if (!audioViewModel.Checked)
        //            continue;

        //        var device = new AudioCaptureDevice(audioViewModel.DeviceInfo);
        //        device.AudioSourceError += device_AudioSourceError;
        //        device.Format = SampleFormat.Format16Bit;
        //        device.SampleRate = Settings.Default.SampleRate;
        //        device.DesiredFrameSize = 2 * 4098;
        //        device.Start();

        //        audioDevices.Add(device);
        //    }

        //    if (audioDevices.Count > 0) // Check if we need to record audio
        //    {
        //        var audioDevice = new AudioSourceMixer(audioDevices);
        //        audioDevice.AudioSourceError += device_AudioSourceError;
        //        audioDevice.NewFrame += audioDevice_NewFrame;
        //        audioDevice.Start();

        //        videoWriter.Open(OutputPath, width, height, framerate, VideoCodec.H264, videoBitRate,
        //            AudioCodec.MP3, audioBitRate, audioDevice.SampleRate, audioDevice.Channels);
        //    }
        //    else
        //    {
        //        videoWriter.Open(OutputPath, width, height, framerate, VideoCodec.H264, videoBitRate);
        //    }
        //}
    }
}
