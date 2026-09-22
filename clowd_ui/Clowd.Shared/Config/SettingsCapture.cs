using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Media;

namespace Clowd.Config
{
    /// <summary>Mirrors the capturer's --tips-mode flag (clowd_capture, see CAPTURE_PROTOCOL.md).</summary>
    public enum CapturerTipsMode
    {
        Hints,
        Tips,
        Off,
    }

    public class SettingsCapture : SimpleNotifyObject
    {
        /// <summary>
        /// Whether the capture overlay offers UPLOAD. The one switch in this section that is OFF
        /// by default: uploading a capture puts it on someone else's server, so it is opted into
        /// rather than out of. Every other switch here is on, because the point of those is
        /// trimming a strip that grew past what fits comfortably under a small selection, not
        /// shipping features off. Hides the button in both strips: the capture panel's UPLOAD and
        /// the OCR panel's, because someone who turned uploading off did not mean "except for
        /// text". Turning it off also takes the U accelerator with it, so a hidden button cannot
        /// still fire (clowd_capture PanelFeatures).
        ///
        /// RENAMED from <c>UploadButtonEnabled</c> to carry existing users to the new default.
        /// The settings file is written with every property spelled out (no
        /// DefaultIgnoreCondition in <see cref="SettingsService.CreateJsonOptions"/>), so every
        /// installed copy has <c>"UploadButtonEnabled": true</c> on disk and would have kept the
        /// button for ever. <see cref="SettingsService.Load"/> binds by property name onto an
        /// instance whose field initializers have already run, so the old key now matches nothing
        /// and is dropped, this property keeps its compiled-in <c>false</c>, and the stale key
        /// disappears from the file on the next save.
        ///
        /// Do NOT add back-compatible binding for the old name — that would undo the migration.
        /// The trick is one-shot per rename, and it resets a deliberate choice along with an
        /// untouched default, so it is only for a default worth moving everyone to.
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Upload")]
        [Description("Show the UPLOAD button in the capture window, which uploads the capture and " +
                     "copies its link to the clipboard")]
        [VisibleWhen(nameof(SettingsUpload.Mode), UploadsMode.On, Section = nameof(SettingsRoot.Uploads))]
        public bool UploadButtonVisible
        {
            get => _uploadButtonVisible;
            set => Set(ref _uploadButtonVisible, value);
        }

        /// <summary>
        /// Whether the capture overlay offers SHARE. Trims the button and its H accelerator only —
        /// the action itself stays reachable from the tray item and the Share Region hotkey, which
        /// dispatch it without ever raising the panel. Same division as
        /// <see cref="UploadButtonVisible"/>, which hides the strip's UPLOAD while the shell's
        /// "Upload File…" tray item stays (clowd_capture PanelFeatures, over <c>--no-share</c>).
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Share region")]
        [Description("Show the SHARE button in the capture window, which mirrors the selected region " +
                     "into a window a meeting app can share")]
        [VisibleWhen(nameof(SettingsShareRegion.Mode), ShareRegionMode.On, Section = nameof(SettingsRoot.ShareRegion))]
        public bool ShareRegionEnabled
        {
            get => _shareRegionEnabled;
            set => Set(ref _shareRegionEnabled, value);
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

        /// <summary>
        /// Whether the capture overlay offers SEARCH — a reverse image search of the selection.
        /// The shell hands the cropped image to the default browser, which posts it to Google Lens
        /// and lands on the results; nothing else reaches that action, so switching this off
        /// removes the feature rather than just trimming a button (clowd_capture PanelFeatures,
        /// over <c>--no-image-search</c>).
        /// </summary>
        [Category("Optional features")]
        [DisplayName("Reverse image search")]
        [Description("Show the SEARCH button in the capture window, which looks the selected image " +
                     "up with a reverse image search and opens the results in your browser")]
        public bool ImageSearchEnabled
        {
            get => _imageSearchEnabled;
            set => Set(ref _imageSearchEnabled, value);
        }

        /// <summary>
        /// Whether the shell keeps a capturer process waiting in the background
        /// (standby mode, CAPTURE_PROTOCOL.md) so the overlay opens without paying
        /// process/GPU startup on every capture. When off — or after the standby
        /// process crashes repeatedly, which overrides this to off for the rest of
        /// the shell's lifetime without touching the saved value — the shell
        /// registers the screenshot hotkeys itself and spawns a one-shot capturer
        /// per capture, exactly as before this setting existed.
        /// </summary>
        [Category("Behavior")]
        [DisplayName("Keep capturer warm")]
        [Description("Keep the capture overlay ready in the background so it opens with the lowest " +
                     "possible latency. Turn this off if it causes problems; captures then start " +
                     "the overlay on demand, which is slightly slower.")]
        public bool KeepCapturerWarm
        {
            get => _keepCapturerWarm;
            set => Set(ref _keepCapturerWarm, value);
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
        /// Whether a scrolling capture winds the target back to the top before it starts
        /// (<c>clowd_scroll_driver</c>, inverted onto its <c>--no-rewind</c> flag). On by default:
        /// someone who selects a region halfway down a page almost always wants the whole page,
        /// and capturing only the bottom half gives them no sign the top is missing. Turning it
        /// off is the "capture from here" intent — a long thread from one particular message.
        ///
        /// It describes how a capture behaves rather than which buttons the overlay offers, so it
        /// sits under Behavior while its parent switch stays with the other optional features; the
        /// [DisabledWhen] still grays it out across the two groups, since with scrolling capture
        /// off there is no scrolling capture for it to describe.
        /// </summary>
        [Category("Behavior")]
        [DisplayName("Scroll to top first")]
        [Description("Before a scrolling capture starts, wind the page back to the top so the whole " +
                     "document is captured. Turn this off to capture from wherever the page is sitting.")]
        [DisabledWhen(nameof(ScrollingCaptureEnabled), false)]
        public bool ScrollCaptureRewindToTop
        {
            get => _scrollCaptureRewindToTop;
            set => Set(ref _scrollCaptureRewindToTop, value);
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

        private string _filenamePattern = DefaultFilenamePattern;
        private bool _keepCapturerWarm = true;
        private bool _screenshotWithCursor = false;
        private bool _detectWindows = true;
        private CapturerTipsMode _tipsMode = CapturerTipsMode.Hints;
        private bool _obscuredWindowPeek = true;
        private bool _roundedWindowCorners = true;
        // Off by default, for everyone — see UploadButtonVisible, whose rename is what
        // carries existing users onto this default.
        private bool _uploadButtonVisible = false;
        private bool _shareRegionEnabled = true;
        private bool _scrollingCaptureEnabled = true;
        private bool _scrollCaptureRewindToTop = true;
        private bool _ocrEnabled = true;
        private bool _imageSearchEnabled = true;
        private double _obscuredWindowDetectionThreshold = 0.80;
        private bool _openSavedInExplorer = true;
    }
}
