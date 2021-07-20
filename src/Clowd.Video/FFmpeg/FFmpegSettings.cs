#pragma warning disable CS0414,CS0067
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;

namespace Clowd.Video.FFmpeg
{
    //[ImplementPropertyChanged]
    //public class FFmpegSettings : INotifyPropertyChanged//, RT.Serialization.IClassifyObjectProcessor
    //{
    //    public string SelectedId { get; set; }

    //    [ClassifyIgnore]
    //    public TrulyObservableCollection<FFmpegCodecPreset> SavedPresets { get; set; } = new TrulyObservableCollection<FFmpegCodecPreset>();

    //    private List<FFmpegCodecPreset> PersistedPresets = new List<FFmpegCodecPreset>();

    //    public event PropertyChangedEventHandler PropertyChanged;

    //    public FFmpegSettings()
    //    {

    //    }

    //    //public void BeforeSerialize()
    //    //{
    //    //    PersistedPresets = SavedPresets.ToList();
    //    //}

    //    //public void AfterDeserialize()
    //    //{
    //    //    var types = new Type[] { typeof(FFmpegCodecPreset_h264_nvenc), typeof(FFmpegCodecPreset_libx264) };
    //    //    foreach (var t in types)
    //    //    {
    //    //        if (PersistedPresets.All(p => p.GetType() != t))
    //    //            PersistedPresets.Add((FFmpegCodecPreset)Activator.CreateInstance(t));
    //    //    }

    //    //    PersistedPresets = PersistedPresets.OrderBy(p => p.IsCustom).ThenBy(p => p.Name).ToList();

    //    //    SavedPresets.AddRange(PersistedPresets);

    //    //    if (SelectedId == null)
    //    //        SelectedId = SavedPresets.Single(s => s.GetType() == typeof(FFmpegCodecPreset_libx264)).Id;
    //    //}

    //    public FFmpegCodecPreset GetSelectedPreset()
    //    {
    //        if (SelectedId == null)
    //            SelectedId = SavedPresets.Single(s => s.GetType() == typeof(FFmpegCodecPreset_libx264)).Id;

    //        return SavedPresets.Single(p => p.Id == SelectedId);
    //    }
    //}

    public class FFmpegCliOption : INotifyPropertyChanged
    {
        public string param_name { get; set; }
        public string param_value { get; set; }

        public FFmpegCliOption()
        {
        }

        public FFmpegCliOption(string name, string value)
        {
            param_name = name;
            param_value = value;
        }

