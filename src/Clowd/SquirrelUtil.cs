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

        public static bool IsFirstRun { get; private set; }
        public static bool JustRestarted { get; private set; }

        private static string UniqueAppKey => "Clowd";
        private static readonly object _lock = new object();
        private static SquirrelUpdateViewModel _model;
        private static InstallerServices _srv;

        public static string[] Startup(string[] args)
        {
            _srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);

            SquirrelAwareApp.HandleEvents(
                onInitialInstall: OnInstall,
                onAppUpdate: OnUpdate,
                onAppUninstall: OnUninstall,
                onEveryRun: OnEveryRun,
                arguments: args);

            JustRestarted = args.Contains("--squirrel-restarted", StringComparer.OrdinalIgnoreCase);

            // if app is still running, filter out squirrel args and continue
            return args.Where(a => !a.Contains("--squirrel", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        public static SquirrelUpdateViewModel GetUpdateViewModel()
        {
            if (_model == null)
                throw new InvalidOperationException("Can't update before app has been initialized");
            return _model;
        }

        private static void OnInstall(SemanticVersion ver, IAppTools tools)
        {
            tools.CreateUninstallerRegistryEntry();
            tools.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", SquirrelRuntimeInfo.EntryExePath, SquirrelRuntimeInfo.EntryExePath);
            _srv.ExplorerAllFilesMenu = menu;
            _srv.ExplorerDirectoryMenu = menu;
            _srv.AutoStartLaunchPath = SquirrelRuntimeInfo.EntryExePath;
        }

        private static void OnUpdate(SemanticVersion ver, IAppTools tools)
        {
            tools.CreateUninstallerRegistryEntry();
            tools.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            // only update registry during update if they have not been removed by user
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", SquirrelRuntimeInfo.EntryExePath, SquirrelRuntimeInfo.EntryExePath);
            if (_srv.ExplorerAllFilesMenu != null)
                _srv.ExplorerAllFilesMenu = menu;
            if (_srv.ExplorerDirectoryMenu != null)
                _srv.ExplorerDirectoryMenu = menu;
            if (_srv.AutoStartLaunchPath != null)
                _srv.AutoStartLaunchPath = SquirrelRuntimeInfo.EntryExePath;
        }

        private static void OnUninstall(SemanticVersion ver, IAppTools tools)
        {
            _srv.RemoveAll();

            tools.RemoveShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);
            tools.RemoveUninstallerRegistryEntry();
        }

        private static void OnEveryRun(SemanticVersion ver, IAppTools tools, bool firstRun)
        {
            IsFirstRun = firstRun;
            tools.SetProcessAppUserModelId();
            _model = new SquirrelUpdateViewModel(JustRestarted, tools.CurrentlyInstalledVersion() != null);
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
                        var menu = new ExplorerMenuLaunchItem("Upload with Clowd", SquirrelRuntimeInfo.EntryExePath, SquirrelRuntimeInfo.EntryExePath);
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
                set => _srv.AutoStartLaunchPath = value ? SquirrelRuntimeInfo.EntryExePath : null;
            }

            public RelayUICommand ClickCommand { get; protected set; }
            public string ClickCommandText { get; protected set; }
            public string Description { get; protected set; }
            public bool IsWorking { get; protected set; }

            public event PropertyChangedEventHandler PropertyChanged;

            private ReleaseEntry _newVersion;
            private IDisposable _timer;

            public SquirrelUpdateViewModel(bool justUpdated, bool isInstalled)
            {
                ClickCommand = new RelayUICommand(OnClick, CanExecute);
                if (isInstalled)
                {
                    ClickCommandText = "Check for updates";
                    Description = "Version: " + Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        .InformationalVersion;
                    _timer = DisposableTimer.Start(TimeSpan.FromMinutes(5), CheckForUpdateTimer);

                    if (justUpdated)
                        Description += ", just updated!";
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
                try
                {
                    lock (_lock)
                    {
                        if (_newVersion != null) return;
                        if (IsWorking) return;
                        IsWorking = true;
                    }

                    CommandManager.InvalidateRequerySuggested();
                    ClickCommandText = "Checking...";
                    using var mgr = new UpdateManager(Config.SettingsRoot.Current.General.UpdateReleaseUrl);
                    _newVersion = await mgr.UpdateApp(OnProgress);
                }
                finally
                {
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

                    lock (_lock) IsWorking = false;
                    CommandManager.InvalidateRequerySuggested();
                }
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
                    await UpdateManager.RestartAppWhenExited(arguments: "--squirrel-restarted");
                    App.Current.ExitApp();
                }
            }

            private void OnProgress(int obj)
            {
                if (obj < 33)
                    Description = $"Checking for updates: {obj}%";
                else
                    Description = $"Downloading updates: {obj}%";
            }

            private bool CanExecute(object parameter)
            {
                return !IsWorking;
            }
        }
    }
}
