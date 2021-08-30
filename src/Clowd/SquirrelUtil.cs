using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Clowd.PlatformUtil.Windows;
using Clowd.UI.Helpers;
using Clowd.Util;
using Squirrel;

[assembly: AssemblyMetadata("SquirrelAwareVersion", "1")]

namespace Clowd
{
    internal static class SquirrelUtil
    {
        public static string CurrentVersion { get; } = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion;

        public static bool IsFirstRun { private set; get; }

        private static string UniqueAppKey => "Clowd";
        private static readonly object _lock = new object();
        private static SquirrelUpdateViewModel _model;
        private static InstallerServices _srv;

        static SquirrelUtil()
        {
            _model = new SquirrelUpdateViewModelInst();
            _srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
        }

        public static string[] Startup(string[] args)
        {
            SquirrelAwareApp.HandleEvents(
                onInitialInstall: OnInstall,
                onAppUpdate: OnUpdate,
                onAppUninstall: OnUninstall,
                onFirstRun: OnFirstRun,
                arguments: args);

            // if app is still running, filter out squirrel args and continue
            return args.Where(a => !a.Contains("--squirrel", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        public static SquirrelUpdateViewModel GetUpdateViewModel()
        {
            return _model;
        }

        private static void OnInstall(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath);
            _srv.ExplorerAllFilesMenu = menu;
            _srv.ExplorerDirectoryMenu = menu;
            _srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUpdate(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            // only update registry during update if they have not been removed by user
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath);
            if (_srv.ExplorerAllFilesMenu != null)
                _srv.ExplorerAllFilesMenu = menu;
            if (_srv.ExplorerDirectoryMenu != null)
                _srv.ExplorerDirectoryMenu = menu;
            if (_srv.AutoStartLaunchPath != null)
                _srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUninstall(Version obj)
        {
            _srv.RemoveAll();

            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.RemoveShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);
            mgr.RemoveUninstallerRegistryEntry();
        }

        private static void OnFirstRun()
        {
            IsFirstRun = true;
        }

        private class SquirrelUpdateViewModelInst : SquirrelUpdateViewModel
        {
            public SquirrelUpdateViewModelInst() : base()
            {
                // hide constructor
            }
        }

        public class SquirrelUpdateViewModel : INotifyPropertyChanged
        {
            public bool ContextMenuRegistered
            {
                get
                {
                    var files = _srv.ExplorerAllFilesMenu;
                    var directory = _srv.ExplorerDirectoryMenu;
                    if (files == null || directory == null)
                        return false;
                    return true;
                }
                set
                {
                    if (value)
                    {
                        var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath);
                        _srv.ExplorerAllFilesMenu = menu;
                        _srv.ExplorerDirectoryMenu = menu;
                    }
                    else
                    {
                        _srv.ExplorerAllFilesMenu = null;
                        _srv.ExplorerDirectoryMenu = null;
                    }
                }
            }

            public bool AutoRunRegistered
            {
                get => _srv.AutoStartLaunchPath != null;
                set => _srv.AutoStartLaunchPath = value ? AssemblyRuntimeInfo.EntryExePath : null;
            }

            public RelayUICommand ClickCommand { get; protected set; }
            public string ClickCommandText { get; protected set; }
            public string Description { get; protected set; }
            public bool IsWorking { get; protected set; }

            public event PropertyChangedEventHandler PropertyChanged;

            private ReleaseEntry _newVersion;
            private IDisposable _timer;

            protected SquirrelUpdateViewModel()
            {
                using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);

                ClickCommand = new RelayUICommand(OnClick, CanExecute);
                if (mgr.IsInstalledApp)
                {
                    ClickCommandText = "Check for updates";
                    Description = "Version: " + Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        .InformationalVersion;
                    _timer = DisposableTimer.Start(TimeSpan.FromHours(1), CheckForUpdateTimer);
                }
                else
                {
                    IsWorking = true;
                    ClickCommandText = "Not Available";
                    Description = "Can't check for updates in portable mode";
                }
            }

            private void CheckForUpdateTimer()
            {
                if (_newVersion != null) return;
                CheckForUpdatesUnattended().ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        Description = t.Exception.Message;
                        // log this
                    }
                });
            }

            public async Task CheckForUpdatesUnattended()
            {
                lock (_lock)
                {
                    if (_newVersion != null) return;
                    if (IsWorking) return;
                    IsWorking = true;
                }

                CommandManager.InvalidateRequerySuggested();

                ClickCommandText = "Checking...";
                using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
                _newVersion = await mgr.UpdateApp(OnProgress);

                if (_newVersion != null)
                {
                    ClickCommandText = "Restart Clowd";
                    Description = $"Version {_newVersion.Version} has been downloaded";
                }
                else
                {
                    ClickCommandText = "Check for Updates";
                    Description = "Version: " + CurrentVersion + ", no update available";
                }

                lock (_lock)
                    IsWorking = false;

                CommandManager.InvalidateRequerySuggested();
            }

            private async void OnClick(object parameter)
            {
                if (_newVersion == null)
                {
                    // no update downloaded, lets check
                    await CheckForUpdatesUnattended();
                }
                else
                {
                    using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
                    var newAppPath = Path.Combine(mgr.RootAppDirectory, "app-" + _newVersion.Version.ToString(), "Clowd.exe");
                    await UpdateManager.RestartAppWhenExited(newAppPath);
                    App.Current.ExitApp();
                }
            }

            private void OnProgress(int obj)
            {
                Description = $"Checking for updates: {obj}%";
            }

            private bool CanExecute(object parameter)
            {
                return !IsWorking;
            }
        }
    }
}
