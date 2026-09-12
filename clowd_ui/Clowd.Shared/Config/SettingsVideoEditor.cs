using System.ComponentModel;

namespace Clowd.Config
{
    /// <summary>
    /// What the video editor's Render flyout offered last time, remembered so the next render is
    /// one keystroke. The three named presets are the flyout's rows (their CRF and size cap live
    /// in the UI's <c>RenderPresets</c> table); <see cref="RenderPreset.Custom"/> means the user
    /// last rendered from the "Render video" dialog, and the values they chose there are in
    /// <see cref="SettingsVideoEditor.CustomRenderCrf"/> and
    /// <see cref="SettingsVideoEditor.CustomRenderMaxHeight"/>.
    /// </summary>
    public enum RenderPreset
    {
        /// <summary>Plays everywhere, sensible size (CRF 23, no size cap).</summary>
        Share,

        /// <summary>For YouTube or keeping a master (CRF 18, no size cap).</summary>
        BestQuality,

        /// <summary>Fits chat upload limits (CRF 29, capped to 720p).</summary>
        SmallFile,

        /// <summary>Whatever was last set in the render dialog.</summary>
        Custom,
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

        private double _sidebarWidth = 230;
        private string _windowBounds;
        private bool _windowMaximized;
        private RenderPreset _lastRenderPreset = RenderPreset.Share;
        // the Share preset's CRF: the dialog opens on the same quality the flyout's default row
        // would have rendered with, until the user changes it.
        private int _customRenderCrf = (int)VideoQuality.Medium;
        private int _customRenderMaxHeight;
        private bool _copyToClipboardAfterRender;
        private bool _showInFolderAfterRender;
        private bool _hardwareEncodeRender;
    }
}
