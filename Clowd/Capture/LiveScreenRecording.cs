using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using Clowd.Installer.Features;
using Microsoft.VisualBasic.FileIO;
using NReco.VideoConverter;
using PropertyChanged;

namespace Clowd.Utilities
{
    public class LiveScreenRecording
    {
        public string FileName { get; private set; }
        public string ConsoleLog => log.ToString();
        public string OutputDirectory => settings.OutputDirectory;

        private readonly Rectangle bounds;
        private readonly VideoSettings settings;
        private readonly FFMpegConverter ffmpeg;
        private Task runner;
        private StringBuilder log = new StringBuilder();

        public event EventHandler<FFMpegLogEventArgs> LogReceived;

        public LiveScreenRecording(Rectangle bounds)
        {
            this.bounds = bounds;
            settings = App.Current.Settings.VideoSettings;
            ffmpeg = new FFMpegConverter();
            ffmpeg.LogReceived += (s, e) =>
            {
                Console.WriteLine(e.Data);
                log.AppendLine(e.Data);
                LogReceived?.Invoke(this, e);
            };

            //ffmpeg 
            //-f gdigrab -i desktop -framerate 30 -offset_x 10 -offset_y 20 -video_size 640x480 -show_region 1
        }

        public Task Start()
        {
            //var args = String.Join(" ", cli_VideoSource()?.Trim(), cli_VideoCodecAndOutput()?.Trim());
            var args = cli_VideoCodecAndOutput().Trim();

            // run in a background thread
            runner = Task.Factory.StartNew(() => ffmpeg.Invoke(args), TaskCreationOptions.LongRunning);
            return runner;
        }

        public async Task Stop()
        {
            ffmpeg.Stop();
            await runner;
        }

        //private string cli_FilterGraph()
        //{
        //    List<string> filters = new List<string>();
        //    //if (settings.MaxResolution != MaxResolution.Uncapped && bounds.Height > (int)settings.MaxResolution)
        //    //{
        //    //    // keep aspect ratio but limit height to specified max resolution
        //    //    filters.Add("scale=-1:" + (int)settings.MaxResolution);
        //    //}

        //    //if (false)
        //    //{
        //    //    filters.Add("mpdecimate");
        //    //    filters.Add("framerate=" + settings.TargetFramesPerSecond);
        //    //}

        //    if (filters.Any())
        //    {
        //        return $"-filter:v \"{String.Join(",", filters)}\"";
        //    }
        //    else return null;
        //}

        //private string cli_VideoSource()
        //{
        //    //if (DShowFilter.DefaultVideo != null && settings.VideoCodec.GetSelectedPreset() is FFmpegCodecPreset_AudioBase audio && audio.CaptureLoopbackAudio && audio.EnhancedAudioVideoSync)
        //    if (true)
        //    {
        //        // if the above is true, the video will be added to the same audio clock in order to sync the video capture with the audio
        //        return "";
        //    }
        //    else
        //    {
        //        //return $"-f gdigrab -framerate {settings.TargetFramesPerSecond} -offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -show_region 1 -draw_mouse {(settings.ShowCursor ? "1" : "0")} -i desktop";
        //        return $"-f gdigrab -offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -show_region 0 -draw_mouse {(settings.ShowCursor ? "1" : "0")} -i desktop";
        //    }
        //}

        private string cli_VideoCodecAndOutput()
        {
            var codec = settings.VideoCodec.GetSelectedPreset();
            string extension = codec.Extension;

            var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + extension;
            filename = Path.Combine(Path.GetFullPath(settings.OutputDirectory), filename);
            this.FileName = filename;

            //ulong bitrate = (ulong)Math.Round(((decimal)bounds.Width * bounds.Height * settings.TargetFramesPerSecond) * ((decimal)settings.OutputQuality / 1000m), 0);
            //string crf = "";
            //if (settings.H264CRF >= 0)
            //{
            //    crf = " -crf " + ((int)settings.H264CRF).ToString();
            //}

            var options = codec.GetOptions();

            var gdigrabIndex = options.FindIndex(k => k.param_value.Equals("gdigrab", StringComparison.OrdinalIgnoreCase));
            var uscreenIndex = options.FindIndex(k => k.param_value.Contains("UScreenCapture"));

            if (gdigrabIndex < 0 && uscreenIndex < 0)
                throw new Exception("Error in video codec settings: Unknown screen capture mechanism. Can not supply desired screen coordinates to FFmpeg.");

            var fps = Math.Min(settings.FPS, 60);

            if (gdigrabIndex >= 0)
            {
                var gdiOptions = new List<FFmpegCliOption>()
                {
                    new FFmpegCliOption("framerate", fps),
                    new FFmpegCliOption("offset_x", bounds.Left),
                    new FFmpegCliOption("offset_y", bounds.Top),
                    new FFmpegCliOption("video_size", $"{bounds.Width}x{bounds.Height}"),
                    new FFmpegCliOption("draw_mouse", settings.ShowCursor ? "1" : "0"),
                };
                options.InsertRange(gdigrabIndex + 1, gdiOptions);
            }

            if (uscreenIndex >= 0)
            {
                UScreen.SetProperties(bounds, fps, settings.ShowCursor, true);
            }

            var args = String.Join(" ", options.Select(o => $"-{o.param_name} {o.param_value}"));
            return $"{args} -n \"{filename}\"";
        }

