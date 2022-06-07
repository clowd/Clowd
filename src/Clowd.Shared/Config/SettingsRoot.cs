using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using NLog;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{
    public sealed class SettingsRoot : IDisposable
    {
        [Browsable(false), ClassifyIgnore] public static SettingsRoot Current { get; private set; }

        public SettingsGeneral General { get; private set; } = new SettingsGeneral();

        public SettingsHotkey Hotkeys { get; private set; } = new SettingsHotkey();

        public SettingsCapture Capture { get; private set; } = new SettingsCapture();

        public SettingsEditor Editor { get; private set; } = new SettingsEditor();

        public SettingsUpload Uploads { get; private set; } = new SettingsUpload();

        public SettingsVideo Video { get; private set; } = new SettingsVideo();

        private CategoryBase[] All => new CategoryBase[] { General, Hotkeys, Capture, Editor, Uploads, Video };

        public SettingsRoot()
        {
            if (Current != null)
                throw new InvalidOperationException("Dispose old settings before creating a new one");
            Current = this;
        }

        static SettingsRoot()
        {
            Classify.DefaultOptions = GetClassifyOptions();
        }

        private static ILogger _log = LogManager.GetLogger(nameof(SettingsRoot));

        private static string FilePath =>
#if DEBUG
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd", "Clowd.DEBUG.Settings.xml");
#else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd", "Clowd.Settings.xml");
#endif

        private static ClassifyOptions GetClassifyOptions()
        {
            var opt = new ClassifyOptions();
            opt.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
            opt.AddTypeSubstitution(new ClassifyColorTypeOptions());
            return opt;
        }

        public static void LoadDefault()
        {
            if (!File.Exists(FilePath))
            {
                CreateNew();
                return;
            }
            
            try
            {
                var opt = GetClassifyOptions();
                opt.Errors = new List<ClassifyError>();

                Ut.WaitSharingVio(maximum: TimeSpan.FromSeconds(5), func: () =>
                {
                    var settings = ClassifyXml.DeserializeFile<SettingsRoot>(FilePath, opt);
                    settings.RegisterEvents();

                    if (opt.Errors.Any())
                    {
                        _log.Error("Settings were deserialized with errors:");
                        foreach (var e in opt.Errors)
                            _log.Error(e.Exception, "Exception on field: " + e.ObjectPath);
                    }
                    else
                    {
                        _log.Debug("Settings were loaded.");
                    }

                    return true;
                });
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

            if (Current == this)
                Current = null;
        }

        private void RegisterEvents()
        {
            Uploads.DiscoverProviders();
            All.ToList().ForEach(a => a.PropertyChanged += Item_PropertyChanged);
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Save();
        }

        public void Save()
        {
            var filename = FilePath;
            var tempname = filename + ".~tmp";

            var opt = GetClassifyOptions();
            opt.Errors = new List<ClassifyError>();

            Ut.WaitSharingVio(maximum: TimeSpan.FromSeconds(5), func: () =>
            {
                ClassifyXml.SerializeToFile(this, tempname, opt, format: ClassifyXmlFormat.Create("Settings"));
                File.Delete(filename);
                File.Move(tempname, filename);

                if (opt.Errors.Any())
                {
                    _log.Error("Settings were saved with errors:");
                    foreach (var e in opt.Errors)
                        _log.Error(e.Exception, e.ObjectPath);
                }
                else
                {
                    _log.Trace("Settings were saved.");
                }

                return true;
            });
        }
    }
}
