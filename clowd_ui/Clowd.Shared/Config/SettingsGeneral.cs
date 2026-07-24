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

        // compat field: no explorer context menu registration is performed in this build
        [Browsable(false)]
        public bool RegisterExplorerContextMenu
        {
            get => _registerExplorerContextMenu;
            set => Set(ref _registerExplorerContextMenu, value);
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
        private bool _confirmClose = true;
        private bool _registerExplorerContextMenu = true;
        private bool _registerAutoStart = DefaultRegisterAutoStart;

        // only on by default where auto-start is: otherwise the first thing a manual launch does is
        // vanish into the tray, which reads as "nothing happened".
        private bool _startMinimized = DefaultRegisterAutoStart;
        private AppTheme _theme = AppTheme.System;
        private TrayClickAction _trayClick = TrayClickAction.OpenSettings;
    }
}
