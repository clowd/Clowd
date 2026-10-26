using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;

namespace Clowd.Config
{
    public enum AppTheme
    {
        [Description("Follow system")]
        System,

        [Description("Light")]
        Light,

        [Description("Dark")]
        Dark,
    }

    /// <summary>What a click does — shared by the tray icon and by "launching" Clowd while it is
    /// already running (<see cref="SettingsGeneral.TrayClick"/> / <see cref="SettingsGeneral.ShortcutClick"/>).
    /// The member names are what lands in the settings file, so they stay as they were first
    /// written even where the label has since moved on.</summary>
    public enum ClickAction
    {
        [Description("Open main window")]
        OpenSettings,

        [Description("Start capture")]
        CaptureRegion,

        [Description("Do nothing")]
        DoNothing,
    }

    /// <summary>Which of Clowd's own windows are kept out of screen capture (screenshots, recordings,
    /// meeting shares). Each step adds to the one before it, so the order is the point: a window is
    /// hidden when its kind's step is at or below the chosen one (<c>CaptureExclusion</c>). The
    /// member names are what lands in the settings file.</summary>
    public enum HideFromCapture
    {
        [Description("Hide none")]
        None,

        [Description("Tooltips")]
        Tooltips,

        [Description("Toolbars")]
        Toolbars,

        [Description("All windows")]
        AllWindows,
    }

    /// <summary>How often Clowd polls the release feed while running. Values are minutes — the
    /// update scheduler converts them straight to a <see cref="System.TimeSpan"/>.</summary>
    public enum UpdateInterval
    {
        [Description("Every 30 minutes")]
        HalfHourly = 30,

        [Description("Every hour")]
        Hourly = 60,

        [Description("Every 3 hours")]
        ThreeHourly = 180,

        [Description("Every 6 hours")]
        SixHourly = 360,

        [Description("Every 12 hours")]
        TwelveHourly = 720,

        [Description("Once a day")]
        Daily = 1440,
    }

    public class SettingsGeneral : SimpleNotifyObject
    {
        /// <summary>Number of custom colors remembered by the color pickers. 12 is one full row
        /// of 16px swatches across the 192px mini picker.</summary>
        public const int MaxRecentColors = 12;

        /// <summary>Most-recently-used custom colors, newest first, shown as an extra swatch row
        /// in both color pickers. Written by <c>RecentColorHistory</c>.</summary>
        [Browsable(false)]
        public List<Color> RecentColors
        {
            get => _recentColors;
            set => Set(ref _recentColors, value);
        }


        /// <summary>The draw-on-screen toolbar's last colour and size, as indices into its fixed
        /// palette (<c>DrawPalette</c>), so the next session opens with the same brush. Null until
        /// one is first picked; out-of-range values are clamped by the reader.</summary>
        [Browsable(false)]
        public int? DrawOnScreenColorIndex
        {
            get => _drawOnScreenColorIndex;
            set => Set(ref _drawOnScreenColorIndex, value);
        }

        [Browsable(false)]
        public int? DrawOnScreenSizeIndex
        {
            get => _drawOnScreenSizeIndex;
            set => Set(ref _drawOnScreenSizeIndex, value);
        }

        [Browsable(false)]
        public string LastSavePath
        {
            get => _lastSavePath;
            set => Set(ref _lastSavePath, value);
        }

        /// <summary>Last main-window placement as "x,y,width,height" (physical pixels); restored
        /// on open when it still intersects a connected screen.</summary>
        [Browsable(false)]
        public string MainWindowBounds
        {
            get => _mainWindowBounds;
            set => Set(ref _mainWindowBounds, value);
        }

        /// <summary>
        /// Default for <see cref="RegisterAutoStart"/>.
        /// On Windows the Velopack install hook registers the login item at install time, so a fresh
        /// install is already auto-starting; elsewhere the user has to opt in. Debug builds are never
        /// installed, so they don't default to registering their bin directory to run at login.
        /// </summary>
        public static bool DefaultRegisterAutoStart { get; } =
#if DEBUG
            false;
#else
            OperatingSystem.IsWindows();
#endif

        [DisplayName("Start Prisc when your computer starts up")]
        [Description("Prisc starts quietly in the notification area when you log in.")]
        public bool RegisterAutoStart
        {
            get => _registerAutoStart;
            set => Set(ref _registerAutoStart, value);
        }

        /// <summary>
        /// Default for <see cref="RegisterExplorerContextMenu"/>. Windows-only, and never in debug
        /// builds — which are not installed, so registering a verb pointing into a bin directory
        /// would put a stale entry in the developer's own Explorer menu.
        /// </summary>
        public static bool DefaultRegisterExplorerContextMenu { get; } =
#if DEBUG
            false;
#else
            OperatingSystem.IsWindows();
#endif

