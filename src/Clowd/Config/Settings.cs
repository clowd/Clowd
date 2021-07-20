using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.UI;
using Clowd.UI.Config;
using Clowd.UI.Helpers;
using Clowd.Upload;
using Clowd.Util;
using PropertyChanged;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    [Settings("Clowd", SettingsKind.UserSpecific, SettingsSerializer.ClassifyXml)]
    public class ClowdSettings : SettingsBase, IDisposable
    {
        [Browsable(false), ClassifyIgnore]
        public static ClowdSettings Current { get; private set; }

        public GeneralSettings General { get; private set; } = new GeneralSettings();

        public HotkeySettings Hotkeys { get; private set; } = new HotkeySettings();

        public CaptureSettings Capture { get; private set; } = new CaptureSettings();

        public EditorSettings Editor { get; private set; } = new EditorSettings();

        public UploadSettings Uploads { get; private set; } = new UploadSettings();

        public VideoCapturerSettings Video { get; private set; } = new VideoCapturerSettings();

        protected INotifyPropertyChanged[] All => new INotifyPropertyChanged[] { General, Hotkeys, Capture, Editor, Uploads, Video };

        public ClowdSettings()
        {
            if (Current != null)
                throw new InvalidOperationException("Dispose old settings before creating a new one");
            Current = this;
            All.ToList().ForEach(a => a.PropertyChanged += Item_PropertyChanged);
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveLoud();
        }

        public override void Save(string filename = null, SettingsSerializer? serializer = null, SettingsOnFailure onFailure = SettingsOnFailure.Throw)
        {
            SaveInternal(filename, serializer, onFailure);
        }

        public override void SaveLoud(string filename = null, SettingsSerializer? serializer = null)
        {
            SaveInternal(filename, serializer, SettingsOnFailure.Throw);
        }

        public override void SaveQuiet(string filename = null, SettingsSerializer? serializer = null)
        {
            SaveInternal(filename, serializer, SettingsOnFailure.DoNothing);
        }

        protected void SaveInternal(string filename = null, SettingsSerializer? serializer = null, SettingsOnFailure onFailure = SettingsOnFailure.Throw)
        {
            base.Save(filename, serializer, onFailure);
            System.Diagnostics.Trace.WriteLine("Saved Settings");
        }

        public static void LoadDefault()
        {
            try
            {
                ClowdSettings tmp;
                SettingsUtil.LoadSettings(out tmp);
            }
            catch
            {
                Current?.Dispose();
                throw;
            }
        }

        public static void CreateNew()
        {
            new ClowdSettings();
        }

        public void Dispose()
        {
            All.ToList().ForEach(a => a.PropertyChanged -= Item_PropertyChanged);
            General?.Dispose();
            Capture?.Dispose();
            Editor?.Dispose();
            Current = null;
        }
    }

    public abstract class SettingsCategoryBase : IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        [ClassifyIgnore]
        private readonly List<INotifyPropertyChanged> _subscriptions = new List<INotifyPropertyChanged>();

        protected void Subscribe(params INotifyPropertyChanged[] subscriptions)
        {
            _subscriptions.AddRange(subscriptions);
            subscriptions.ToList().ForEach(a => a.PropertyChanged += Item_PropertyChanged);
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, e);
        }

        public void Dispose()
        {
            _subscriptions.ForEach(a => a.PropertyChanged -= Item_PropertyChanged);
            DisposeInternal();
        }

        public virtual void DisposeInternal() { }
    }

    public class GeneralSettings : SettingsCategoryBase
    {
        [Browsable(false)]
        public bool FirstRun { get; set; } = true;

        [Browsable(false)]
        public string LastUploadPath { get; set; }

        [Browsable(false)]
        public string LastSavePath { get; set; }

        [DisplayName("Confirm before exit")]
        [Description("If true, Clowd will prompt for confirmation before closing.")]
        public bool ConfirmClose { get; set; } = true;

        [ClassifyIgnore]
        public AppTheme Theme
        {
            get => ModernWpf.ThemeManager.Current.ApplicationTheme switch
            {
                ModernWpf.ApplicationTheme.Light => AppTheme.Light,
                ModernWpf.ApplicationTheme.Dark => AppTheme.Dark,
                _ => AppTheme.Auto,
            };

            set => ModernWpf.ThemeManager.Current.ApplicationTheme = value switch
            {
                AppTheme.Light => ModernWpf.ApplicationTheme.Light,
                AppTheme.Dark => ModernWpf.ApplicationTheme.Dark,
                _ => null,
            };
        }

        [ClassifyIgnore]
        public Installer.Features.AutoStart StartWithWindows { get; set; } = new Installer.Features.AutoStart();

        [ClassifyIgnore]
        public Installer.Features.ContextMenu AddExplorerContextMenu { get; set; } = new Installer.Features.ContextMenu();

        [ClassifyIgnore]
        public Installer.Features.Shortcuts CreateDesktopShortcuts { get; set; } = new Installer.Features.Shortcuts();

        public enum AppTheme
        {
            [Description("Auto / System")]
            Auto,
            Light,
            Dark
        }
    }

    public class HotkeySettings : SettingsCategoryBase
    {
        [DisplayName("General - File Upload"), ClassifyIgnoreIfDefault]
        public GlobalTrigger FileUploadShortcut { get; set; }
            = new GlobalTrigger(() => App.Current.UploadFile());

        [DisplayName("Capture - Region"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureRegionShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, () => App.Current.StartCapture());

        [DisplayName("Capture - Fullscreen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureFullscreenShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Control, App.Current.QuickCaptureFullScreen);

        [DisplayName("Capture - Active Window"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureActiveShortcut { get; set; }
            = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Alt, App.Current.QuickCaptureCurrentWindow);

        public HotkeySettings()
        {
            Subscribe(FileUploadShortcut, CaptureRegionShortcut, CaptureFullscreenShortcut, CaptureActiveShortcut);
        }

        public override void DisposeInternal()
        {
            FileUploadShortcut?.Dispose();
            CaptureRegionShortcut?.Dispose();
            CaptureFullscreenShortcut?.Dispose();
            CaptureActiveShortcut?.Dispose();
        }
    }

    public class CaptureSettings : SettingsCategoryBase
    {
        [DisplayName("Capture with cursor")]
        [Description("If this is enabled, the cursor will be shown in screenshots")]
        public bool ScreenshotWithCursor { get; set; } = false;

        [Description("If this is true, the Capture window will try to detect and highlight different windows as you hover over them.")]
        public bool DetectWindows { get; set; } = true;

        public bool HideTipsPanel { get; set; }
    }

    public class EditorSettings : SettingsCategoryBase
    {
        public Color CanvasBackground { get; set; } = Colors.White;

        public bool TabsEnabled { get; set; } = true;

        [Browsable(false)]
        public int StartupPadding { get; set; } = 30;

        [DisplayName("Tool preferences")]
        public AutoDictionary<DrawToolsLib.ToolType, SavedToolSettings> Tools { get; set; } = new AutoDictionary<DrawToolsLib.ToolType, SavedToolSettings>();

        public EditorSettings()
        {
            Subscribe(Tools);
        }
    }

    public class SavedToolSettings : INotifyPropertyChanged
    {
        public bool TextObjectColorIsAuto { get; set; } = true;
        public Color ObjectColor { get; set; } = Colors.Red;
        public double LineWidth { get; set; } = 2d;
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 12d;
        public FontStyle FontStyle { get; set; } = FontStyles.Normal;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public FontStretch FontStretch { get; set; } = FontStretches.Normal;

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
