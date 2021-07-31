using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clowd.Setup;
using NAppUpdate.Framework;
using PropertyChanged;

namespace Clowd.UI.Config
{
    [AddINotifyPropertyChangedInterface]
    public partial class UpdaterControl : UserControl
    {
        public string CurrentVersion { get; set; }
        public string Status { get; set; }
        public string ActionText { get; set; }
        public bool CanDoAction { get; set; }

        private UpdateManager _manager;
        private UpdatePackage _package;

        public UpdaterControl()
        {
            CurrentVersion = UpdateHelper.GetCurrentVersion();
            CanDoAction = true;
            var myDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _manager = UpdateHelper.GetUpdaterInstance(myDir, System.Reflection.Assembly.GetExecutingAssembly().Location);
            _manager.ReportProgress += _manager_ReportProgress;

            UpdateState();

            InitializeComponent();
        }

        private void _manager_ReportProgress(NAppUpdate.Framework.Common.UpdateProgressInfo currentStatus)
        {
            Status = $"Downloading {currentStatus.Percentage}%";
        }

        private async void Action_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.IsWorking)
                return;

            if (_package == null)
            {
                _package = await UpdateHelper.GetLatestChannelReleaseAsync();
            }

            var pkg = _package;
            var source = new NAppUpdate.Framework.Sources.SimpleWebSource(pkg.FeedUrl);

            switch (_manager.State)
            {
                case UpdateManager.UpdateProcessState.NotChecked:
                    UpdateState("", "Checking for updates...", false);
                    await _manager.CheckForUpdatesAsync(source);
                    UpdateState();
                    break;

                case UpdateManager.UpdateProcessState.Checked:
                    if (_manager.UpdatesAvailable > 0)
                    {
                        UpdateState("", "Downloading updates...", false);
                        await _manager.PrepareUpdatesAsync(source);
                        UpdateState();
                    }
                    else
                    {
                        UpdateState("", "Checking for updates...", false);
                        await _manager.CheckForUpdatesAsync(source);
                        UpdateState();
                    }
                    break;

                case UpdateManager.UpdateProcessState.Prepared:
                    //if (Debugger.IsAttached)
                    //{
                    //    var result = await NiceDialog.ShowPromptAsync(this, NiceDialogIcon.Information,
                    //        "You are recieving this notification because the debugger is currently attached and Clowd is about to be restarted to install updates. " +
                    //        $"This will detach the debugger and replace the current binaries with versions downloaded from {pkg.FeedUrl}.", "Proceed?");

                    //    if (result)
                    //    {
                    //        _manager.ApplyUpdates(true, true, true);
                    //    }
                    //}
                    //else
                    //{
                    //    _manager.ApplyUpdates(true, true, false);
                    //}
                    break;

                    //case UpdateManager.UpdateProcessState.RollbackRequired:
                    //    break;
                    //case UpdateManager.UpdateProcessState.AfterRestart:
                    //    break;
                    //case UpdateManager.UpdateProcessState.AppliedSuccessfully:
                    //    break;
            }
        }

        private void UpdateState(string status, string action, bool canAction)
        {
            Status = status;
            ActionText = action;
            CanDoAction = canAction;
        }

        private void UpdateState()
        {
            if (_manager.IsWorking)
            {
                UpdateState("", "Working...", false);
                return;
            }

            var newVer = $"{_package?.Version}-{_package?.Channel}";

            if (_manager.UpdatesAvailable > 0 && CurrentVersion.Equals(newVer, StringComparison.OrdinalIgnoreCase))
            {
                if (_manager.State == UpdateManager.UpdateProcessState.Checked)
                {
                    UpdateState($"Modified installation detected", "Download repairs", true);
                }
                else if (_manager.State == UpdateManager.UpdateProcessState.Prepared)
                {
                    UpdateState($"Modified installation detected", "Install repairs", true);
                }
                return;
            }

            switch (_manager.State)
            {
                case UpdateManager.UpdateProcessState.NotChecked:
                    UpdateState("", "Check for updates", true);
                    break;

                case UpdateManager.UpdateProcessState.Checked:
                    if (_manager.UpdatesAvailable > 0)
                        UpdateState($"Update available: {newVer}", "Download updates", true);
                    else
                        UpdateState("This is the latest version", "Check for updates", true);
                    break;

                case UpdateManager.UpdateProcessState.Prepared:
                    UpdateState("Updates are downloaded", "Install updates", true);
                    break;

                default:
                    UpdateState("", "Check for updates", true);
                    break;

                    //case UpdateManager.UpdateProcessState.RollbackRequired:
                    //    break;
                    //case UpdateManager.UpdateProcessState.AfterRestart:
                    //    break;
                    //case UpdateManager.UpdateProcessState.AppliedSuccessfully:
                    //    break;
            }
        }
    }
}