        [DisplayName("Add 'Upload with Prisc' to the Explorer context menu")]
        [Description("Adds a right-click entry that uploads files and folders with Prisc.")]
        public bool RegisterExplorerContextMenu
        {
            get => _registerExplorerContextMenu;
            set => Set(ref _registerExplorerContextMenu, value);
        }

        [DisplayName("Automatically check for and download updates")]
        [Description("Look for new releases while Prisc is running and download them in the background.")]
        public bool AutoDownloadUpdates
        {
            get => _autoDownloadUpdates;
            set => Set(ref _autoDownloadUpdates, value);
        }

        [DisplayName("Check for updates")]
        [Description("How often Prisc looks for a newer release.")]
        public UpdateInterval UpdateCheckInterval
        {
            get => _updateCheckInterval;
            set => Set(ref _updateCheckInterval, value);
        }

        [DisplayName("Automatically restart Prisc to apply updates in the background")]
        [Description("Only restarts after 10 minutes of inactivity, never during a capture, recording or upload.")]
        public bool AutoApplyUpdates
        {
            get => _autoApplyUpdates;
            set => Set(ref _autoApplyUpdates, value);
        }

        /// <summary>
        /// Opt-in to installing pre-release builds. Everyone follows the same release channel;
        /// this only widens the update feed to also include releases still flagged as
        /// pre-releases on GitHub (see UpdateService), so the newest release wins either way.
        /// </summary>
        [DisplayName("Opt in to experimental builds")]
        [Description("Experimental builds get new features first but may have more bugs.")]
        public bool IncludePrereleaseUpdates
        {
            get => _includePrereleaseUpdates;
            set => Set(ref _includePrereleaseUpdates, value);
        }

        /// <summary>
        /// UI language as a culture name ("de", "fr-CA"). Null or empty means "follow the OS", which
        /// is the state of every install until the user picks a language; a name with no matching
        /// satellite assembly also falls back to the OS language (see Loc.ApplyCulture).
        ///
        /// Not browsable: the settings-control factory would render a text box. The System page
        /// hosts a hand-built combo populated from Loc.GetAvailableLanguages().
        /// </summary>
        [Browsable(false)]
        public string Language
        {
            get => _language;
            set => Set(ref _language, value);
        }

        [DisplayName("Theme")]
        [Description("Choose light or dark, or follow the system setting.")]
        public AppTheme Theme
        {
            get => _theme;
            set => Set(ref _theme, value);
        }

        /// <summary>
        /// macOS only: run window content up under a transparent title bar, so the traffic lights
        /// float over it, instead of sitting below a title bar of its own. Read once per window in
        /// SystemThemedWindow's constructor, which is why the description promises new windows
        /// rather than an instant change; the main window is reopened on the spot so the choice is
        /// visible, and anything already open keeps its own look until the user closes it.
        /// </summary>
        [HiddenOnWindows]
        [DisplayName("Extend windows into the title bar")]
        [Description("Run window content under a transparent title bar, like most modern Mac apps.")]
        public bool ExtendIntoTitleBar
        {
            get => _extendIntoTitleBar;
            set => Set(ref _extendIntoTitleBar, value);
        }

        /// <summary>
        /// Follow the OS accent color instead of <see cref="AccentColor"/>. Reads as false wherever
        /// there is no system accent to read (macOS), so the row it disables cannot get stuck grayed
        /// out on a platform that hides this checkbox.
        /// </summary>
        [DisplayName("Use system accent color")]
        [Description("Use the accent color chosen in Windows settings.")]
        [HiddenOnMacOS]
        public bool UseSystemAccentColor
        {
            get => _useSystemAccentColor && AccentColors.SystemAccentSupported;
            set => Set(ref _useSystemAccentColor, value);
        }

        /// <summary>
        /// The manually chosen accent. Stored exactly as picked: <see cref="MaintainMinimumContrast"/>
        /// can be turned off and on again, and darkening on assignment would have thrown the
        /// original away the first time it was on. The correction happens at the point of use, in
        /// <see cref="GetEffectiveAccentColor"/>.
        /// </summary>
        [DisplayName("Accent color")]
        [Description("Colors the capture overlay, the recording toolbar and highlighted controls.")]
        [DisabledWhen(nameof(UseSystemAccentColor))]
        public Color AccentColor
        {
            get => _accentColor;
            set => Set(ref _accentColor, value);
        }

