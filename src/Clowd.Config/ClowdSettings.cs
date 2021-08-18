using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    [Settings("Clowd", SettingsKind.UserSpecific, SettingsSerializer.ClassifyXml)]
    public sealed class ClowdSettings : SettingsBase, IDisposable
    {
        [Browsable(false), ClassifyIgnore]
        public static ClowdSettings Current { get; private set; }

        public GeneralSettings General { get; private set; } = new GeneralSettings();

        public HotkeySettings Hotkeys { get; private set; } = new HotkeySettings();

        public CaptureSettings Capture { get; private set; } = new CaptureSettings();

        public EditorSettings Editor { get; private set; } = new EditorSettings();

        public UploadSettings Uploads { get; private set; } = new UploadSettings();

        public VideoSettings Video { get; private set; } = new VideoSettings();

        private SettingsCategoryBase[] All => new SettingsCategoryBase[] { General, Hotkeys, Capture, Editor, Uploads, Video };

        static ClowdSettings()
        {
            Classify.DefaultOptions = new ClassifyOptions();
            Classify.DefaultOptions.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
            Classify.DefaultOptions.AddTypeSubstitution(new ClassifyColorTypeOptions());
        }

        public ClowdSettings()
        {
            if (Current != null)
                throw new InvalidOperationException("Dispose old settings before creating a new one");
            Current = this;
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

        public static void LoadDefault()
        {
            try
            {
                ClowdSettings tmp;
                SettingsUtil.LoadSettings(out tmp);
                tmp.RegisterEvents();
            }
            catch
            {
                Current?.Dispose();
                throw;
            }
        }

        public static void CreateNew()
        {
            var tmp = new ClowdSettings();
            tmp.RegisterEvents();
        }

        public void Dispose()
        {
            All.ToList().ForEach(a => a.PropertyChanged -= Item_PropertyChanged);
            All.ToList().ForEach(a => a.Dispose());
            Current = null;
        }

        private void RegisterEvents()
        {
            All.ToList().ForEach(a => a.PropertyChanged += Item_PropertyChanged);
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveQuiet();
        }

        private void SaveInternal(string filename = null, SettingsSerializer? serializer = null, SettingsOnFailure onFailure = SettingsOnFailure.Throw)
        {
            base.Save(filename, serializer, onFailure);
            System.Diagnostics.Trace.WriteLine("Saved Settings");
        }
    }
}
