using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace Clowd.Config
{
    /// <summary>
    /// Marks a string settings property as an audio device id so the settings factory renders it
    /// as a device dropdown (fed by AudioDeviceManager) instead of a free-text box. The stored
    /// value stays a plain string ("default" or a platform device id), so persistence and the
    /// obs-express CLI mapping are unchanged.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class AudioDeviceSelectorAttribute : Attribute
    {
        /// <summary>"speaker" (output/render devices) or "microphone" (input/capture devices).</summary>
        public string DeviceType { get; }

        public AudioDeviceSelectorAttribute(string deviceType)
        {
            DeviceType = deviceType;
        }
    }

    /// <summary>
    /// Marks a string settings property as a camera device id so the settings factory renders it
    /// as a device dropdown (fed by CameraDeviceManager) instead of a free-text box. The stored
    /// value stays a plain string (a platform device id, or empty for "no camera"), so persistence
    /// and the obs-express settings mapping are unchanged. Unlike audio there is no device *type*
    /// to distinguish and no "default" pseudo-device: a webcam is only ever captured when the user
    /// has picked one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class CameraDeviceSelectorAttribute : Attribute
    {
    }

    /// <summary>
    /// Hides a settings row from the generated settings UI on macOS. The property still persists
    /// and is still applied where the platform honors it — used for the speaker device picker,
    /// which selects nothing on macOS: ScreenCaptureKit captures the whole system mix, so there
    /// is no output device to choose (obs-express ignores the id on macOS 13+).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class HiddenOnMacOSAttribute : Attribute
    {
    }

    /// <summary>
    /// Recording quality preset. The numeric value is the encoder CRF/CQP passed straight through
    /// to obs-express via <c>--crf</c> (lower = higher quality). Persisted by name (no converter
    /// required); <c>(int)VideoQuality</c> yields the CRF.
    /// </summary>
    public enum VideoQuality
    {
        [Description("Low (smaller file)")]
        Low = 29,

        [Description("Medium")]
        Medium = 23,

        [Description("High (larger file)")]
        High = 16,
    }

    /// <summary>
    /// GIF conversion preset, passed straight through to vid2gif via <c>--quality</c> (the lowercase
    /// member name is the CLI value). It trades frame rate and dithering fidelity against file size;
    /// GIFs are palette-limited, so this moves the size far more than a video quality preset does.
    /// </summary>
    public enum GifQuality
    {
        [Description("Best (20 fps, smoothest dithering, largest file)")]
        Best,

        [Description("Good (15 fps, balanced)")]
        Good,

        [Description("Fair (10 fps, smallest file)")]
        Fair,
    }

    /// <summary>What Clowd shows the user once a recording has been saved. Declaration order is
    /// the order the settings dropdown offers them in, so the default comes first.</summary>
    public enum RecordingFinishAction
    {
        /// <summary>Opens the recording in the video editor. Falls back to the Recents page where
        /// the editor cannot run (non-Windows) or the entry is not editable (a GIF).</summary>
        [Description("Video Editor")]
        VideoEditor,

        [Description("Recent Page")]
        RecentsPage,

        [Description("Output Folder")]
        OutputFolder,

        [Description("None")]
        None,
    }

    /// <summary>
    /// Screen-recording settings, mirrored into the obs-express CLI at recording start
    /// (see the video-recording design, §4.2). A settings snapshot is taken when a recording is
    /// initialized, so changes apply to the next recording only. Device ids are plain strings
    /// ("default" = system default device) rendered as dropdowns via [AudioDeviceSelector].
    ///
    /// Declaration order is the page's section order (the settings factory groups by first
    /// appearance of each [Category]), which is why Output comes first.
    /// </summary>
    public class SettingsRecording : SimpleNotifyObject
    {
        [Category("Output")]
        [DisplayName("Output folder")]
        [Description("Folder that finished recordings are saved to. If it is empty or unavailable, your Videos folder is used.")]
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => Set(ref _outputDirectory, value);
        }

        [Category("Output")]
        [DisplayName("Filename pattern")]
        [Description("Date format used to name saved recordings (.NET date format string). A number is appended if the name is already taken.")]
        public string FilenamePattern
        {
            get => _filenamePattern;
            set => Set(ref _filenamePattern, value);
        }

        [Category("Output")]
        [DisplayName("Open when finished")]
        [Description("What to show when a recording finishes: the video editor (to trim it and place the webcam), the Recents page (to play or upload it), the folder it was saved to, or nothing")]
        public RecordingFinishAction OpenWhenFinished
        {
            get => _openWhenFinished;
            set => Set(ref _openWhenFinished, value);
        }

        [Category("Video")]
        [DisplayName("Frame rate")]
        [Description("Recording frame rate in frames per second")]
        [Range(1, 240)]
        public int Fps
        {
            get => _fps;
            set => Set(ref _fps, value);
        }

        [Category("Video")]
        [DisplayName("Quality")]
        [Description("Encoder quality preset — higher quality produces larger files")]
        public VideoQuality Quality
        {
            get => _quality;
            set => Set(ref _quality, value);
        }

        [Category("Video")]
        [DisplayName("Max output width")]
        [Description("Downscale the recording so its width does not exceed this many pixels (0 = no limit). Aspect ratio is preserved.")]
        [Range(0, 16384)]
        public int MaxResolutionWidth
        {
            get => _maxResolutionWidth;
            set => Set(ref _maxResolutionWidth, value);
        }

        [Category("Video")]
        [DisplayName("Max output height")]
        [Description("Downscale the recording so its height does not exceed this many pixels (0 = no limit). Aspect ratio is preserved.")]
        [Range(0, 16384)]
        public int MaxResolutionHeight
        {
            get => _maxResolutionHeight;
            set => Set(ref _maxResolutionHeight, value);
        }

        [Category("Video")]
        [DisplayName("Hardware acceleration")]
        [Description("Prefer a hardware H.264 encoder (NVENC/AMF/QSV) when available, falling back to software x264")]
        public bool HardwareAccelerated
        {
            get => _hardwareAccelerated;
            set => Set(ref _hardwareAccelerated, value);
        }

        [Category("Video")]
        [DisplayName("Show mouse cursor")]
        [Description("Include the mouse cursor in the recording")]
        public bool ShowMouseCursor
        {
            get => _showMouseCursor;
            set => Set(ref _showMouseCursor, value);
        }

        [Category("Video")]
        [DisplayName("Highlight mouse clicks")]
        [Description("Show an expanding highlight at the pointer on every click — visible only in the recording, not on your screen")]
        public bool HighlightClicks
        {
            get => _highlightClicks;
            set => Set(ref _highlightClicks, value);
        }

        [Category("Audio")]
        [DisplayName("Capture speakers")]
        [Description("Record system/speaker output audio")]
        public bool CaptureSpeaker
        {
            get => _captureSpeaker;
            set => Set(ref _captureSpeaker, value);
        }

        [Category("Audio")]
        [DisplayName("Speaker device")]
        [Description("Speaker/output device to record")]
        [AudioDeviceSelector("speaker")]
        [HiddenOnMacOS]
        public string SpeakerDeviceId
        {
            get => _speakerDeviceId;
            set => Set(ref _speakerDeviceId, value);
        }

        [Category("Audio")]
        [DisplayName("Compensate for speaker volume")]
        [Description("Boost captured speaker audio to undo the system volume slider on devices that apply it in software (common for USB audio), so recordings are not quieter than what you heard. Devices with hardware volume control are left untouched.")]
        [HiddenOnMacOS]
        public bool SpeakerVolumeCompensation
        {
            get => _speakerVolumeCompensation;
            set => Set(ref _speakerVolumeCompensation, value);
        }

        [Category("Audio")]
        [DisplayName("Capture microphone")]
        [Description("Record microphone/input audio")]
        public bool CaptureMicrophone
        {
            get => _captureMicrophone;
            set => Set(ref _captureMicrophone, value);
        }

        [Category("Audio")]
        [DisplayName("Microphone device")]
        [Description("Microphone/input device to record")]
        [AudioDeviceSelector("microphone")]
        public string MicrophoneDeviceId
        {
            get => _microphoneDeviceId;
            set => Set(ref _microphoneDeviceId, value);
        }

        [Category("Audio")]
        [DisplayName("Separate audio tracks")]
        [Description("Write each recorded audio device to its own track in the file, so the video editor can trim and mix speakers and microphone independently. Turn it off to record a single track with everything mixed together.")]
        public bool SeparateAudioTracks
        {
            get => _separateAudioTracks;
            set => Set(ref _separateAudioTracks, value);
        }

        [Category("Webcam")]
        [DisplayName("Capture webcam")]
        [Description("Record a webcam alongside the screen as a second video track. Nothing is composited into the recording itself — the track is only shown once you open the recording in the video editor, where the overlay can be positioned, shaped or dropped entirely.")]
        public bool CaptureWebcam
        {
            get => _captureWebcam;
            set => Set(ref _captureWebcam, value);
        }

        [Category("Webcam")]
        [DisplayName("Webcam device")]
        [Description("Camera to record. Empty means no camera, which is also what an unplugged one falls back to.")]
        [CameraDeviceSelector]
        public string WebcamDeviceId
        {
            get => _webcamDeviceId;
            set => Set(ref _webcamDeviceId, value);
        }

        [Category("GIF")]
        [DisplayName("Quality")]
        [Description("Quality preset used when converting a recording to a GIF — higher quality means a higher frame rate and finer dithering, and a much larger file")]
        public GifQuality GifQuality
        {
            get => _gifQuality;
            set => Set(ref _gifQuality, value);
        }

        [Category("GIF")]
        [DisplayName("Max width")]
        [Description("Downscale the GIF so its width does not exceed this many pixels (0 = no limit). Aspect ratio is preserved and a recording is never upscaled; if a max height is set too, whichever limit is more restrictive wins.")]
        [Range(0, 16384)]
        public int GifMaxWidth
        {
            get => _gifMaxWidth;
            set => Set(ref _gifMaxWidth, value);
        }

        [Category("GIF")]
        [DisplayName("Max height")]
        [Description("Downscale the GIF so its height does not exceed this many pixels (0 = no limit). Aspect ratio is preserved and a recording is never upscaled; if a max width is set too, whichever limit is more restrictive wins.")]
        [Range(0, 16384)]
        public int GifMaxHeight
        {
            get => _gifMaxHeight;
            set => Set(ref _gifMaxHeight, value);
        }

        /// <summary>
        /// Where recordings go until the user picks a folder: the platform Videos/Movies folder,
        /// falling back to ~/Videos on platforms where the shell returns nothing for it (Linux
        /// without xdg-user-dirs). Used as the compiled-in default of <see cref="OutputDirectory"/>
        /// and as the fallback when the configured folder cannot be written to.
        /// </summary>
        public static string DefaultOutputDirectory
        {
            get
            {
                var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (!String.IsNullOrWhiteSpace(videos))
                    return videos;

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return String.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "Videos");
            }
        }

        private int _fps = 30;
        private VideoQuality _quality = VideoQuality.Medium;
        private int _maxResolutionWidth = 0;
        private int _maxResolutionHeight = 0;
        private bool _hardwareAccelerated = true;
        private bool _showMouseCursor = true;
        private bool _highlightClicks = true;
        private bool _captureSpeaker = false;
        private string _speakerDeviceId = "default";
        private bool _speakerVolumeCompensation = true;
        private bool _captureMicrophone = false;
        private string _microphoneDeviceId = "default";
        private bool _separateAudioTracks = true;
        private bool _captureWebcam = false;
        private string _webcamDeviceId = "";
        private string _outputDirectory = DefaultOutputDirectory;
        private string _filenamePattern = "yyyy-MM-dd HH-mm-ss";
        private RecordingFinishAction _openWhenFinished = RecordingFinishAction.VideoEditor;
        private GifQuality _gifQuality = GifQuality.Good;
        private int _gifMaxWidth = 0;
        private int _gifMaxHeight = 0;
    }
}
