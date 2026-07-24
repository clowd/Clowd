using System;
using System.ComponentModel;

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

        [DisplayName("Start Clowd minimised")]
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
        /// The Velopack channel to fetch updates from, overriding the channel this build was
        /// installed from (see UpdateService). Null means "follow the installed channel", which is
        /// the state of every install until the user switches between stable and pre-release.
        ///
        /// Once written this is never set back to null: SettingsService.Load binds through
        /// ConfigurationBuilder, which skips null values, so a null would let the previously saved
        /// channel resurrect on the next launch. The effective channel is always stored in full.
        /// </summary>
        [Browsable(false)]
        public string UpdateChannel
        {
            get => _updateChannel;
            set => Set(ref _updateChannel, value);
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
        private string _updateChannel;
    }
}
