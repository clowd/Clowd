using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Media;

namespace Clowd.Config
{
    /// <summary>Mirrors the capturer's --tips-mode flag (clowd_capture_wgpu, see CAPTURE_PROTOCOL.md).</summary>
    public enum CapturerTipsMode
    {
        Hints,
        Tips,
        Off,
    }

    /// <summary>Mirrors the capturer's --memory-hints flag (clowd_capture_wgpu, see CAPTURE_PROTOCOL.md).</summary>
    public enum CapturerMemoryHints
    {
        LowerMemoryUsage,
        MaxPerformance,
    }

    public class SettingsCapture : SimpleNotifyObject
    {
        [Category("Behavior")]
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

        /// <summary>
        /// Keep a fully warmed-up capture process resident so the overlay appears the moment the
        /// hotkey is pressed instead of paying for GPU initialization every time. Turning it off is
        /// the escape hatch for the idle memory/VRAM cost it buys that with; captures then take the
        /// original one-process-per-capture path (see ScreenCaptureService).
        /// </summary>
        [Category("Performance")]
        [DisplayName("Fast capture startup")]
        [Description("Keep the capture tool warmed up in the background so the capture overlay opens instantly. " +
                     "Costs a small amount of memory while idle.")]
        public bool KeepCaptureReady
        {
            get => _keepCaptureReady;
            set => Set(ref _keepCaptureReady, value);
        }

        /// <summary>
        /// The capture tool's GPU allocator strategy (<c>--memory-hints</c>). LowerMemoryUsage is
        /// the right trade for nearly everyone — it is what keeps the warmed-up background process
        /// small while idle. Read once at process launch, so CaptureProcessHost relaunches the
        /// warm host when this changes.
        /// </summary>
        [Category("Performance")]
        [DisplayName("GPU memory usage")]
        [Description("How the capture tool budgets GPU memory. Lower memory usage is recommended; " +
                     "Max performance uses larger GPU memory blocks, which costs extra idle memory " +
                     "when the capture tool runs in the background.")]
        public CapturerMemoryHints MemoryHints
        {
            get => _memoryHints;
            set => Set(ref _memoryHints, value);
        }

        [Category("Behavior")]
        [DisplayName("Tips overlay")]
        [Description("Which tips/hints overlay the capture window shows at startup (cycled at runtime with T)")]
        public CapturerTipsMode TipsMode
        {
            get => _tipsMode;
            set => Set(ref _tipsMode, value);
        }

        [Category("Behavior")]
        [DisplayName("Obscured window peek")]
        [Description("Capture obstructed windows and show a peek-through composite when hovering them")]
        public bool ObscuredWindowPeek
        {
            get => _obscuredWindowPeek;
            set => Set(ref _obscuredWindowPeek, value);
        }

        [Category("Behavior")]
        [DisplayName("Obscured window threshold")]
        [Description("How much of a window may be covered by other windows before it can no longer be selected")]
        [Range(0.0, 1.0)]
        public double ObscuredWindowDetectionThreshold
        {
            get => _obscuredWindowDetectionThreshold;
            set => Set(ref _obscuredWindowDetectionThreshold, value);
        }

        /// <summary>
        /// Follow the OS accent colour instead of <see cref="AccentColor"/>. Reads as false wherever
        /// there is no system accent to read (macOS), so the row it disables cannot get stuck greyed
        /// out on a platform that hides this checkbox.
        /// </summary>
        [Category("Appearance")]
        [DisplayName("Use system accent color")]
        [Description("Draw the capture overlay in the accent color chosen in Windows settings")]
        [HiddenOnMacOS]
        public bool UseSystemAccentColor
        {
            get => _useSystemAccentColor && AccentColors.SystemAccentSupported;
            set => Set(ref _useSystemAccentColor, value);
        }

        /// <summary>
        /// The manually chosen overlay accent. Normalised on assignment so the swatch in the
        /// settings page shows exactly the colour the overlay will be drawn in — a colour too light
        /// to carry white text is darkened (issue #48).
        /// </summary>
        [Category("Appearance")]
        [DisplayName("Accent color")]
        [Description("Color of the crosshair, selection border and primary buttons in the capture overlay. " +
                     "A color too light to carry the white button labels is darkened until it is readable.")]
        [DisabledWhen(nameof(UseSystemAccentColor))]
        public Color AccentColor
        {
            get => _accentColor;
            set => Set(ref _accentColor, AccentColors.EnsureContrastWithWhite(value));
        }

        [Category("Saving")]
        [DisplayName("Open saved files in Explorer")]
        [Description("Reveal the file in Explorer after a capture is saved to disk")]
        public bool OpenSavedInExplorer
        {
            get => _openSavedInExplorer;
            set => Set(ref _openSavedInExplorer, value);
        }

        [Category("Saving")]
        [DisplayName("Filename pattern")]
        [Description("Date format used to name saved captures and uploads (.NET date format string)")]
        public string FilenamePattern
        {
            get => _filenamePattern;
            set => Set(ref _filenamePattern, value);
        }

        /// <summary>
        /// The colour the capture overlay is actually launched with (<c>--accent-color</c>): the OS
        /// accent when the user asked for it and there is one to read, otherwise their own choice —
        /// in both cases dark enough for the white text drawn on top of it.
        /// </summary>
        public Color GetEffectiveAccentColor()
        {
            var color = (UseSystemAccentColor ? AccentColors.GetSystemAccent() : null) ?? AccentColor;
            return AccentColors.EnsureContrastWithWhite(color);
        }

        private string _filenamePattern = "yyyy-MM-dd HH-mm-ss";
        private bool _screenshotWithCursor = true;
        private bool _keepCaptureReady = true;
        private CapturerMemoryHints _memoryHints = CapturerMemoryHints.LowerMemoryUsage;
        private bool _detectWindows = true;
        private CapturerTipsMode _tipsMode = CapturerTipsMode.Hints;
        private bool _obscuredWindowPeek = true;
        private double _obscuredWindowDetectionThreshold = 0.80;
        private bool _openSavedInExplorer = true;
        private bool _useSystemAccentColor = true;
        private Color _accentColor = AccentColors.Default;
    }
}
