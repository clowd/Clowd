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

    public class SettingsCapture : SimpleNotifyObject
    {
        /// <summary>
        /// Whether the capture overlay offers UPLOAD. On by default like every switch in this
        /// section — the point is trimming a strip that grew past what fits comfortably under a
        /// small selection, not shipping features off. Hides the button in both strips: the
        /// capture panel's UPLOAD and the OCR panel's, because someone who turned uploading off
        /// did not mean "except for text". Turning it off also takes the U accelerator with it,
        /// so a hidden button cannot still fire (clowd_capture PanelFeatures).
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Upload")]
        [Description("Show the UPLOAD button in the capture window, which uploads the capture and " +
                     "copies its link to the clipboard")]
        public bool UploadButtonEnabled
        {
            get => _uploadButtonEnabled;
            set => Set(ref _uploadButtonEnabled, value);
        }

        /// <summary>
        /// Whether the capture overlay offers SCROLL. On both platforms: the driver
        /// (<c>clowd_scroll_driver</c>) has a Win32 and a macOS backend, and the overlay shows the
        /// button wherever this is on.
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Scrolling capture")]
        [Description("Show the SCROLL button in the capture window, which captures a whole scrolling " +
                     "page by scrolling it and stitching the frames together")]
        public bool ScrollingCaptureEnabled
        {
            get => _scrollingCaptureEnabled;
            set => Set(ref _scrollingCaptureEnabled, value);
        }

        /// <summary>
        /// Whether a scrolling capture winds the target back to the top before it starts
        /// (<c>clowd_scroll_driver</c>, inverted onto its <c>--no-rewind</c> flag). On by default:
        /// someone who selects a region halfway down a page almost always wants the whole page,
        /// and capturing only the bottom half gives them no sign the top is missing. Turning it
        /// off is the "capture from here" intent — a long thread from one particular message.
        ///
        /// Lives beside its parent switch and grays out with it: with scrolling capture off there
        /// is no scrolling capture for it to describe.
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Scroll to top first")]
        [Description("Before a scrolling capture starts, wind the page back to the top so the whole " +
                     "document is captured. Turn this off to capture from wherever the page is sitting.")]
        [DisabledWhen(nameof(ScrollingCaptureEnabled), false)]
        public bool ScrollCaptureRewindToTop
        {
            get => _scrollCaptureRewindToTop;
            set => Set(ref _scrollCaptureRewindToTop, value);
        }

        /// <summary>
        /// Whether the capture overlay offers OCR. Switching it off makes the whole OCR flow
        /// unreachable — the OCR button is the only way into the mode that raises the
        /// UPLOAD/SEARCH/COPY strip.
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Text recognition (OCR)")]
        [Description("Show the OCR button in the capture window, which lifts the text out of the " +
                     "selection so it can be copied, searched or uploaded")]
        public bool OcrEnabled
        {
            get => _ocrEnabled;
            set => Set(ref _ocrEnabled, value);
        }

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

        [Category("Behavior")]
        [DisplayName("Tips overlay")]
        [Description("Which tips/hints overlay the capture window shows at startup (cycled at runtime with T)")]
        public CapturerTipsMode TipsMode
        {
            get => _tipsMode;
            set => Set(ref _tipsMode, value);
        }

        /// <summary>
        /// Whether a selection made by picking a window (hover and click, <c>W</c>, the
        /// capture-window hotkey) takes on that window's OS corner radius — rounded dashed
        /// border in the overlay, transparent corners in the copied / saved / uploaded image
        /// — instead of a sharp rectangle that ships a few pixels of whatever sat behind the
        /// window. Dragged selections stay square either way (clowd_capture
        /// <c>--no-rounded-corners</c>). On by default: it is what the screen actually shows.
        /// </summary>
        [Category("Behavior")]
        [DisplayName("Rounded window corners")]
        [Description("When a window is selected, match its rounded corners: the selection border " +
                     "follows the window's corner radius and the corners are transparent in the " +
                     "copied or saved image. Dragged selections are always square.")]
        public bool RoundedWindowCorners
        {
            get => _roundedWindowCorners;
            set => Set(ref _roundedWindowCorners, value);
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
        /// Follow the OS accent color instead of <see cref="AccentColor"/>. Reads as false wherever
        /// there is no system accent to read (macOS), so the row it disables cannot get stuck grayed
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
        /// The manually chosen overlay accent. Normalized on assignment so the swatch in the
        /// settings page shows exactly the color the overlay will be drawn in — a color too light
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

        /// <summary>
        /// Applied when the setting is left blank, and the value the capture overlay already
        /// defaults to — so the shell only spends a <c>--filename-pattern</c> argument on a
        /// pattern the user actually changed.
        /// </summary>
        public const string DefaultFilenamePattern = "yyyy-MM-dd HH-mm-ss";

        [Category("Saving")]
        [DisplayName("Filename pattern")]
        [Description("Date format used to name saved captures and uploads (.NET date format string)")]
        public string FilenamePattern
        {
            get => _filenamePattern;
            set => Set(ref _filenamePattern, value);
        }

        /// <summary>
        /// The color the capture overlay is actually launched with (<c>--accent-color</c>): the OS
        /// accent when the user asked for it and there is one to read, otherwise their own choice —
        /// in both cases dark enough for the white text drawn on top of it.
        /// </summary>
        public Color GetEffectiveAccentColor()
        {
            var color = (UseSystemAccentColor ? AccentColors.GetSystemAccent() : null) ?? AccentColor;
            return AccentColors.EnsureContrastWithWhite(color);
        }

        private string _filenamePattern = DefaultFilenamePattern;
        private bool _screenshotWithCursor = true;
        private bool _detectWindows = true;
        private CapturerTipsMode _tipsMode = CapturerTipsMode.Hints;
        private bool _obscuredWindowPeek = true;
        private bool _roundedWindowCorners = true;
        private bool _uploadButtonEnabled = true;
        private bool _scrollingCaptureEnabled = true;
        private bool _scrollCaptureRewindToTop = true;
        private bool _ocrEnabled = true;
        private double _obscuredWindowDetectionThreshold = 0.80;
        private bool _openSavedInExplorer = true;
        private bool _useSystemAccentColor = true;
        private Color _accentColor = AccentColors.Default;
    }
}
