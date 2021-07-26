using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clowd.Installer;
using Clowd.Installer.Features;
using NAppUpdate.Framework;
using NAppUpdate.Framework.Sources;

namespace Clowd.Setup.Views
{
    public class DoWorkViewModel : ViewModelBase
    {
        public string Step
        {
            get => _step;
            set => RaiseAndSetIfChanged(ref _step, value);
        }

        public double Progress
        {
            get => _progress;
            set => RaiseAndSetIfChanged(ref _progress, value);
        }

        public bool CanCancel
        {
            get => !CancelRequested && _canCancel;
            set => RaiseAndSetIfChanged(ref _canCancel, value);
        }

        public bool CancelRequested
        {
            get => _cancelRequested;
            set
            {
                RaiseAndSetIfChanged(ref _cancelRequested, value);
                RaisePropertyChanged(nameof(CanCancel));
            }
        }

        public bool ProgressIndeterminate
        {
            get => _progressIndeterminate;
            set => RaiseAndSetIfChanged(ref _progressIndeterminate, value);
        }

        private bool _canCancel = true;
        private bool _cancelRequested = false;
        private string _step = "Preparing...";
        private double _progress;
        private bool _progressIndeterminate = true;
    }

    public partial class DoWorkView : UserControl
    {
        public DoWorkViewModel WorkModel { get; }
        public CustomizeViewModel CustomizeModel { get; }
        public UninstallViewModel UninstModel { get; }
        public UpdateManager Manager { get; private set; }

        public DoWorkView() // designer
        {
            this.DataContext = new DoWorkViewModel();
            InitializeComponent();
        }

        public DoWorkView(CustomizeViewModel model) // install
        {
            CustomizeModel = model;
            WorkModel = new DoWorkViewModel();
            this.DataContext = WorkModel;
            InitializeComponent();
            Install();
        }

        public DoWorkView(UninstallViewModel model) // uninstall
        {
            UninstModel = model;
            WorkModel = new DoWorkViewModel();
            this.DataContext = WorkModel;
            InitializeComponent();
            Uninstall();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void Install()
        {
            var closingHandler = new EventHandler<CancelEventArgs>((s, e) =>
            {
                e.Cancel = true;
                OnCancelClick(this, new RoutedEventArgs());
            });

            MainWindow.Current.Closing += closingHandler;

            try
            {
                WorkModel.Step = "Searching for packages online...";

                var instDir = Path.Combine(CustomizeModel.InstallDirectory, "Clowd");

                // if chosen directory in local app data, nest it in our default location
                if (instDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase))
                {
                    instDir = PathConstants.AppData;
                }
                else
                {
                    if (!Directory.Exists(instDir)) Directory.CreateDirectory(instDir);
                }

                CustomizeModel.InstallDirectory = instDir;

                var exePath = Path.Combine(instDir, Constants.ClowdExeName);

                // init update manager
                var manager = UpdateHelper.GetUpdaterInstance(instDir, exePath);
                var package = await UpdateHelper.GetLatestChannelReleaseAsync();
                var source = new SimpleWebSource(package.FeedUrl);
                Manager = manager;


                if (WorkModel.CancelRequested) throw new UserAbortException();

                // check for updates
                WorkModel.Step = "Checking metadata...";
                await manager.CheckForUpdatesAsync(source);

                if (WorkModel.CancelRequested) throw new UserAbortException();

                if (manager.UpdatesAvailable > 0)
                {
                    // download updates
                    WorkModel.Step = "Downloading...";
                    WorkModel.ProgressIndeterminate = false;
                    var repDel = new NAppUpdate.Framework.Common.ReportProgressDelegate((e) =>
                    {
                        WorkModel.Step = e.Message;
                        WorkModel.Progress = e.Percentage;
                    });
                    manager.ReportProgress += repDel;
                    await manager.PrepareUpdatesAsync(source);
                    manager.ReportProgress -= repDel;

                    if (WorkModel.CancelRequested) throw new UserAbortException();

                    // install updates
                    WorkModel.Step = "Installing...";
                    WorkModel.CanCancel = false;
                    WorkModel.ProgressIndeterminate = true;
                    await Task.Run(() => manager.ApplyUpdate(false, false, Path.GetTempFileName(), true));
                }

                // add features
                WorkModel.Step = "Installing features...";

                var cp = new ControlPanel();
                cp.Install(exePath);

                if (CustomizeModel.FeatureAutoStart)
                {
                    var at = new AutoStart();
                    at.Install(exePath);
                }

                if (CustomizeModel.FeatureContextMenu)
                {
                    var at = new Installer.Features.ContextMenu();
                    at.Install(exePath);
                }

                if (CustomizeModel.FeatureShortcuts)
                {
                    var at = new Shortcuts();
                    at.Install(exePath);
                }

                WorkModel.Step = " Downloading core plugin - [1/1] obs-express";
                var obs = new Video.ObsModule(null);
                await obs.CheckForUpdates(false);
                if (obs.UpdateAvailable != null)
                {
                    await obs.Install(obs.UpdateAvailable);
                }

                // TODO get info from dll
                //var resolver = new PathAssemblyResolver(new string[] { exePath, Path.Combine(instDir, "mscorlib.dll") });
                //using (var context = new MetadataLoadContext(resolver, "mscorlib"))
                //{
                //    Assembly a = context.LoadFromAssemblyPath(exePath);
                //}

                WorkModel.ProgressIndeterminate = false;
                WorkModel.Progress = 100;
                WorkModel.Step = "Done";
            }
            catch (Exception) when (WorkModel.CancelRequested)
            {
                Cancel();
            }
            finally
            {
                MainWindow.Current.Closing -= closingHandler;
            }
        }

