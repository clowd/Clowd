using RT.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    [Settings("Clowd", SettingsKind.UserSpecific, SettingsSerializer.ClassifyXml)]
    public class AppSettings : SettingsBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public bool FirstRun { get; set; } = true;
        public bool ConfirmClose { get; set; } = true;
        public bool UseCustomWindowChrome { get; set; } = true;
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public Color UserAccentColor { get; set; } = Color.FromRgb(59, 151, 210);
        [RT.Util.Serialization.ClassifyIgnore]
        public ColorScheme ColorScheme { get; set; } = ColorScheme.Light;
        public AccentScheme AccentScheme { get; set; } = AccentScheme.User;
        public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromHours(6);
        public CaptureSettings CaptureSettings { get; set; } = new CaptureSettings();
        public string LastUploadPath { get; set; }
    }
    [PropertyChanged.ImplementPropertyChanged]
    public class CaptureSettings
    {
        public bool ScreenshotWithCursor { get; set; } = false;
        public bool ShowMagnifier { get; set; } = true;
        public Color DefaultDrawingColor { get; set; } = Colors.Red;
        public KeyGesture StartCaptureShortcut { get; set; } = new KeyGesture(Key.PrintScreen);
    }
    public enum ColorScheme
    {
        Light,
        Dark
    }
    public enum AccentScheme
    {
        User,
        System
    }
}
