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

    public enum TrayClickAction
    {
        [Description("Open settings")]
        OpenSettings,

        [Description("Capture region")]
        CaptureRegion,
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
        /// Default for <see cref="RegisterAutoStart"/> (and, per that, <see cref="StartMinimized"/>).
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

        [DisplayName("Start Clowd when your computer starts up")]
        [Description("Launches Clowd automatically when you log in.")]
        public bool RegisterAutoStart
        {
            get => _registerAutoStart;
            set => Set(ref _registerAutoStart, value);
        }

        [DisplayName("Start Clowd minimized")]
        [Description("Starts Clowd in the notification area without opening this window.")]
        public bool StartMinimized
        {
            get => _startMinimized;
            set => Set(ref _startMinimized, value);
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

        [DisplayName("Add 'Upload with Clowd' to the Explorer context menu")]
        [Description("Adds a right-click entry for files and folders that uploads them with Clowd.")]
        public bool RegisterExplorerContextMenu
        {
            get => _registerExplorerContextMenu;
            set => Set(ref _registerExplorerContextMenu, value);
        }

        [DisplayName("Automatically check for and download updates")]
        [Description("Periodically looks for a newer release while Clowd is running, and downloads it ready to be applied.")]
        public bool AutoDownloadUpdates
        {
            get => _autoDownloadUpdates;
            set => Set(ref _autoDownloadUpdates, value);
        }

        [DisplayName("Check for updates")]
        [Description("How often Clowd looks for a newer release.")]
        public UpdateInterval UpdateCheckInterval
        {
            get => _updateCheckInterval;
            set => Set(ref _updateCheckInterval, value);
        }

        [DisplayName("Automatically restart Clowd to apply updates in the background")]
        [Description("Silently restarts Clowd to finish installing a downloaded update, but only once the computer has been idle for a while.")]
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
        [DisplayName("Opt-in to experimental builds")]
        [Description("Bleeding edge releases may have newer preview features, but also may have more bugs than stable releases.")]
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
        /// Not browsable: the settings-control factory would render a text box. The General page
        /// hosts a hand-built combo populated from Loc.GetAvailableLanguages().
        /// </summary>
        [Browsable(false)]
        public string Language
        {
            get => _language;
            set => Set(ref _language, value);
        }

        [DisplayName("Theme")]
        [Description("Choose the light or dark appearance, or follow the Windows setting.")]
        public AppTheme Theme
        {
            get => _theme;
            set => Set(ref _theme, value);
        }

        /// <summary>
        /// Follow the OS accent color instead of <see cref="AccentColor"/>. Reads as false wherever
        /// there is no system accent to read (macOS), so the row it disables cannot get stuck grayed
        /// out on a platform that hides this checkbox.
        /// </summary>
        [DisplayName("Use system accent color")]
        [Description("Draw Clowd's capture surfaces in the accent color chosen in Windows settings")]
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
        [Description("Color of the crosshair, selection border and primary buttons in the capture overlay, " +
                     "and of the recording toolbar and border.")]
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
        [Description("Darken the accent color until the white labels drawn on it stay readable. " +
                     "Turning this off uses your color exactly as picked, which may make those labels hard to read.")]
        public bool MaintainMinimumContrast
        {
            get => _maintainMinimumContrast;
            set => Set(ref _maintainMinimumContrast, value);
        }

        /// <summary>
        /// The color the capture surfaces are actually drawn in: the OS accent when the user asked
        /// for it and there is one to read, otherwise their own choice — darkened for legibility
        /// unless <see cref="MaintainMinimumContrast"/> says not to. This is what the overlay is
        /// launched with (<c>--accent-color</c>) and what <c>AppStyles.CaptureAccentColor</c>
        /// paints the recording toolbar and border with, so all of them agree by construction.
        /// </summary>
        public Color GetEffectiveAccentColor()
        {
            var color = (UseSystemAccentColor ? AccentColors.GetSystemAccent() : null) ?? AccentColor;
            return MaintainMinimumContrast ? AccentColors.EnsureContrastWithWhite(color) : color;
        }

        [DisplayName("Tray icon click")]
        [Description("What a single click on the tray icon does. The right-click menu always offers everything.")]
        public TrayClickAction TrayClick
        {
            get => _trayClick;
            set => Set(ref _trayClick, value);
        }

        [DisplayName("Confirm before exit")]
        [Description("If true, Clowd will prompt for confirmation before closing.")]
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
        private string _language;
        private bool _confirmClose = true;
        private bool _registerExplorerContextMenu = DefaultRegisterExplorerContextMenu;
        private bool _registerAutoStart = DefaultRegisterAutoStart;

        // only on by default where auto-start is: otherwise the first thing a manual launch does is
        // vanish into the tray, which reads as "nothing happened".
        private bool _startMinimized = DefaultRegisterAutoStart;
        private AppTheme _theme = AppTheme.System;
        private TrayClickAction _trayClick = TrayClickAction.OpenSettings;

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
