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
using RT.Serialization;
using PData = PropertyTools.DataAnnotations;
using System.Reflection;
using FileUploadLib.Providers;
//using Screeney;

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

    public enum UploadsProvider
    {
        [Description("None / Disabled")]
        None = 0,
        [Description("Azure Storage")]
        Azure
    }

    public enum SelectedWindowForegroundPromotion
    {
        [Description("Disabled")]
        None,
        [Description("When Clicked")]
        WhenClicked,
        [Description("When Hovered")]
        WhenHovered
    }

    [ImplementPropertyChanged]
    [Settings("Clowd", SettingsKind.UserSpecific, SettingsSerializer.ClassifyXml)]
    public class GeneralSettings : SettingsBase, INotifyPropertyChanged, IDisposable, IClassifyObjectProcessor
    {
        [Browsable(false), ClassifyIgnore]
        public new object Attribute { get; } = null;

        [Browsable(false)]
        public event PropertyChangedEventHandler PropertyChanged;

        [Browsable(false)]
        public bool FirstRun { get; set; } = true;

        [Browsable(false)]
        public string LastUploadPath { get; set; }

        [Category("General"), DisplayName("Confirm before exit")]
        [Description("If true, Clowd will prompt for confirmation before closing.")]
        public bool ConfirmClose { get; set; } = true;

        [Description("Specifies whether to use the system default window chrome, or a metro design.")]
        public bool UseCustomWindowChrome { get; set; } = false;

        [DisplayName("Tray-drop enabled")]
        [Description("If true, allows dropping files directly on to the windows tray icon to start an upload.")]
        public bool TrayDropEnabled { get; set; } = true;

        [DisplayName("Add Windows Explorer Context-Menu item")]
        [Description("If true, will add an item to the explorer context menu allowing you to right click to upload files.")]
        public bool ExplorerMenuEnabled
        {
            get
            {
                return (new Installer.Features.ContextMenu()).CheckInstalled(Assembly.GetExecutingAssembly().Location, Installer.RegistryQuery.CurrentUser);
            }
            set
            {
                var assetPath = Assembly.GetExecutingAssembly().Location;
                var feature = new Installer.Features.ContextMenu();
                var isInstalled = feature.CheckInstalled(assetPath, Installer.RegistryQuery.CurrentUser);
                if (isInstalled != value)
                {
                    if (value)
                    {
                        feature.Install(assetPath, Installer.InstallMode.CurrentUser);
                    }
                    else
                    {
                        feature.Uninstall(assetPath, Installer.RegistryQuery.CurrentUser);
                    }
                }
            }
        }


        [DisplayName("Accent color"), PData.EnableBy(nameof(AccentScheme), AccentScheme.User)]
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
            = new GlobalTrigger(() => App.Current.ShowHome());

        [ExpandAsCategory("Capture")]
        public CaptureSettings CaptureSettings { get; set; } = new CaptureSettings();

        [ExpandAsCategory("Editor")]
        public EditorSettings EditorSettings { get; set; } = new EditorSettings();

        [ExpandAsCategory("Uploads")]
        public UploadSettings UploadSettings { get; set; } = new UploadSettings();

        [ExpandAsCategory("Video")]
        public VideoSettings VideoSettings { get; set; } = new VideoSettings();

        [Browsable(false)]
        public MagnifierSettings MagnifierSettings { get; set; } = new MagnifierSettings();

        [Browsable(false), ClassifyNotNull]
        public int[] CustomColors { get; set; } = new int[0];

        private IEnumerable<T> GetAllAssignableToT<T>(T root)
        {
            MethodInfo method = GetType().GetMethod(nameof(GetAllAssignableToT), BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo myself = method.MakeGenericMethod(typeof(T));

            yield return root;

            var subSettings = root
                .GetType()
                .GetProperties()
                .Where(p => typeof(T).IsAssignableFrom(p.PropertyType))
                .Select(p => p.GetValue(root))
                .Cast<T>();

            foreach (var s in subSettings)
                foreach (var results in (IEnumerable<T>)myself.Invoke(this, new[] { (object)s }))
                    yield return results;
        }

        public void Dispose()
        {
            foreach (var a in GetAllAssignableToT<IDisposable>(this))
                a?.Dispose();
        }

        public void BeforeSerialize()
        {
            // do nothing
        }

        public void AfterDeserialize()
        {
            var debouncer = new Debouncer();
            var tmp = GetAllAssignableToT<INotifyPropertyChanged>(this).ToArray();
            foreach (var a in tmp)
                if (a != null)
                    a.PropertyChanged += (s, ev) => debouncer.Debounce(() => this.SaveQuiet());
        }

        public override void SaveQuiet(string filename = null, SettingsSerializer? serializer = null)
        {
            base.SaveQuiet(filename, serializer);
            Console.WriteLine("Saved!!!!!!!");
        }
    }

    [ImplementPropertyChanged]
    public class CaptureSettings : IDisposable
    {
        [DisplayName("Capture with cursor")]
        [Description("If this is enabled, the cursor will be shown in screenshots")]
        public bool ScreenshotWithCursor { get; set; } = false;

        [Description("This controls the default state of the pixel magnifier in the capture window")]
        public bool MagnifierEnabled { get; set; } = true;

        [Description("This controls whether the tips menu is displayed when capturing a screenshot.")]
        public bool TipsEnabled { get; set; } = true;

        [Description("If this is true, the Capture window will try to detect and highlight different windows as you hover over them.")]
        public bool DetectWindows { get; set; } = true;

        [DisplayName("Bring selected window to the foreground")]
        public SelectedWindowForegroundPromotion SelectedWindowPromotion { get; set; } = SelectedWindowForegroundPromotion.WhenClicked;

        [Category("Hotkeys"), DisplayName("Capture - Region"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureRegionShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, () => App.Current.StartCapture());

        [Category("Hotkeys"), DisplayName("Capture - Fullscreen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureFullscreenShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Control, App.Current.QuickCaptureFullScreen);

        [Category("Hotkeys"), DisplayName("Capture - Active Window"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureActiveShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Alt, App.Current.QuickCaptureCurrentWindow);

        public void Dispose()
        {
            CaptureRegionShortcut?.Dispose();
            CaptureFullscreenShortcut?.Dispose();
            CaptureActiveShortcut?.Dispose();
        }
    }

    [ImplementPropertyChanged]
    public class EditorSettings
    {
        [DisplayName("Object color")]
        public Color ObjectDrawingColor { get; set; } = Colors.Red;

        [PData.Spinnable(1, 2, 1, 10)]
        public int ObjectLineWidth { get; set; } = 2;

        public Color CanvasBackground { get; set; } = Colors.White;

        [DisplayName("Font family"), PData.FontPreview(16), ClassifyIgnore]
        public FontFamily DefaultFont
        {
            get { return new FontFamily(_defaultFontString); }
            set { _defaultFontString = value.Source; }
        }
        private string _defaultFontString = "Arial";

        [DisplayName("Font size"), PData.Spinnable(2, 4, 6, 48)]
        public int DefaultFontSize { get; set; } = 8;

        [DisplayName("Capture padding (px)"), PData.Spinnable(5, 10, 0, 100)]
        [Description("Controls how much space (in pixels) there is between an opened capture and the editors window edge.")]
        public int CapturePadding { get; set; } = 30;
    }

    [ImplementPropertyChanged]
    public class UploadSettings : IDisposable, IAzureOptions
    {
        [Category("Hotkeys"), DisplayName("Uploads - Activate Next"), ClassifyIgnoreIfDefault]
        [Description("This hotkey activates the next item in the task window. " +
                     "For instance, if the next item is an upload it will be copied to the clipboard.")]
        public GlobalTrigger ActivateNextShortcut { get; set; }
           = new GlobalTrigger(() => TaskWindow.Current?.ActivateNext());

        [Category("Uploads"), DisplayName("Upload Storage Provider")]
        public UploadsProvider UploadProvider { get; set; } = UploadsProvider.None;

        [PData.VisibleBy(nameof(UploadProvider), UploadsProvider.Azure)]
        public string AzureConnectionString { get; set; }

        [PData.VisibleBy(nameof(UploadProvider), UploadsProvider.Azure)]
        public string AzureContainerName { get; set; }

        [Description("If true, the original filename will be ignored and a random one will be chosen for the upload.")]
        public bool UseUniqueUploadKey { get; set; } = true;

        [Description("Use if you would like to override the default URL generated by Clowd. Use the following substitution variables: \n#{uk} - Upload Key\n#{mt} - Mime Type (calculated from file name)")]
        public string CustomUrlPattern { get; set; }

        public void Dispose()
        {
            ActivateNextShortcut?.Dispose();
        }
    }

    public enum BitrateMultiplier : int
    {
        Low = 75,
        Medium = 100,
        High = 150,
    }

    public enum MaxResolution : int
    {
        [Description("Uncapped")]
        Uncapped = 0,
        [Description("SD - 480p")]
        _480p = 480,
        [Description("HD - 720p")]
        _720p = 720,
        [Description("HD - 1080p")]
        _1080p = 1080,
        [Description("HD - 1440p")]
        _1440p = 1440,
        [Description("4K - 2160p")]
        _2160p = 2160,
    }

    [ImplementPropertyChanged]
    public class VideoSettings : IDisposable
    {
        [DisplayName("Max Resolution")]
        public MaxResolution MaxResolution { get; set; } = MaxResolution._1080p;

        [PData.DirectoryPath]
        public string OutputDirectory { get; set; }

        public bool ShowCursor { get; set; } = true;

        public CaptureVideoCodec VideoCodec { get; set; } = CaptureVideoCodec.libx264;

        [PData.VisibleBy(nameof(VideoCodec), CaptureVideoCodec.h264_nvenc)]
        public FFMpegCodecSettings_h264_nvenc h264_nvenc { get; set; } = new FFMpegCodecSettings_h264_nvenc();

        [PData.VisibleBy(nameof(VideoCodec), CaptureVideoCodec.libx264)]
        public FFMpegCodecSettings_libx264 libx264 { get; set; } = new FFMpegCodecSettings_libx264();

        public void Dispose()
        {
        }
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
