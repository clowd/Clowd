using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    public enum VideoQuality
    {
        Low = 29,
        Medium = 24,
        High = 19,
    }

    public enum VideoPerformance
    {
        Slow,
        Medium,
        Fast,
    }

    public enum VideoSubsamplingMode
    {
        yuv420,
        yuv444,
    }

    public class SettingsVideo : CategoryBase
    {
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => Set(ref _outputDirectory, value);
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

        public VideoPerformance Performance
        {
            get => _performance;
            set => Set(ref _performance, value);
        }

        public VideoSubsamplingMode SubsamplingMode
        {
            get => _subsamplingMode;
            set => Set(ref _subsamplingMode, value);
        }

        public bool CaptureSpeaker
        {
            get => _captureSpeaker;
            set => Set(ref _captureSpeaker, value);
        }

        public IAudioSpeakerDevice CaptureSpeakerDevice
        {
            get => _captureSpeakerDevice;
            set => Set(ref _captureSpeakerDevice, value);
        }

        public bool CaptureMicrophone
        {
            get => _captureMicrophone;
            set => Set(ref _captureMicrophone, value);
        }

        public IAudioMicrophoneDevice CaptureMicrophoneDevice
        {
            get => _captureMicrophoneDevice;
            set => Set(ref _captureMicrophoneDevice, value);
        }

        public bool HardwareAccelerated
        {
            get => _hardwareAccelerated;
            set => Set(ref _hardwareAccelerated, value);
        }

        public bool TrackMouseClicks
        {
            get => _trackMouseClicks;
            set => Set(ref _trackMouseClicks, value);
        }

        private string _outputDirectory;
        private int _fps = 30;
        private int _maxResolutionWidth;
        private int _maxResolutionHeight;
        private VideoQuality _quality = VideoQuality.Medium;
        private VideoPerformance _performance = VideoPerformance.Medium;
        private VideoSubsamplingMode _subsamplingMode = VideoSubsamplingMode.yuv420;
        private bool _captureSpeaker = false;
        private IAudioSpeakerDevice _captureSpeakerDevice;
        private bool _captureMicrophone = false;
        private IAudioMicrophoneDevice _captureMicrophoneDevice;
        private bool _hardwareAccelerated = true;
        private bool _trackMouseClicks = true;
    }
}
