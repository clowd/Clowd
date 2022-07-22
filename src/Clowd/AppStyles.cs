using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.UI.ViewManagement;
using Icon = System.Drawing.Icon;

namespace Clowd
{
    enum ResourceIcon
    {
        IconClowd,
        IconPhoto,
        IconVideo,
        IconCopy,
        IconCopySmall,
        IconSave,
        IconSaveSmall,
        IconSearch,
        IconReset,
        IconClose,
        IconPlay,
        IconStop,
        IconSettings,
        IconDrawing,
        IconMicrophoneEnabled,
        IconMicrophoneDisabled,
        IconSpeakerEnabled,
        IconSpeakerDisabled,
        IconToolNone,
        IconToolPointer,
        IconToolRectangle,
        IconToolFilledRectangle,
        IconToolEllipse,
        IconToolLine,
        IconToolArrow,
        IconToolPolyLine,
        IconToolText,
        IconToolPixelate,
        IconToolErase,
        IconUndo,
        IconRedo,
        IconPinned,
        IconCrop,
        IconChevronDown,
        IconHamburgerMore,
        IconVideoMKV,
        IconVideoMP4,
        IconVideoGIF,
        IconGalleryList,
        IconGallerySmall,
        IconGalleryMedium,
        IconGalleryLarge,
    }

    static class AppStyles
    {
       
        public static Color AccentColor => (Color)FindResource(SYSTEM_ACCENT_COLOR_KEY);
        public static Style AudioLevelProgressBarStyle => (Style)FindResource("AudioLevelProgressBarStyle");
        public static Brush CheckerboardBrushSmall => (Brush)FindResource("CheckeredLightGrayBackgroundBrush");
        public static Brush AccentBackgroundBrush => new SolidColorBrush(AccentColor);
        public static Brush IdealBackgroundBrush => new SolidColorBrush(Color.FromRgb(55, 55, 55));
        public static Brush IdealForegroundBrush => Brushes.White;
        public static Style CustomWindowStyle => (Style)FindResource("CustomUiWindow");

        // public static bool IsDarkTheme => WPFUI.Appearance.Theme.GetAppTheme() == WPFUI.Appearance.ThemeType.Dark;

        public static Stream AppIconDarkThemeStream => Application.GetResourceStream(new Uri("pack://application:,,,/Images/default-white.ico")).Stream;
        public static Stream AppIconLightThemeStream => Application.GetResourceStream(new Uri("pack://application:,,,/Images/default.ico")).Stream;

        public static string UiDateTimePattern
            => System.Threading.Thread.CurrentThread.CurrentCulture.DateTimeFormat.ShortDatePattern + " " +
               System.Threading.Thread.CurrentThread.CurrentCulture.DateTimeFormat.ShortTimePattern;

        private static readonly UISettings _uiSettings;
        private const string SYSTEM_ACCENT_COLOR_KEY = "SystemAccentColor";
        
        static AppStyles()
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => Initialize();
        }

        public static Icon AppIconGdi
        {
            get
            {
                var desiredSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
                var avaliableSizes = new[] { 64, 48, 40, 32, 24, 20, 16 };
                var nearest = avaliableSizes.OrderBy(x => Math.Abs(x - desiredSize)).First();
                // var stream = IsDarkTheme ? AppIconDarkThemeStream : AppIconLightThemeStream;
                return new Icon(AppIconLightThemeStream, new System.Drawing.Size(nearest, nearest));
            }
        }

        public static ImageSource AppIconWpf
        {
            get
            {
                // var stream = IsDarkTheme ? AppIconDarkThemeStream : AppIconLightThemeStream;
                BitmapDecoder decoder = IconBitmapDecoder.Create(AppIconLightThemeStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
        }

        public static System.Windows.Shapes.Path GetIconElement(ResourceIcon icon)
        {
            return (System.Windows.Shapes.Path)FindResource(icon.ToString());
        }

        public static void Initialize()
        {
            App.Current.Resources[SYSTEM_ACCENT_COLOR_KEY] = GetUIColor(UIColorType.Accent);
        }

        private static Color GetUIColor(UIColorType type)
        {
            var c = _uiSettings.GetColorValue(type);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        private static object FindResource(string resourceName)
        {
            return App.Current.FindResource(resourceName);
        }
    }
}
