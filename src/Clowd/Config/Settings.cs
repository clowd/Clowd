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

        public GeneralSettings General { get; init; } = new GeneralSettings();

        public HotkeySettings Hotkey { get; init; } = new HotkeySettings();

        public CaptureSettings Capture { get; init; } = new CaptureSettings();

        public EditorSettings Editor { get; init; } = new EditorSettings();

        public UploadSettings Upload { get; init; } = new UploadSettings();

        public VideoCapturerSettings Video { get; init; } = new VideoCapturerSettings();

        public ClowdSettings()
        {
            if (Current != null)
                throw new InvalidOperationException("Dispose old settings before creating a new one");
            Current = this;
        }

        public void Dispose()
        {
            General.Dispose();
            Capture.Dispose();
            Editor.Dispose();
            Current = null;
        }
    }

    public abstract class SettingsCategoryBase : IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected SettingsCategoryBase()
        {
            PropertyChanged += SettingsCategoryBase_PropertyChanged;
        }

        private void SettingsCategoryBase_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ClowdSettings.Current.SaveLoud();
        }

        public void Dispose()
        {
            PropertyChanged -= SettingsCategoryBase_PropertyChanged;
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

        public AutoDictionary<DrawToolsLib.ToolType, SavedToolSettings> Tools { get; set; } = new AutoDictionary<DrawToolsLib.ToolType, SavedToolSettings>();
    }

    [AddINotifyPropertyChangedInterface]
    public class SavedToolSettings
    {
        public bool TextObjectColorIsAuto { get; set; } = true;
        public Color ObjectColor { get; set; } = Colors.Red;
        public double LineWidth { get; set; } = 2d;
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 12d;
        public FontStyle FontStyle { get; set; } = FontStyles.Normal;
        public FontWeight FontWeight { get; set; } = FontWeights.Normal;
        public FontStretch FontStretch { get; set; } = FontStretches.Normal;
    }
}
