using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Config;
using Velopack;

namespace Clowd.UI.Pages
{
    public partial class GeneralSettingsPage : UserControl
    {
        public GeneralSettingsPage()
        {
            InitializeComponent();
            DataContext = SettingsRoot.Current.General;

            BindEnumCombo(ThemeCombo, nameof(SettingsGeneral.Theme), typeof(AppTheme));
            BindEnumCombo(TrayClickCombo, nameof(SettingsGeneral.TrayClick), typeof(TrayClickAction));

            InitializeUpdateGroup();
            InitializeAutoStart();
        }

        // ---- Auto-start ----

        /// <summary>The checkbox writes the setting; App applies it to the OS (registry key /
        /// LaunchAgent). This only reports back when that failed — a stale checkbox that silently
        /// does nothing is worse than no checkbox.</summary>
        private void InitializeAutoStart()
        {
            if (!AutoStartManager.IsSupported)
            {
                AutoStartCheck.IsEnabled = false;
                AutoStartStatusText.Text = "Starting Clowd at login is not supported on this platform.";
                AutoStartStatusText.IsVisible = true;
                return;
            }

            // StateChanged is static and the settings window is rebuilt every time it is reopened
            // (PageManager evicts it on close), so the subscription has to be tied to the visual
            // tree or each open would leak another handler.
            AttachedToVisualTree += (s, e) =>
            {
                AutoStartManager.StateChanged += OnAutoStartStateChanged;
                ShowAutoStartError();
            };

            DetachedFromVisualTree += (s, e) => AutoStartManager.StateChanged -= OnAutoStartStateChanged;
        }

        private void OnAutoStartStateChanged(object sender, EventArgs e) =>
            Dispatcher.UIThread.Post(ShowAutoStartError);

        private void ShowAutoStartError()
        {
            AutoStartStatusText.Text = AutoStartManager.LastError is { } err
                ? "Could not change the startup setting: " + err
                : null;
            AutoStartStatusText.IsVisible = AutoStartStatusText.Text != null;
        }

        // ---- Updates group ----

        private UpdateInfo _availableUpdate;
        private bool _updateBusy;

        private void InitializeUpdateGroup()
        {
            VersionText.Text = "Current version: " + UpdateService.Default.CurrentVersion;

            if (!UpdateService.Default.IsSupported)
            {
                UpdateButton.IsEnabled = false;
                UpdateStatusText.Text = "Automatic updates are not available in this build.";
                return;
            }

            if (UpdateService.Default.UpdatePendingRestart != null)
            {
                UpdateButton.Content = "Restart to Update";
                UpdateStatusText.Text = "An update has been downloaded and will be applied on restart.";
                return;
            }

            // silent check when the page opens; the button reflects the result.
            _ = CheckForUpdates(interactive: false);
        }

        private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            if (_updateBusy)
                return;

            if (UpdateService.Default.UpdatePendingRestart is { } pending)
            {
                UpdateService.Default.ApplyUpdatesAndRestart(pending);
                return;
            }

            if (_availableUpdate != null)
            {
                await DownloadUpdate(_availableUpdate);
                return;
            }

            await CheckForUpdates(interactive: true);
        }

        private async System.Threading.Tasks.Task CheckForUpdates(bool interactive)
        {
            _updateBusy = true;
            try
            {
                UpdateStatusText.Text = "Checking for updates…";
                var info = await UpdateService.Default.CheckForUpdatesAsync();
                _availableUpdate = info;
                if (info != null)
                {
                    UpdateButton.Content = "Download Update";
                    UpdateStatusText.Text = $"Version {info.TargetFullRelease.Version} is available.";
                }
                else
                {
                    UpdateStatusText.Text = "You are using the latest version.";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = interactive ? "Update check failed: " + ex.Message : "";
            }
            finally
            {
                _updateBusy = false;
            }
        }

        private async System.Threading.Tasks.Task DownloadUpdate(UpdateInfo info)
        {
            _updateBusy = true;
            try
            {
                UpdateButton.IsEnabled = false;
                UpdateProgress.IsVisible = true;
                UpdateStatusText.Text = $"Downloading version {info.TargetFullRelease.Version}…";
                await UpdateService.Default.DownloadUpdatesAsync(info,
                    p => Dispatcher.UIThread.Post(() => UpdateProgress.Value = p));
                UpdateButton.Content = "Restart to Update";
                UpdateStatusText.Text = "Update downloaded. Restart Clowd to finish installing.";
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "Download failed: " + ex.Message;
            }
            finally
            {
                UpdateProgress.IsVisible = false;
                UpdateButton.IsEnabled = true;
                _updateBusy = false;
            }
        }

        /// <summary>Enum ComboBox with [Description] display names, same as the generated
        /// settings pages (SettingsControlFactory).</summary>
        private void BindEnumCombo(ComboBox combo, string propertyName, Type enumType)
        {
            combo.ItemTemplate = new FuncDataTemplate<object>((o, ns) =>
                new TextBlock { Text = SettingsControlFactory.GetEnumDisplayString(o) });
            combo.ItemsSource = Enum.GetValues(enumType);
            combo.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(propertyName)
            {
                Mode = Avalonia.Data.BindingMode.TwoWay,
            });
        }
    }
}
