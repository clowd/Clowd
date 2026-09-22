using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// What the video editor's Render flyout offered last time, remembered so the next render is
    /// one keystroke. The three named presets are the flyout's rows (their CRF and size cap live
    /// in the UI's <c>RenderPresets</c> table); <see cref="RenderPreset.Custom"/> means the user
    /// last rendered from the "Render video" dialog, and the values they chose there are in
    /// <see cref="SettingsVideoEditor.CustomRenderCrf"/>,
    /// <see cref="SettingsVideoEditor.CustomRenderMaxHeight"/> and
    /// <see cref="SettingsVideoEditor.CustomRenderMaxFps"/>.
    /// </summary>
    public enum RenderPreset
    {
        /// <summary>Plays everywhere, sensible size (CRF 23, capped to 1080p and 60 fps).</summary>
        Share,

        /// <summary>For YouTube or keeping a master (CRF 18, no size or frame-rate cap).</summary>
        BestQuality,

        /// <summary>Fits chat upload limits (CRF 29, capped to 720p and 30 fps).</summary>
        SmallFile,

        /// <summary>Whatever was last set in the render dialog.</summary>
        Custom,

        /// <summary>One of the user's own presets, named by
        /// <see cref="SettingsVideoEditor.LastUserRenderPresetId"/>.</summary>
        User,
    }

    /// <summary>
    /// A render preset the user saved from the "Render video" dialog ("Save current settings as
    /// new preset"): quality, size and frame-rate caps and encoder, and the after-render actions ticked with them —
    /// "Delete session" included, so a preset made with it closes the editor and deletes the
    /// session on every successful one-click render.
    /// </summary>
    public class RenderUserPreset
    {
        /// <summary>Stable identity, so renaming or re-saving a preset never confuses the flyout's
        /// "last used" check mark with another one.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; }

        /// <summary>x264 CRF, 0-51.</summary>
        public int Crf { get; set; }

        /// <summary>Encode-time height cap, 0 for none.</summary>
        public int MaxHeight { get; set; }

        /// <summary>Frame-rate cap in whole frames per second, 0 for none (the project's fastest
        /// clip). A preset saved before the field existed reads 0, which is what it rendered at.</summary>
        public int MaxFps { get; set; }

        public bool HardwareEncoder { get; set; }

        public bool CopyToClipboard { get; set; }

        public bool ShowInFolder { get; set; }

        public bool DeleteSession { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>
    /// Remembered view state for the video editor window. Everything here is
    /// <see cref="BrowsableAttribute">[Browsable(false)]</see>: none of it is a preference the user
    /// sets on the settings page, it is just what the window looked like last time (the same deal
    /// as <see cref="SettingsEditor.SidebarWidth"/> and
    /// <see cref="SettingsGeneral.MainWindowBounds"/>).
    /// </summary>
    public class SettingsVideoEditor : SimpleNotifyObject
    {
        /// <summary>Width of the properties sidebar; matches the image editor's default.</summary>
        [Browsable(false)]
        public double SidebarWidth
        {
            get => _sidebarWidth;
            set => Set(ref _sidebarWidth, value);
        }

        /// <summary>Last window placement as "x,y,width,height" (physical pixels), restored on open
        /// when it still intersects a connected screen — the exact format and semantics of
        /// <see cref="SettingsGeneral.MainWindowBounds"/>. Null until the window has been opened once.</summary>
        [Browsable(false)]
        public string WindowBounds
        {
            get => _windowBounds;
            set => Set(ref _windowBounds, value);
        }

        /// <summary>Whether the window was maximized when it was last closed. Tracked separately
        /// from <see cref="WindowBounds"/>, which always holds the *restored* placement.</summary>
        [Browsable(false)]
        public bool WindowMaximized
        {
            get => _windowMaximized;
            set => Set(ref _windowMaximized, value);
        }

        /// <summary>The preset the Render flyout starts on — the row that is checked and focused
        /// when it opens, so Enter repeats the last render. <see cref="RenderPreset.Custom"/> when
        /// the last render came from the dialog, in which case the flyout focuses "More options…"
        /// instead (there is no row for a custom render).</summary>
        [Browsable(false)]
        public RenderPreset LastRenderPreset
        {
            get => _lastRenderPreset;
            set => Set(ref _lastRenderPreset, value);
        }

        /// <summary>The x264 CRF the render dialog was last rendered with (0-51, lower is better).
        /// Prefills the dialog's Quality row; only meaningful alongside
        /// <see cref="LastRenderPreset"/> = <see cref="RenderPreset.Custom"/>, but it is kept
        /// whatever the last render was so the dialog reopens where the user left it.</summary>
        [Browsable(false)]
        public int CustomRenderCrf
        {
            get => _customRenderCrf;
            set => Set(ref _customRenderCrf, value);
        }

        /// <summary>The encode-time cap on the output height the render dialog was last rendered
        /// with, 0 for none (the project's own canvas height). The project is never touched: this
        /// is only what the encoder is opened at.</summary>
        [Browsable(false)]
        public int CustomRenderMaxHeight
        {
            get => _customRenderMaxHeight;
            set => Set(ref _customRenderMaxHeight, value);
        }

        /// <summary>The frame-rate cap the render dialog was last rendered with, in whole frames per
        /// second, 0 for none ("Actual": the fastest clip in the project). Like the size cap it
        /// never raises a rate — see <c>RenderFrameRate.Resolve</c>.</summary>
        [Browsable(false)]
        public int CustomRenderMaxFps
        {
            get => _customRenderMaxFps;
            set => Set(ref _customRenderMaxFps, value);
        }

        /// <summary>Put the finished video on the clipboard as a file (so a paste into Explorer,
        /// Discord, Slack or Teams attaches it). Off by default — a render should not take the
        /// clipboard unless the user asked for it.</summary>
        [Browsable(false)]
        public bool CopyToClipboardAfterRender
        {
            get => _copyToClipboardAfterRender;
            set => Set(ref _copyToClipboardAfterRender, value);
        }

        /// <summary>Reveal the finished video in the file manager.</summary>
        [Browsable(false)]
        public bool ShowInFolderAfterRender
        {
            get => _showInFolderAfterRender;
            set => Set(ref _showInFolderAfterRender, value);
        }

        /// <summary>Render with the GPU encoder when one opens (the dialog's "Use hardware encoder").
        /// Off by default: x264 is faster on a desktop CPU and its file smaller at the same quality
        /// setting, so the GPU path is an opt-in for machines where the CPU is the bottleneck.</summary>
        [Browsable(false)]
        public bool HardwareEncodeRender
        {
            get => _hardwareEncodeRender;
            set => Set(ref _hardwareEncodeRender, value);
        }

        /// <summary>The user's own presets, in the order they were saved; the Render flyout lists
        /// them under the built-in rows. Replace the list rather than mutating it, so the change is
        /// raised.</summary>
        [Browsable(false)]
        public List<RenderUserPreset> RenderUserPresets
        {
            get => _renderUserPresets;
            set => Set(ref _renderUserPresets, value ?? new List<RenderUserPreset>());
        }

        /// <summary>Which of <see cref="RenderUserPresets"/> the last render used, when
        /// <see cref="LastRenderPreset"/> is <see cref="RenderPreset.User"/>.</summary>
        [Browsable(false)]
        public string LastUserRenderPresetId
        {
            get => _lastUserRenderPresetId;
            set => Set(ref _lastUserRenderPresetId, value);
        }

        /// <summary>The last size entered in the aspect picker's "Custom…" dialog, offered as the
        /// picker's second row in every project from then on. 0 until one has been entered.</summary>
        [Browsable(false)]
        public int CustomOutputWidthPx
        {
            get => _customOutputWidthPx;
            set => Set(ref _customOutputWidthPx, value);
        }

        /// <summary>See <see cref="CustomOutputWidthPx"/>.</summary>
        [Browsable(false)]
        public int CustomOutputHeightPx
        {
            get => _customOutputHeightPx;
            set => Set(ref _customOutputHeightPx, value);
        }

        private double _sidebarWidth = 230;
        private string _windowBounds;
        private bool _windowMaximized;
        private RenderPreset _lastRenderPreset = RenderPreset.Share;
        // the Share preset's CRF: the dialog opens on the same quality the flyout's default row
        // would have rendered with, until the user changes it.
        private int _customRenderCrf = (int)VideoQuality.Medium;
        private int _customRenderMaxHeight;
        private int _customRenderMaxFps;
        private bool _copyToClipboardAfterRender;
        private bool _showInFolderAfterRender;
        private bool _hardwareEncodeRender;
        private List<RenderUserPreset> _renderUserPresets = new List<RenderUserPreset>();
        private string _lastUserRenderPresetId;
        private int _customOutputWidthPx;
        private int _customOutputHeightPx;
    }
}
