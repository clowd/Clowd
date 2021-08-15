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
using Squirrel;

[assembly: AssemblyMetadata("SquirrelAwareVersion", "1")]

namespace Clowd
{
    internal class SquirrelUpdateViewModel : INotifyPropertyChanged
    {
        public RelayUICommand ClickCommand { get; protected set; }
        public string ClickCommandText { get; protected set; }
        public string Description { get; protected set; }
        public bool IsWorking { get; protected set; }

        public event PropertyChangedEventHandler PropertyChanged;

        private static string UniqueAppKey => "Clowd";
        private ReleaseEntry _newVersion;
        private static readonly object _lock = new object();

        public SquirrelUpdateViewModel()
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);

            ClickCommand = new RelayUICommand(OnClick, CanExecute);
            if (mgr.IsInstalledApp)
            {
                ClickCommandText = "Check for updates";
                Description = "Version: " + Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    .InformationalVersion;
            }
            else
            {
                IsWorking = true;
                ClickCommandText = "Not Available";
                Description = "Can't check for updates in portable app";
            }
        }

        private async void OnClick(object parameter)
        {
            lock (_lock)
            {
                if (IsWorking) return;
                IsWorking = true;
            }

            CommandManager.InvalidateRequerySuggested();
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);

            if (_newVersion == null)
            {
                // no update downloaded, lets check
                ClickCommandText = "Checking...";
                _newVersion = await mgr.UpdateApp(OnProgress);

                if (_newVersion != null)
                {
                    ClickCommandText = "Restart";
                    Description = $"Version {_newVersion.Version} has been downloaded";
                }
                else
                {
                    ClickCommandText = "Check for Updates";
                    Description = "Version: " + Assembly.GetExecutingAssembly()
                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                      .InformationalVersion + ", no update available";
                }
            }
            else
            {
                var newAppPath = Path.Combine(mgr.RootAppDirectory, "app-" + _newVersion.Version.ToString(), "Clowd.exe");
                UpdateManager.RestartApp(newAppPath);
            }

            lock (_lock)
                IsWorking = false;

            CommandManager.InvalidateRequerySuggested();
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

    internal static class SquirrelUtil
    {
        public static string UniqueAppKey => "Clowd";

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

        private static void OnInstall(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath);
            srv.ExplorerAllFilesMenu = menu;
            srv.ExplorerDirectoryMenu = menu;
            srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUpdate(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            // only update registry during update if they have not been removed by user
            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath);
            if (srv.ExplorerAllFilesMenu != null)
                srv.ExplorerAllFilesMenu = menu;
            if (srv.ExplorerDirectoryMenu != null)
                srv.ExplorerDirectoryMenu = menu;
            if (srv.AutoStartLaunchPath != null)
                srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUninstall(Version obj)
        {
            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            srv.RemoveAll();

            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.RemoveShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);
            mgr.RemoveUninstallerRegistryEntry();
        }

        private static void OnFirstRun()
        {
            MessageBox.Show("Thanks for installing clowd, it is running in the system tray!");
        }
    }
}
