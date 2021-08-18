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
    public sealed class SettingsRoot : SettingsBase, IDisposable
    {
        [Browsable(false), ClassifyIgnore]
        public static SettingsRoot Current { get; private set; }

        public SettingsGeneral General { get; private set; } = new SettingsGeneral();

        public SettingsHotkey Hotkeys { get; private set; } = new SettingsHotkey();

        public SettingsCapture Capture { get; private set; } = new SettingsCapture();

        public SettingsEditor Editor { get; private set; } = new SettingsEditor();

        public SettingsUpload Uploads { get; private set; } = new SettingsUpload();

        public SettingsVideo Video { get; private set; } = new SettingsVideo();

        private CategoryBase[] All => new CategoryBase[] { General, Hotkeys, Capture, Editor, Uploads, Video };

        static SettingsRoot()
        {
            Classify.DefaultOptions = new ClassifyOptions();
            Classify.DefaultOptions.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
            Classify.DefaultOptions.AddTypeSubstitution(new ClassifyColorTypeOptions());
        }

        public SettingsRoot()
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
                SettingsRoot tmp;
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
            var tmp = new SettingsRoot();
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
