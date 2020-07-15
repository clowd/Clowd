using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using NReco.VideoConverter;

namespace Clowd.Utilities
{
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
            string crf = "";
            if (settings.H264CRF >= 0)
            {
                crf = " -crf " + ((int)settings.H264CRF).ToString();
            }
            return $"{codec}{crf} -n \"{filename}\"";
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

            return codec + $" -preset {settings.H264Preset.ToString()} -tune animation";
        }
    }

}
