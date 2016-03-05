using RT.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using PropertyChanged;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using Clowd.Controls;
using Clowd.Utilities;
using PropertyTools.Wpf;
using RT.Util.Serialization;
using PData = PropertyTools.DataAnnotations;

namespace Clowd
{
    public enum AccentScheme
    {
        [Description("User defined")]
        User,
        [Description("Automatic")]
        System
    }

    public enum SettingsDisplayMode
    {
        [Description("Tabbed")]
        Tabbed,
        [Description("Single Page")]
        SinglePage
    }

    [ImplementPropertyChanged]
    [Settings("Clowd", SettingsKind.UserSpecific, SettingsSerializer.ClassifyXml)]
    public class GeneralSettings : SettingsBase, INotifyPropertyChanged
    {
        [Browsable(false), ClassifyIgnore]
        public new object Attribute { get; } = null;

        [Browsable(false)]
        public event PropertyChangedEventHandler PropertyChanged;

        [Browsable(false)]
        public bool FirstRun { get; set; } = true;

        [Browsable(false)]
        public string LastUploadPath { get; set; }

        [Browsable(false)]
        public string Username { get; set; }

        [Browsable(false)]
        public string PasswordHash { get; set; }

        [Category("General"), DisplayName("Confirm before exit")]
        [Description("If true, Clowd will prompt for confirmation before closing.")]
        public bool ConfirmClose { get; set; } = true;

        [Description("Specifies whether to use the system default window chrome, or a metro design.")]
        public bool UseCustomWindowChrome { get; set; } = true;

        [DisplayName("Accent color"), PData.EnableBy("AccentScheme", AccentScheme.User)]
        [Description("Allows you to set a custom accent color when the appropriate accent mode is also set.")]
        public Color UserAccentColor { get; set; } = Color.FromRgb(59, 151, 210);

        [DisplayName("Accent mode")]
        [Description("If user is selected, you can define a custom accent color. " +
                     "Otherwise, Clowd will choose a color based on the system color")]
        public AccentScheme AccentScheme { get; set; } = AccentScheme.User;

        [DataType(DataType.Duration)]
        [Description("This is how often clowd checks for updates.")]
        public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromHours(6);

        [DisplayName("Settings Display Mode")]
        [Description("This controls whether the settings window is separated into tabbed content" +
                     "or as a single scrolling page.")]
        public SettingsDisplayMode DisplayMode { get; set; } = SettingsDisplayMode.Tabbed;

        [Category("Hotkeys"), DisplayName("General - File Upload"), ClassifyIgnoreIfDefault]
        public GlobalTrigger FileUploadShortcut { get; set; }
            = new GlobalTrigger(() => App.Current.UploadFile());

        [Category("Hotkeys"), DisplayName("General - Open Clowd"), ClassifyIgnoreIfDefault]
        public GlobalTrigger OpenHomeShortcut { get; set; }
            = new GlobalTrigger(() =>
                {
                    if (UploadManager.Authenticated)
                        TemplatedWindow.CreateWindow("Clowd", new HomePage()).Show();
                    else
                        TemplatedWindow.CreateWindow("Clowd", new LoginPage()).Show();
                });

        [ExpandAsCategory("Capture")]
        public CaptureSettings CaptureSettings { get; set; } = new CaptureSettings();

        [ExpandAsCategory("Editor")]
        public EditorSettings EditorSettings { get; set; } = new EditorSettings();

        public MagnifierSettings MagnifierSettings { get; set; } = new MagnifierSettings();
    }

    [ImplementPropertyChanged]
    public class CaptureSettings
    {
        [DisplayName("Capture with cursor")]
        [Description("If this is enabled, the cursor will be shown in screenshots")]
        public bool ScreenshotWithCursor { get; set; } = false;

        [Description("This controls the default state of the pixel magnifier in the capture window")]
        public bool MagnifierEnabled { get; set; } = true;

        [Description("If this is true, the Capture window will try to detect and highlight different windows as you hover over them.")]
        public bool DetectWindows { get; set; } = true;

        [Category("Hotkeys"), DisplayName("Capture - Region"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureRegionShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, () => App.Current.StartCapture());

        [Category("Hotkeys"), DisplayName("Capture - Fullscreen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureFullscreenShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Control, () => App.Current.QuickCapture());

        [Category("Hotkeys"), DisplayName("Capture - Active Window"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureActiveShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Alt, () => App.Current.QuickCapture());
    }

    [ImplementPropertyChanged]
    public class EditorSettings
    {
        [DisplayName("Object Color")]
        public Color DefaultDrawingColor { get; set; } = Colors.Red;

        [PData.FontPreview(16), ClassifyIgnore]
        public FontFamily DefaultFont
        {
            get { return new FontFamily(_defaultFontString); }
            set { _defaultFontString = value.Source; }
        }
        private string _defaultFontString = "Arial";

        [PData.Spinnable(1, 2, 1, 10)]
        public int ObjectLineWidth { get; set; } = 2;
    }

    [ImplementPropertyChanged]
    public class MagnifierSettings
    {
        public double Zoom { get; set; } = 12;
        public int AreaSize { get; set; } = 13;
        public double GridLineWidth { get; set; } = 0.7; // 1 pixel at <= 200% DPI; 2 pixels at 225% DPI; always rendered as a whole number of pixels
        public double BorderWidth { get; set; } = 3;
        public Color BorderColor { get; set; } = Colors.DarkGray;
        public Color CrosshairColor { get; set; } = Color.FromArgb(125, 173, 216, 230); // light blue
    }
}