        public FFmpegCliOption(string name, int value)
        {
            param_name = name;
            param_value = value.ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public interface FFmpegCodecPreset : INotifyPropertyChanged
    {
        string Id { get; set; }
        string Name { get; set; }
        string Description { get; set; }
        string Extension { get; set; }
        bool IsCustom { get; }
        List<FFmpegCliOption> GetOptions();
        void SetOptions(List<FFmpegCliOption> options);
    }

    //public class FFmpegDirectShowAudioDevice
    //{
    //    public string FriendlyName { get; private set; }
    //    public string DevicePath { get; private set; }

    //    public static FFmpegDirectShowAudioDevice[] GetDevices()
    //    {
    //        return DsDevice.GetDevicesOfCat(FilterCategory.AudioInputDevice).Select(k => new FFmpegDirectShowAudioDevice()
    //        {
    //            DevicePath = k.DevicePath,
    //            FriendlyName = k.Name,
    //        }).ToArray();
    //    }

    //    public override bool Equals(object obj)
    //    {
    //        var other = obj as FFmpegDirectShowAudioDevice;
    //        if (other == null)
    //            return false;

    //        return other.DevicePath == this.DevicePath;
    //    }

    //    public override int GetHashCode()
    //    {
    //        return DevicePath.GetHashCode();
    //    }
    //}

    public abstract class FFmpegCodecPreset_AudioBase : FFmpegCodecPreset
    {
        [Browsable(false)]
        public string Id { get; set; }

        [Browsable(false)]
        public string Name { get; set; }

        [Browsable(false)]
        public string Description { get; set; }

        [Browsable(false)]
        public string Extension { get; set; }

        [Browsable(false)]
        public bool IsCustom => false;

        public int CQ { get; set; } = 29;

        public int MaxWidth { get; set; } = 0;


        //[PropertyTools.DataAnnotations.Category("Audio")]
        //public bool CaptureLoopbackAudio
        //{
        //    get => GetDirectShowDependency(IsDirectShowInstalled, nameof(CaptureLoopbackAudio), this, l => l._loopback);
        //    set => SetDirectShowDependency(IsDirectShowInstalled, value, nameof(CaptureLoopbackAudio), this, l => l._loopback);
        //}
        //public bool CaptureLoopbackAudio
        //{
        //    get
        //    {
        //        var ret = _loopback && IsLoopbackInstalled;
        //        if (_loopback != ret)
        //        {
        //            _loopback = ret;
        //            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CaptureLoopbackAudio)));
        //        }
        //        return ret;
        //    }
        //    set
        //    {
        //        var isInstalled = IsLoopbackInstalled;
        //        if (!isInstalled && value)
        //        {
        //            NiceDialog.ShowSettingsPromptAsync(
        //                TemplatedWindow.GetWindow(typeof(SettingsPage)),
        //                SettingsCategory.Windows,
        //                $"You must install 'Windows/DirectShow Add-ons' before {App.ClowdAppName} is able to capture loopback audio");

        //            _loopback = false;
        //        }
        //        else
        //        {
        //            _loopback = value;
        //        }
        //        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CaptureLoopbackAudio)));
        //    }
        //}

        //public bool CaptureMicrophone { get; set; } = false;

        //[Browsable(false)]
        //public bool IsDirectShowInstalled => App.Current.Settings.FeatureSettings.DirectShow.CheckInstalled(System.Reflection.Assembly.GetEntryAssembly().Location);

        //[PropertyTools.DataAnnotations.EnableBy(nameof(CaptureMicrophone), true)]
        //public FFmpegDirectShowAudioDevice SelectedMicrophone { get; set; }

        //[PropertyTools.DataAnnotations.Category("Video")]
        //[Description("This may solve some DPI related bugs, and also improves audio sync if capturing loopback audio")]
        //public bool EnhancedVideoCapture
        //{
        //    get => GetDirectShowDependency(IsDirectShowInstalled, nameof(CaptureLoopbackAudio), this, l => l._enhanced);
        //    set => SetDirectShowDependency(IsDirectShowInstalled, value, nameof(CaptureLoopbackAudio), this, l => l._enhanced);
        //}

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract List<FFmpegCliOption> GetOptions();

        private bool _enhanced = false;
        private bool _loopback = false;

        public virtual void SetOptions(List<FFmpegCliOption> options)
        {
            throw new NotImplementedException();
        }

        //protected void AddAudioVideoPresets(List<FFmpegCliOption> output)
        //{
        //    DirectShowFilterInfo loopback = null;// CaptureLoopbackAudio ? DShowFilter.DefaultAudio : null;
        //    DirectShowFilterInfo enhancedVideo = null;// EnhancedVideoCapture ? DShowFilter.DefaultVideo : null;
        //    DirectShowFilterInfo microphone = null;// CaptureMicrophone ? SelectedMicrophone : null;

        //    if (loopback != null && enhancedVideo != null) // video=dshow, audio=dshow
        //    {
        //        output.Add(new FFmpegCliOption("f", "dshow"));
        //        output.Add(new FFmpegCliOption("i", $"video=\"{enhancedVideo.FilterName}\":audio=\"{loopback.FilterName}\""));
        //    }
        //    else
        //    {
        //        if (enhancedVideo != null) // video=dshow, audio=null
        //        {
        //            output.Add(new FFmpegCliOption("f", "dshow"));
        //            output.Add(new FFmpegCliOption("i", $"video=\"{enhancedVideo.FilterName}\""));
        //        }
        //        else
        //        {
        //            output.Add(new FFmpegCliOption("f", "gdigrab"));
        //            output.Add(new FFmpegCliOption("i", "desktop"));
        //        }

        //        if (loopback != null)
        //        {
        //            output.Add(new FFmpegCliOption("f", "dshow"));
        //            output.Add(new FFmpegCliOption("i", $"audio=\"{loopback.FilterName}\""));
        //        }
        //    }

        //    //else if (loopback != null) // video=gdigrab, audio=dshow
        //    //{
        //    //    output.Add(new FFmpegCliOption("f", "gdigrab"));
        //    //    output.Add(new FFmpegCliOption("i", "desktop"));

        //    //    output.Add(new FFmpegCliOption("f", "dshow"));
        //    //    output.Add(new FFmpegCliOption("i", $"audio=\"{loopback.FilterName}\""));
        //    //}
        //    //else if (enhancedVideo != null) // video=dshow, audio=null
        //    //{
        //    //    output.Add(new FFmpegCliOption("f", "dshow"));
        //    //    output.Add(new FFmpegCliOption("i", $"video=\"{enhancedVideo.FilterName}\""));
        //    //}
        //    //else // video=gdigrab, audio=null
        //    //{
        //    //    output.Add(new FFmpegCliOption("f", "gdigrab"));
        //    //    output.Add(new FFmpegCliOption("i", "desktop"));
        //    //}

        //    if (microphone != null)
        //    {
        //        output.Add(new FFmpegCliOption("f", "dshow"));
        //        output.Add(new FFmpegCliOption("i", $"audio=\"{microphone.FriendlyName}\""));
        //    }

        //    // if capturing both loopback, and mic, we need to amix them
        //    if (loopback != null && microphone != null)
        //    {
        //        // https://stackoverflow.com/questions/14498539/how-to-overlay-downmix-two-audio-files-using-ffmpeg
        //        // -filter_complex amix=inputs=2:duration=longest
        //        output.Add(new FFmpegCliOption("filter_complex", "amix=inputs=2:duration=longest"));
        //    }

        //    // audio codec - video codec added later
        //    if (loopback != null || microphone != null)
        //    {
        //        output.Add(new FFmpegCliOption("codec:a", "libmp3lame"));
        //        output.Add(new FFmpegCliOption("aq", "2"));
        //    }
        //}

        //protected void SetDirectShowDependency<TObj>(bool condition, bool requestedValue, string propertyName, TObj source, Expression<Func<TObj, bool>> localField)
        //{
        //    var field = Expr.GetFieldInfoFromExpression(localField);

        //    if (!condition && requestedValue)
        //    {
        //        NiceDialog.ShowSettingsPromptAsync(
        //            TemplatedWindow.GetWindow(typeof(SettingsPage)),
        //            SettingsCategory.Windows,
        //            $"You must install 'Windows/DirectShow Add-ons' before {Constants.ClowdAppName} is able to capture loopback audio");

        //        field.SetValue(this, false);
        //    }
        //    else
        //    {
        //        field.SetValue(this, requestedValue);
        //    }
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //}

        //protected bool GetDirectShowDependency<TObj>(bool condition, string propertyName, TObj source, Expression<Func<TObj, bool>> localField)
        //{
        //    var field = Expr.GetFieldInfoFromExpression(localField);

        //    var currentValue = (bool)field.GetValue(this);

        //    var ret = currentValue && condition;
        //    if (currentValue != ret)
        //    {
        //        field.SetValue(this, ret);
        //        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //    }
        //    return ret;
        //}
    }

    public class FFmpegCodecPreset_h264_nvenc : FFmpegCodecPreset_AudioBase
    {
        //[PropertyTools.DataAnnotations.Category("Video")]
        //public MaxResolution MaxResolution { get; set; } = MaxResolution.Uncapped;

        //[DisplayName("Quality (CQ)")]
        //[Description("Lower = higher quality, larger files\nHigher = lower quality, smaller files")]
        //[PropertyTools.DataAnnotations.Slidable(Minimum = 19, Maximum = 39, SnapToTicks = true, TickFrequency = 5, SmallChange = 5, LargeChange = 5)]
        //public int CQ { get; set; } = 29;

        public FFmpegNVENCPerformanceMode PerformanceMode { get; set; } = FFmpegNVENCPerformanceMode.Slow;

        public FFmpegNVENCChromaSubsamplingMode2 SubsamplingMode { get; set; } = FFmpegNVENCChromaSubsamplingMode2.yuv420;

        public FFmpegCodecPreset_h264_nvenc()
        {
            Id = Guid.NewGuid().ToString();
            Name = "h264_nvenc (nvidia h/w)";
            Description = "Nvidia hardware encoding (nvenc) is available on Pascal, Turing, Volta and newer. Only use this option if you have a supported Nvidia graphics card.";
            Extension = "mp4";
        }

        public override List<FFmpegCliOption> GetOptions()
        {
            var output = new List<FFmpegCliOption>();

            output.Add(new FFmpegCliOption("f", "gdigrab"));
            output.Add(new FFmpegCliOption("i", "desktop"));

            //AddAudioVideoPresets(output);

            //int resolution = (int)/*MaxResolution*/;
            int resolution = MaxWidth;
            if (resolution > 0)
                output.Add(new FFmpegCliOption("vf", $@"""scale=trunc(oh*a/2)*2:min({resolution}\,iw)"""));
            else
                output.Add(new FFmpegCliOption("vf", $@"""crop=floor(iw/2)*2:floor(ih/2)*2""")); // crop one pixel if the image is not even dimensions

            // output.Add(new FFMpegCodecOption2("vf", $@"""pad=ceil(iw/2)*2:ceil(ih/2)*2""")); // pad one black pixel if the image is not even dimensions

            output.Add(new FFmpegCliOption("codec:v", "h264_nvenc"));
            output.Add(new FFmpegCliOption("bf:v", "0"));
            output.Add(new FFmpegCliOption("b:v", "0"));

            switch (SubsamplingMode)
            {
                case FFmpegNVENCChromaSubsamplingMode2.yuv420:
                    output.Add(new FFmpegCliOption("profile:v", "high"));
                    output.Add(new FFmpegCliOption("pix_fmt", "yuv420p"));
                    break;
                case FFmpegNVENCChromaSubsamplingMode2.yuv444:
                    output.Add(new FFmpegCliOption("profile:v", "high444p"));
                    output.Add(new FFmpegCliOption("pix_fmt", "yuv444p"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(SubsamplingMode));
            }

            switch (PerformanceMode)
            {
                case FFmpegNVENCPerformanceMode.Slow:
                    output.Add(new FFmpegCliOption("preset:v", "slow"));
                    output.Add(new FFmpegCliOption("rc", "vbr_hq"));
                    output.Add(new FFmpegCliOption("rc-lookahead", "32"));
                    output.Add(new FFmpegCliOption("cq:v", CQ.ToString()));
                    break;
                case FFmpegNVENCPerformanceMode.Medium:
                    output.Add(new FFmpegCliOption("preset:v", "slow"));
                    output.Add(new FFmpegCliOption("rc", "vbr"));
                    output.Add(new FFmpegCliOption("rc-lookahead", "0"));
                    output.Add(new FFmpegCliOption("cq:v", CQ.ToString()));
                    break;
                case FFmpegNVENCPerformanceMode.Fast:
                    output.Add(new FFmpegCliOption("preset:v", "llhq"));
                    output.Add(new FFmpegCliOption("rc", "constqp"));
                    output.Add(new FFmpegCliOption("rc-lookahead", "0"));
                    output.Add(new FFmpegCliOption("qp", CQ.ToString()));
                    output.Add(new FFmpegCliOption("zerolatency", "1"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(PerformanceMode));
            }

            return output;
        }

        public enum FFmpegNVENCChromaSubsamplingMode2
        {
            yuv420 = 0,
            yuv444 = 2,
        }

        public enum FFmpegNVENCPerformanceMode
        {
            Slow,
            Medium,
            Fast,
        }
    }

    public class FFmpegCodecPreset_libx264 : FFmpegCodecPreset_AudioBase
    {
        //public MaxResolution MaxResolution { get; set; } = MaxResolution.Uncapped;

        //[DisplayName("Quality (CRF)")]
        //[Description("Lower = higher quality, larger files\nHigher = lower quality, smaller files")]
        ////[PropertyTools.DataAnnotations.Slidable(Minimum = 19, Maximum = 39, SnapToTicks = true, TickFrequency = 5, SmallChange = 5, LargeChange = 5)]
        //public int CRF { get; set; } = 29;

        public FFmpegLib264PerformanceMode PerformanceMode { get; set; } = FFmpegLib264PerformanceMode.Fast;

        public FFmpegLib264ChromaSubsamplingMode2 SubsamplingMode { get; set; } = FFmpegLib264ChromaSubsamplingMode2.yuv420;

        public FFmpegCodecPreset_libx264()
        {
            Id = Guid.NewGuid().ToString();
            Name = "h264_libx264 (software)";
            Description = "Software encoder; Has good support, but is much slower. Consider using a hardware encoder instead if you have a supported system configuration.";
            Extension = "mp4";
        }

        public override List<FFmpegCliOption> GetOptions()
        {
            var output = new List<FFmpegCliOption>();

            output.Add(new FFmpegCliOption("f", "gdigrab"));
            output.Add(new FFmpegCliOption("i", "desktop"));

            //AddAudioVideoPresets(output);

            //int resolution = (int)/*MaxResolution*/;
            int resolution = MaxWidth;
            if (resolution > 0)
                output.Add(new FFmpegCliOption("vf", $@"""scale=trunc(oh*a/2)*2:min({resolution}\,iw)"""));
            else
                output.Add(new FFmpegCliOption("vf", $@"""crop=floor(iw/2)*2:floor(ih/2)*2""")); // crop one pixel if the image is not even dimensions

            output.Add(new FFmpegCliOption("codec:v", "libx264"));
            output.Add(new FFmpegCliOption("threads", "4"));

            switch (SubsamplingMode)
            {
                case FFmpegLib264ChromaSubsamplingMode2.yuv420:
                    output.Add(new FFmpegCliOption("profile:v", "high"));
                    output.Add(new FFmpegCliOption("pix_fmt", "yuv420p"));
                    break;
                case FFmpegLib264ChromaSubsamplingMode2.yuv422:
                    output.Add(new FFmpegCliOption("profile:v", "high422"));
                    output.Add(new FFmpegCliOption("pix_fmt", "yuv422p"));
                    break;
                case FFmpegLib264ChromaSubsamplingMode2.yuv444:
                    output.Add(new FFmpegCliOption("profile:v", "high444"));
                    output.Add(new FFmpegCliOption("pix_fmt", "yuv444p"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(SubsamplingMode));
            }

            switch (PerformanceMode)
            {
                case FFmpegLib264PerformanceMode.Slow:
                    output.Add(new FFmpegCliOption("preset:v", "medium"));
                    output.Add(new FFmpegCliOption("tune", "psnr"));
                    output.Add(new FFmpegCliOption("g", "60"));
                    output.Add(new FFmpegCliOption("bf:v", "3"));
                    output.Add(new FFmpegCliOption("keyint_min", "60"));
                    output.Add(new FFmpegCliOption("sc_threshold", "0"));
                    output.Add(new FFmpegCliOption("crf", CQ.ToString()));
                    break;
                case FFmpegLib264PerformanceMode.Fast:
                    output.Add(new FFmpegCliOption("preset:v", "veryfast"));
                    output.Add(new FFmpegCliOption("tune", "zerolatency"));
                    output.Add(new FFmpegCliOption("g", "999999"));
                    output.Add(new FFmpegCliOption("bf:v", "0"));
                    output.Add(new FFmpegCliOption("x264opts", "no-sliced-threads:nal-hrd=cbr"));
                    output.Add(new FFmpegCliOption("crf", CQ.ToString()));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(PerformanceMode));
            }

            return output;
        }

        public enum FFmpegLib264PerformanceMode
        {
            Slow,
            Fast,
        }

        public enum FFmpegLib264ChromaSubsamplingMode2
        {
            yuv420 = 0,
            yuv422 = 1,
            yuv444 = 2,
        }
    }

    //[ImplementPropertyChanged]
    //public class FFmpegCodecPreset_Custom : FFmpegCodecPreset
    //{
    //    [Browsable(false)]
    //    public string Id { get; set; }

    //    [Browsable(true)]
    //    public string Name { get; set; }

    //    [Browsable(false)]
    //    public string Description { get; set; }

    //    [Browsable(true)]
    //    public string Extension { get; set; }

    //    [Browsable(false)]
    //    public bool IsCustom => true;

    //    private List<FFmpegCliOption> _options = new List<FFmpegCliOption>();

    //    public event PropertyChangedEventHandler PropertyChanged;

    //    protected FFmpegCodecPreset_Custom()
    //    {
    //    }

    //    public FFmpegCodecPreset_Custom(string name, string parentName, string extension)
    //    {
    //        Id = Guid.NewGuid().ToString();
    //        Name = name;
    //        Description = "Custom preset created on " + DateTime.Now.ToShortDateString() + " at " + DateTime.Now.ToShortTimeString() + " from existing preset '" + parentName + "'.";
    //        Extension = extension;
    //    }

    //    public List<FFmpegCliOption> GetOptions()
    //    {
    //        return _options.Select(o => new FFmpegCliOption(o.param_name, o.param_value)).ToList();
    //    }

    //    public void SetOptions(List<FFmpegCliOption> options)
    //    {
    //        _options = options;
    //    }
    //}
}