        private void DirDeleteSafeRetry(string directory)
        {
            int retry = 10;
            while (--retry > 0)
            {
                try
                {
                    Directory.Delete(directory, true);
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    if (retry <= 0)
                        throw;
                }
            }
        }

        private async void Uninstall()
        {
            WorkModel.CanCancel = false;
            var exePath = Path.Combine(UninstModel.InstallationDirectory, Constants.ClowdExeName);

            WorkModel.Step = "Removing features...";
            await Task.Delay(100);
            new AutoStart().Uninstall(exePath);
            new Installer.Features.ContextMenu().Uninstall(exePath);
            new Shortcuts().Uninstall(exePath);

            WorkModel.Step = "Closing running processes...";
            await Task.Delay(100);
            await Task.Run(() =>
            {
                foreach (var p in Process.GetProcessesByName("Clowd"))
                    try { p.Kill(); p.WaitForExit(); } catch { }

                foreach (var p in Process.GetProcessesByName("obs-express"))
                    try { p.Kill(); p.WaitForExit(); } catch { }

                foreach (var p in Process.GetProcessesByName("obs64"))
                    try { p.Kill(); p.WaitForExit(); } catch { }
            });

            WorkModel.Step = "Deleting files...";
            await Task.Delay(100);
            await Task.Run(() =>
            {
                if (UninstModel.KeepSettings)
                {
                    DirDeleteSafeRetry(PathConstants.AppData);
                    DirDeleteSafeRetry(PathConstants.BackupData);
                    DirDeleteSafeRetry(PathConstants.UpdateData);
                    DirDeleteSafeRetry(PathConstants.PluginData);
                    DirDeleteSafeRetry(PathConstants.LogData);
                }
                else
                {
                    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd");
                    var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");
                    DirDeleteSafeRetry(UninstModel.InstallationDirectory);
                    DirDeleteSafeRetry(local);
                    DirDeleteSafeRetry(roaming);
                }
            });

            WorkModel.Step = "Finishing...";
            new ControlPanel().Uninstall(exePath);

            WorkModel.ProgressIndeterminate = false;
            WorkModel.Progress = 100;
            WorkModel.Step = "Done";
        }

        private void Cancel()
        {
            if (Manager != null)
                DirDeleteSafeRetry(Manager.Config.TempFolder);

            Manager = null;

            WorkModel.CanCancel = true;
            WorkModel.Step = "Cancelled";
            WorkModel.ProgressIndeterminate = false;
            WorkModel.Progress = 0;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (WorkModel.CanCancel)
            {
                WorkModel.CancelRequested = true;
                if (Manager != null)
                    Manager.Abort();
            }
        }
    }
}
