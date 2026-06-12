using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>Mirrors the capturer's --tips-mode flag (clowd_capture_wgpu, see CAPTURE_PROTOCOL.md).</summary>
    public enum CapturerTipsMode
    {
        Hints,
        Tips,
        Off,
    }

    public class SettingsCapture : CategoryBase
    {
        [DisplayName("Capture with cursor")]
        [Description("If this is enabled, the cursor will be shown in screenshots")]
        public bool ScreenshotWithCursor
        {
            get => _screenshotWithCursor;
            set => Set(ref _screenshotWithCursor, value);
        }

        [Browsable(false)]
        [Description("If this is true, the Capture window will try to detect and highlight different windows as you hover over them.")]
        public bool DetectWindows
        {
            get => _detectWindows;
            set => Set(ref _detectWindows, value);
        }

        [DisplayName("Tips overlay")]
        [Description("Which tips/hints overlay the capture window shows at startup (cycled at runtime with T)")]
        public CapturerTipsMode TipsMode
        {
            get => _tipsMode;
            set => Set(ref _tipsMode, value);
        }

        [DisplayName("Obscured window peek")]
        [Description("Capture obstructed windows and show a peek-through composite when hovering them")]
        public bool ObscuredWindowPeek
        {
            get => _obscuredWindowPeek;
            set => Set(ref _obscuredWindowPeek, value);
        }

        [DisplayName("Obscured window threshold")]
        [Description("Maximum fraction (0.0 - 1.0) of a window's area that may be covered by other windows before it can no longer be selected")]
        public double ObscuredWindowDetectionThreshold
        {
            get => _obscuredWindowDetectionThreshold;
            set => Set(ref _obscuredWindowDetectionThreshold, value);
        }

        public bool OpenSavedInExplorer
        {
            get => _openSavedInExplorer;
            set => Set(ref _openSavedInExplorer, value);
        }

        public string FilenamePattern
        {
            get => _filenamePattern;
            set => Set(ref _filenamePattern, value);
        }

        private string _filenamePattern = "yyyy-MM-dd HH-mm-ss";
        private bool _screenshotWithCursor = true;
        private bool _detectWindows = true;
        private CapturerTipsMode _tipsMode = CapturerTipsMode.Hints;
        private bool _obscuredWindowPeek = true;
        private double _obscuredWindowDetectionThreshold = 0.80;
        private bool _openSavedInExplorer = true;
    }
}
