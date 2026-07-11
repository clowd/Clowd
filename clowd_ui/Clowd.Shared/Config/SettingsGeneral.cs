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

        // compat field: no auto-start registration is performed in this build
        [Browsable(false)]
        public bool RegisterAutoStart
        {
            get => _registerAutoStart;
            set => Set(ref _registerAutoStart, value);
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
        private bool _registerAutoStart = true;
        private AppTheme _theme = AppTheme.System;
        private TrayClickAction _trayClick = TrayClickAction.OpenSettings;
    }
}
