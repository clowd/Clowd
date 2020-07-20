using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using NReco.VideoConverter;

namespace Clowd.Utilities
{
    public class LiveScreenRecording
    {
        public string FileName { get; private set; }
        public string ConsoleLog => log.ToString();

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
            var args = String.Join(" ", cli_VideoSource()?.Trim(), cli_FilterGraph()?.Trim(), cli_VideoCodecAndOutput()?.Trim());

            // run in a background thread
            return Task.Factory.StartNew(() => ffmpeg.Invoke(args), TaskCreationOptions.LongRunning);
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
            return $"-f gdigrab -offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -show_region 0 -draw_mouse {(settings.ShowCursor ? "1" : "0")} -i desktop";
        }

        private string cli_VideoCodecAndOutput()
        {
            var allCodecSettings = new FFMpegCodecSettings[] { settings.libx264, settings.h264_nvenc };
            var codec = allCodecSettings.Single(s => s.Codec == settings.VideoCodec);

            string extension = codec.Container;

            var filename = "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + extension;
            filename = Path.Combine(Path.GetFullPath(settings.OutputDirectory), filename);
            this.FileName = filename;

            //ulong bitrate = (ulong)Math.Round(((decimal)bounds.Width * bounds.Height * settings.TargetFramesPerSecond) * ((decimal)settings.OutputQuality / 1000m), 0);
            //string crf = "";
            //if (settings.H264CRF >= 0)
            //{
            //    crf = " -crf " + ((int)settings.H264CRF).ToString();
            //}
            return $"{codec.GetCliArguments(bounds.Width, bounds.Height, 30)} -n \"{filename}\"";
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

    public enum CaptureVideoCodec
    {
        [Description("h264 - software")]
        libx264 = 1,
        [Description("h264 - hardware / nvenc")]
        h264_nvenc = 2,
    }

    public enum FFMpegCodecOptionPreset
    {
        [Description("Fast / Lower Quality")]
        Fast_LowQuality = 1,
        [Description("Medium")]
        Medium = 2,
        [Description("Slow / Higher Quality")]
        Slow_HighQuality = 3,
        [Description("Custom")]
        Custom = 0,
    }

    public abstract class FFMpegCodecSettings : INotifyPropertyChanged
    {
        public FFMpegCodecOptionPreset Preset
        {
            get
            {
                return _preset;
            }
            set
            {
                if (_preset != value)
                {
                    _preset = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preset)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Options)));
                    if (_preset != FFMpegCodecOptionPreset.Custom)
                        _options = GetDefaultsForPreset(value);
                }
            }
        }

        public List<FFMpegCodecOption> Options
        {
            get
            {
                if (_preset == FFMpegCodecOptionPreset.Custom)
                    return _options ?? GetDefaultsForPreset(FFMpegCodecOptionPreset.Medium);

                return GetDefaultsForPreset(_preset);
            }
            //set
            //{
            //    _options = value;
            //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Options)));
            //}
        }

        private FFMpegCodecOptionPreset _preset = FFMpegCodecOptionPreset.Medium;
        private List<FFMpegCodecOption> _options;

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract CaptureVideoCodec Codec { get; }
        public abstract string Description { get; }
        public abstract string Container { get; }

        public virtual string GetCliArguments(int width, int height, int framerate)
        {
            Dictionary<string, string> variables = new Dictionary<string, string>();
            variables.Add("%width%", width.ToString());
            variables.Add("%height%", height.ToString());
            variables.Add("%rate%", framerate.ToString());
            variables.Add("%whr%", (width * height * framerate).ToString());

            StringBuilder sb = new StringBuilder();

            string eval(string value)
            {
                if (String.IsNullOrWhiteSpace(value))
                    return "";

                foreach (var v in variables)
                {
                    value = value.Replace(v.Key, v.Value);
                }

                var spl = value.Split('*').Select(s => s.Trim()).ToArray();

                if (spl.Length <= 1)
                    return value;

                decimal vint = Convert.ToDecimal(spl[0]);

                foreach (var i in spl.Skip(1))
                    vint = vint * Convert.ToDecimal(i);

                return ((int)vint).ToString();
            }

            foreach (var item in Options)
            {
                if (String.IsNullOrWhiteSpace(item.param_name))
                    continue;
                if (String.IsNullOrWhiteSpace(item.param_value))
                    continue;

                if (item.param_name.StartsWith("%"))
                {
                    variables.Add(item.param_name, eval(item.param_value));
                    continue;
                }

                sb.Append($" -{item.param_name} {eval(item.param_value)}");
            }

            return sb.ToString();
        }

        protected virtual List<FFMpegCodecOption> GetDefaultsForPreset(FFMpegCodecOptionPreset preset)
        {
            return GetDefaultsForIndex(ParseCSV(GetDefaultsCSVText()), (int)preset);
        }

        protected virtual List<FFMpegCodecOption> GetDefaultsForIndex(List<string[]> rawData, int index)
        {
            return rawData
                .Select(r => new FFMpegCodecOption { param_name = r[0], param_value = r[index] })
                .Where(o => !String.IsNullOrWhiteSpace(o.param_value))
                .ToList();
        }

        protected virtual List<string[]> ParseCSV(string csvText)
        {
            var output = new List<string[]>();

            byte[] defaultBytes = Encoding.UTF8.GetBytes(csvText);
            MemoryStream ms = new MemoryStream();
            ms.Write(defaultBytes, 0, defaultBytes.Length);
            ms.Position = 0;

            using (TextFieldParser parser = new TextFieldParser(ms))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                while (!parser.EndOfData)
                {
                    string[] fields = parser.ReadFields();
                    output.Add(fields);
                }
            }

            return output;
        }

        protected abstract string GetDefaultsCSVText();

        public class FFMpegCodecOption
        {
            public string param_name { get; set; }
            public string param_value { get; set; }
        }
    }

    public class FFMpegCodecSettings_libx264 : FFMpegCodecSettings
    {
        public override CaptureVideoCodec Codec => CaptureVideoCodec.libx264;

        public override string Description => "Software x264 encoder. Best results, but requires a lot of CPU resources. Consider a hardware encoder if you have a supported graphics card or intel processor.";

        public override string Container => "mp4";

        protected override string GetDefaultsCSVText()
        {
            // csv format: [param_name, fast_value, medium_value, slowhq_value]
            return
@"codec:v,libx264,libx264,libx264
preset:v,veryfast,veryfast,medium
profile:v,high,high,high
tune:v,animation,animation,animation
bf,3,4,4
crf:v,26,20,20
coder:v,cabac,cabac,cabac";
        }
    }

    public class FFMpegCodecSettings_h264_nvenc : FFMpegCodecSettings
    {
        public override CaptureVideoCodec Codec => CaptureVideoCodec.h264_nvenc;

        public override string Description => "Nvidia hardware encoding (nvenc) is available on Pascal, Turing, Volta and newer. Only use this option if you have a supported Nvidia graphics card.";

        public override string Container => "mp4";

        protected override string GetDefaultsCSVText()
        {
            // csv format: [param_name, fast_value, medium_value, slowhq_value]
            return
@"codec:v,h264_nvenc,h264_nvenc,h264_nvenc
preset:v,fast,medium,slow
profile:v,high,high,high
rc:v,vbr,vbr,vbr_hq
rc-lookahead:v,32,32,32
bf,3,4,4
b_ref_mode:v,middle,middle,middle
coder:v,cabac,cabac,cabac
%bmulti%,0.075,0.1,0.15
b:v,%bmulti% * %whr%,%bmulti% * %whr%,%bmulti% * %whr%
maxrate,1.25 * %bmulti% * %whr%,1.25 * %bmulti% * %whr%,1.25 * %bmulti% * %whr%
bufsize,2 * %bmulti% * %whr%,2 * %bmulti% * %whr%,2 * %bmulti% * %whr%";
        }
    }
}
