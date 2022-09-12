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
using Clowd.Config;
using Clowd.PlatformUtil.Windows;
using Clowd.UI.Helpers;
using Clowd.Util;
using NLog;
using NuGet.Versioning;
using Squirrel;

namespace Clowd
{
    internal static class SquirrelUtil
    {
        public static string CurrentVersion => ThisAssembly.AssemblyInformationalVersion;
        public static bool IsFirstRun { get; private set; }
        public static bool JustRestarted { get; private set; }
        public static bool IsInstalled { get; private set; }

        private static string UniqueAppKey => "Clowd";
        private static readonly object _lock = new object();
        private static SquirrelUpdateViewModel _model;
        private static InstallerServices _srv;
        private static DateTime _startTime;

        /// <summary>
        /// Handles Squirrel startup arguments, configures auto-updates, and returns remaining non-squirrel arguments
        /// </summary>
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
            _model = new SquirrelUpdateViewModel();
            _startTime = DateTime.Now;
            return args.Where(a => !a.Contains("--squirrel", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        public static void SetAutoStart(bool isEnabled)
        {
            if (isEnabled)
            {
                _srv.AutoStartLaunchPath = SquirrelRuntimeInfo.EntryExePath;
            }
            else
            {
                _srv.AutoStartLaunchPath = null;
            }
        }

        public static void SetExplorerMenu(bool isEnabled)
        {
            if (isEnabled)
            {
                var menu = new ExplorerMenuLaunchItem("Upload with Clowd", SquirrelRuntimeInfo.EntryExePath, SquirrelRuntimeInfo.EntryExePath);
                _srv.ExplorerAllFilesMenu = menu;
                _srv.ExplorerDirectoryMenu = menu;
            }
            else
            {
                _srv.ExplorerDirectoryMenu = null;
                _srv.ExplorerAllFilesMenu = null;
            }
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
        }

        private static void OnUpdate(SemanticVersion ver, IAppTools tools)
        {
            tools.CreateUninstallerRegistryEntry();
            tools.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);
        }

        private static void OnUninstall(SemanticVersion ver, IAppTools tools)
        {
            _srv.RemoveAll();
        }

        private static void OnEveryRun(SemanticVersion ver, IAppTools tools, bool firstRun)
        {
            IsFirstRun = firstRun;
            IsInstalled = tools.CurrentlyInstalledVersion() != null;
        }

        public class SquirrelUpdateViewModel : SimpleNotifyObject
        {
            public bool ContextMenuRegistered
            {
                get => SettingsRoot.Current.General.RegisterExplorerContextMenu;
                set
                {
                    SettingsRoot.Current.General.RegisterExplorerContextMenu = value;
                    SetExplorerMenu(value);
                }
            }

            public bool AutoRunRegistered
            {
                get => SettingsRoot.Current.General.RegisterAutoStart;
                set
                {
                    SettingsRoot.Current.General.RegisterAutoStart = value;
                    SetAutoStart(value);
                }
            }

            public RelayCommand ClickCommand
            {
                get => _clickCommand;
                protected set => Set(ref _clickCommand, value);
            }

            public string ClickCommandText
            {
                get => _clickCommandText;
                protected set => Set(ref _clickCommandText, value);
            }

            public string Description
            {
                get => _description;
                protected set => Set(ref _description, value);
            }

            public bool IsWorking
            {
                get => _isWorking;
                protected set => Set(ref _isWorking, value);
            }

            private ReleaseEntry _newVersion;
            private IDisposable _timer;
            private RelayCommand _clickCommand;
            private string _clickCommandText;
            private string _description;
            private bool _isWorking;
            private DateTime? _manuallyCheckedForUpdate;

            private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

            public SquirrelUpdateViewModel()
            {
                ClickCommand = new RelayCommand()
                {
                    Executed = OnClick,
                    CanExecute = CanExecute
                };
                _timer = DisposableTimer.Start(TimeSpan.FromMinutes(30), CheckForUpdateTimer);
                UpdateStateText();
            }

            private void UpdateStateText()
            {
                if (!IsInstalled)
                {
                    IsWorking = true;
                    ClickCommandText = "Not Available";
                    Description = "Can't check for updates in portable mode";
                    return;
                }

                if (_newVersion != null)
                {
                    ClickCommandText = "Restart Clowd";
                    Description = $"Version {_newVersion.Version} is ready to be installed";
                    return;
                }

                ClickCommandText = "Check for Updates";
                if (_manuallyCheckedForUpdate.HasValue && (DateTime.Now - _manuallyCheckedForUpdate.Value) < TimeSpan.FromMinutes(5))
                {
                    Description = "Version: " + CurrentVersion + ", no update available";
                }
                else if (JustRestarted && (DateTime.Now - _startTime) < TimeSpan.FromMinutes(30))
                {
                    Description = $"Version: {CurrentVersion}, just updated!";
                }
                else
                {
                    Description = "Version: " + CurrentVersion;
                }
            }

            private void CheckForUpdateTimer()
            {
                if (_newVersion != null)
                {
                    // restart automatically if update waiting to install and system is idle
                    var idleTime = PlatformUtil.Platform.Current.GetSystemIdleTime();
                    if (idleTime > TimeSpan.FromHours(6))
                    {
                        RestartApp(false);
                    }
                }
                else
                {
                    CheckForUpdatesUnattended();
                }
            }

            public async Task CheckForUpdatesUnattended()
            {
                Exception ex = null;
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
                    using var mgr = new UpdateManager(SettingsRoot.Current.General.UpdateReleaseUrl);
                    _newVersion = await mgr.UpdateApp(OnProgress);
                }
                catch (Exception e)
                {
                    ex = e;
                    _log.Error(ex, "Failed to check for updates");
                }
                finally
                {
                    UpdateStateText();
                    if (ex != null) Description = ex.Message;
                    lock (_lock) IsWorking = false;
                    CommandManager.InvalidateRequerySuggested();
                }
            }

            private async void OnClick(object parameter)
            {
                if (_newVersion == null)
                {
                    // no update downloaded, lets check
                    _manuallyCheckedForUpdate = DateTime.Now;
                    await CheckForUpdatesUnattended();
                }
                else
                {
                    RestartApp(true);
                }
            }

            private void RestartApp(bool notifyUser)
            {
                var arguments = notifyUser ? "--squirrel-restarted" : "";
                UpdateManager.RestartAppWhenExited(arguments: arguments);
                App.Current.ExitApp();
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

    internal class SquirrelLogger : Squirrel.SimpleSplat.ILogger
    {
        private readonly NLog.Logger _log;

        protected SquirrelLogger()
        {
            _log = NLog.LogManager.GetLogger("Squirrel");
        }

        public Squirrel.SimpleSplat.LogLevel Level { get; set; }

        public static void Register()
        {
            Squirrel.SimpleSplat.SquirrelLocator.CurrentMutable.Register(() => new SquirrelLogger(), typeof(Squirrel.SimpleSplat.ILogger));
        }

        public void Write(string message, Squirrel.SimpleSplat.LogLevel logLevel)
        {
            switch (logLevel)
            {
                case Squirrel.SimpleSplat.LogLevel.Debug:
                    _log.Debug(message);
                    break;
                case Squirrel.SimpleSplat.LogLevel.Info:
                    _log.Info(message);
                    break;
                case Squirrel.SimpleSplat.LogLevel.Warn:
                    _log.Warn(message);
                    break;
                case Squirrel.SimpleSplat.LogLevel.Error:
                    _log.Error(message);
                    break;
                case Squirrel.SimpleSplat.LogLevel.Fatal:
                    _log.Fatal(message);
                    break;
                default:
                    _log.Info(message);
                    break;
            }
        }
    }
}
