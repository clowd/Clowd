using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
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
        public UpdateManager Manager { get; private set; }

        public DoWorkView() : this(true, new CustomizeViewModel())
        {
        }

        public DoWorkView(bool installing, CustomizeViewModel model)
        {
            CustomizeModel = model;
            WorkModel = new DoWorkViewModel();
            this.DataContext = WorkModel;
            InitializeComponent();
            Install();
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
                    instDir = PathConstants.App;
                }
                else
                {
                    if (!Directory.Exists(instDir)) Directory.CreateDirectory(instDir);
                }

                CustomizeModel.InstallDirectory = instDir;

                var exePath = Path.Combine(instDir, "Clowd.exe");

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

                // TODO get info from dll
                //var resolver = new PathAssemblyResolver(new string[] { exePath, Path.Combine(instDir, "mscorlib.dll") });
                //using (var context = new MetadataLoadContext(resolver, "mscorlib"))
                //{
                //    Assembly a = context.LoadFromAssemblyPath(exePath);
                //}

                WorkModel.ProgressIndeterminate = false;
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

        private void Uninstall()
        {

        }

        private void Cancel()
        {
            if (Manager != null)
                Directory.Delete(Manager.Config.TempFolder, true);

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