        /// <summary>
        /// Whether the accent is darkened until white text on it is readable (WCAG AA, 4.5:1 — see
        /// <see cref="AccentColors.EnsureContrastWithWhite"/>). On by default and worth leaving on:
        /// every surface this color fills carries white labels and glyphs directly on top of it, and
        /// a light accent leaves them unreadable (issue #48). Off is for someone who wants their
        /// exact color and has decided they can live with that.
        /// </summary>
        [DisplayName("Maintain minimum contrast")]
        [Description("Darken the accent color so white labels on it stay readable.")]
        public bool MaintainMinimumContrast
        {
            get => _maintainMinimumContrast;
            set => Set(ref _maintainMinimumContrast, value);
        }

        /// <summary>
        /// The color the capture surfaces are actually drawn in: the OS accent when the user asked
        /// for it and there is one to read, otherwise their own choice — darkened for legibility
        /// unless <see cref="MaintainMinimumContrast"/> says not to. This is what
        /// <c>AppStyles.CaptureAccentColor</c> paints the recording toolbar and border with; the
        /// overlay is launched with the pick (<see cref="GetPickedAccentColor"/>, as
        /// <c>--accent-color</c>, plus <c>--no-accent-contrast</c> when the correction is off) and
        /// makes the same correction itself, so all of them agree.
        /// </summary>
        public Color GetEffectiveAccentColor()
        {
            var color = GetPickedAccentColor();
            return MaintainMinimumContrast ? AccentColors.EnsureContrastWithWhite(color) : color;
        }

        /// <summary>
        /// The accent as chosen — the OS accent when the user asked for it and there is one to read,
        /// otherwise their own pick — before any contrast correction. For surfaces that carry no white
        /// ink and want the color itself rather than a fill dark enough to write on, such as the
        /// floating strips' glows and the capturer's hint comet (<see cref="AccentColors.CometColors"/>).
        /// </summary>
        public Color GetPickedAccentColor()
            => (UseSystemAccentColor ? AccentColors.GetSystemAccent() : null) ?? AccentColor;

        [DisplayName("Tray icon click")]
        [Description("What a single click on the tray icon does.")]
        public ClickAction TrayClick
        {
            get => _trayClick;
            set => Set(ref _trayClick, value);
        }

        [DisplayName("Shortcut click")]
        [Description("What launching Prisc from a shortcut, the taskbar or the dock does.")]
        public ClickAction ShortcutClick
        {
            get => _shortcutClick;
            set => Set(ref _shortcutClick, value);
        }

        /// <summary>
        /// Which Clowd windows screen capture cannot see, applied live to every open window by
        /// <c>CaptureExclusion</c>. Tooltips by default: the tips and the region border are
        /// transient chrome nobody wants in a recording, while the toolbars stay visible unless asked.
        /// </summary>
        [DisplayName("Hide Prisc windows from screen capture")]
        [Description("Keep Prisc's own windows out of screenshots, recordings and screen shares.")]
        public HideFromCapture HideFromScreenCapture
        {
            get => _hideFromScreenCapture;
            set => Set(ref _hideFromScreenCapture, value);
        }

        [DisplayName("Confirm before exit")]
        [Description("Ask before Prisc closes.")]
        public bool ConfirmClose
        {
            get => _confirmClose;
            set => Set(ref _confirmClose, value);
        }

        private bool _useSystemAccentColor = true;
        private Color _accentColor = AccentColors.ClowdBlue;
        private bool _maintainMinimumContrast = true;
        private string _lastSavePath;
        private string _mainWindowBounds;
        private int? _drawOnScreenColorIndex;
        private int? _drawOnScreenSizeIndex;
        private string _language;
        private bool _confirmClose = true;
        private bool _registerExplorerContextMenu = DefaultRegisterExplorerContextMenu;
        private bool _registerAutoStart = DefaultRegisterAutoStart;
        private AppTheme _theme = AppTheme.System;

        // On by default: it is what a Tahoe-era mac window looks like, and the windows that
        // extend have been laid out for it (the gutters in SystemThemedWindow).
        private bool _extendIntoTitleBar = true;
        private ClickAction _trayClick = ClickAction.OpenSettings;
        private ClickAction _shortcutClick = ClickAction.OpenSettings;
        private HideFromCapture _hideFromScreenCapture = HideFromCapture.Tooltips;

        private bool _autoDownloadUpdates = true;
        private UpdateInterval _updateCheckInterval = UpdateInterval.ThreeHourly;

        // on by default: the restart only happens once the machine itself has been idle for ten
        // minutes (IdleMonitor), so in practice the user finds Clowd already up to date rather than
        // ever seeing it happen.
        private bool _autoApplyUpdates = true;
        private bool _includePrereleaseUpdates;
        private List<Color> _recentColors = new List<Color>();
    }
}
