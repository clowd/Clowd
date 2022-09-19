using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Clowd.Config
{
    public enum VideoQuality
    {
        [Description("Low (smaller files)")]
        Low = 29,
        [Description("Medium (balanced)")]
        Medium = 23,
        [Description("High (larger files)")]
        High = 16,
    }

    // public enum VideoPerformance
    // {
    //     Slow,
    //     Medium,
    //     Fast,
    // }

    // public enum VideoSubsamplingMode
    // {
    //     yuv420,
    //     yuv444,
    // }

    public enum VideoOutputType
    {
        MP4 = 1,
        GIF = 2,
    }

    public class SettingsVideo : CategoryBase
    {
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => Set(ref _outputDirectory, value);
        }

        public string FilenamePattern
        {
            get => _filenamePattern;
            set => Set(ref _filenamePattern, value);
        }

        public int Fps
        {
            get => _fps;
            set => Set(ref _fps, value);
        }

        public int MaxResolutionWidth
        {
            get => _maxResolutionWidth;
            set => Set(ref _maxResolutionWidth, value);
        }

        public int MaxResolutionHeight
        {
            get => _maxResolutionHeight;
            set => Set(ref _maxResolutionHeight, value);
        }

        public VideoQuality Quality
        {
            get => _quality;
            set => Set(ref _quality, value);
        }

        // public VideoPerformance Performance
        // {
        //     get => _performance;
        //     set => Set(ref _performance, value);
        // }
        //
        // public VideoSubsamplingMode SubsamplingMode
        // {
        //     get => _subsamplingMode;
        //     set => Set(ref _subsamplingMode, value);
        // }

        public VideoOutputType OutputMode
        {
            get => _outputMode;
            set => Set(ref _outputMode, value);
        }

        [Browsable(false)]
        public bool CaptureSpeaker
        {
            get => _captureSpeaker;
            set => Set(ref _captureSpeaker, value);
        }

        public AudioDeviceInfo CaptureSpeakerDevice
        {
            get => _captureSpeakerDevice;
            set => Set(ref _captureSpeakerDevice, value);
        }

        [Browsable(false)]
        public bool CaptureMicrophone
        {
            get => _captureMicrophone;
            set => Set(ref _captureMicrophone, value);
        }

        public AudioDeviceInfo CaptureMicrophoneDevice
        {
            get => _captureMicrophoneDevice;
            set => Set(ref _captureMicrophoneDevice, value);
        }

        public bool HardwareAccelerated
        {
            get => _hardwareAccelerated;
            set => Set(ref _hardwareAccelerated, value);
        }

        public bool ShowMouseCursor
        {
            get => _showMouseCursor;
            set => Set(ref _showMouseCursor, value);
        }

        public bool ShowClickAnimation
        {
            get => _showClickAnimation;
            set => Set(ref _showClickAnimation, value);
        }

        public Color ClickAnimationColor
        {
            get => _clickAnimationColor;
            set => Set(ref _clickAnimationColor, value);
        }

        public bool OpenFinishedInExplorer
        {
            get => _openFinishedInExplorer;
            set => Set(ref _openFinishedInExplorer, value);
        }

        private string _filenamePattern = "yyyy-MM-dd HH-mm-ss";
        private string _outputDirectory;
        private int _fps = 30;
        private int _maxResolutionWidth;
        private int _maxResolutionHeight;
        private VideoQuality _quality = VideoQuality.Medium;
        // private VideoPerformance _performance = VideoPerformance.Medium;
        // private VideoSubsamplingMode _subsamplingMode = VideoSubsamplingMode.yuv420;
        private VideoOutputType _outputMode = VideoOutputType.MP4;
        private bool _captureSpeaker = false;
        private AudioDeviceInfo _captureSpeakerDevice;
        private bool _captureMicrophone = false;
        private AudioDeviceInfo _captureMicrophoneDevice;
        private bool _hardwareAccelerated = true;
        private bool _showMouseCursor = true;
        private bool _showClickAnimation = true;
        private Color _clickAnimationColor = Colors.Red;
        private bool _openFinishedInExplorer = true;
    }
}