        //private string cli_VideoCodecAndOutput()
        //{
        //    string codec = "";
        //    string extension = "";
        //    switch (settings.VideoCodec)
        //    {
        //        case CaptureVideoCodec.libx264:
        //            codec = codec_H264();
        //            extension = "mp4";
        //            break;
        //        default:
        //            throw new NotImplementedException();
        //    }

        //    var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + extension;
        //    filename = Path.Combine(Path.GetFullPath(settings.OutputDirectory), filename);
        //    this.FileName = filename;

        //    //ulong bitrate = (ulong)Math.Round(((decimal)bounds.Width * bounds.Height * settings.TargetFramesPerSecond) * ((decimal)settings.OutputQuality / 1000m), 0);
        //    //string crf = "";
        //    //if (settings.H264CRF >= 0)
        //    //{
        //    //    crf = " -crf " + ((int)settings.H264CRF).ToString();
        //    //}
        //    return $"{codec} -n \"{filename}\"";
        //}

        //private string OLDDDDD_codec_H264()
        //{
        //    // https://github.com/obsproject/obs-studio/blob/959dbb64ed1c8d12723a3161e2b571a2a913b3d9/UI/window-basic-settings.cpp#L4331
        //    // https://github.com/obsproject/obs-studio/blob/959dbb64ed1c8d12723a3161e2b571a2a913b3d9/UI/window-basic-settings.cpp#L4380

        //    string codec = "-codec:v libx264";
        //    // suggests 5-6 bframes is best https://www.reddit.com/r/obs/comments/40ylt8/a_simple_x264_tweak_in_obs_which_can_make_a_big/ 
        //    // rc_lookahead 10 or 20?
        //    //if (settings.HardwareAcceleration)
        //    if (false)
        //    {
        //        ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DisplayConfiguration");

        //        string graphicsCard = string.Empty;
        //        foreach (ManagementObject mo in searcher.Get())
        //        {
        //            foreach (PropertyData property in mo.Properties)
        //            {
        //                if (property.Name == "Description")
        //                {
        //                    graphicsCard = property.Value.ToString();
        //                }
        //            }
        //        }

        //        if (graphicsCard.ToUpper().Contains("NVIDIA"))
        //        {
        //            //return "-codec:v h264_nvenc -preset slow -rc:v vbr_hq -cq:v 23";
        //            // for film, 3 bframes? for animations - 16 bframes????
        //            // this suggests (for libx264) that 16 bframes is nolonger viable (sensible max is 5) https://forum.doom9.org/showthread.php?t=139827 

        //            // preset and audio (linux) https://gist.github.com/Brainiarc7/4636a162ef7dc2e8c9c4c1d4ae887c0e
        //            // good overview of all hardware encoders https://gist.github.com/Brainiarc7/4b49f463a08377530df6cecb8171306a
        //            // set level.. 4.1 reasonable?  https://en.wikipedia.org/wiki/Advanced_Video_Coding#Levels
        //            // https://en.wikipedia.org/wiki/H.264/MPEG-4_AVC#Levels
        //            // https://superuser.com/questions/1296374/best-settings-for-ffmpeg-with-nvenc/1296511#1296511
        //            // https://superuser.com/a/1236387/27539
        //            // https://developer.nvidia.com/blog/nvidia-ffmpeg-transcoding-guide/
        //            return "-codec:v h264_nvenc -preset slow -rc:v vbr -cq:v 23";
        //        }

        //        if (graphicsCard.ToUpper().Contains("AMD"))
        //        {
        //            return "-codec:v h264_amf";
        //        }

        //        // h264_qsv (INTEL)
        //        // https://trac.ffmpeg.org/wiki/Hardware/QuickSync

        //        // real time vp9 encoding
        //        // https://developers.google.com/media/vp9/live-encoding
        //    }

        //    return codec + $" -preset veryfast -tune animation";
        //}
    }


}
