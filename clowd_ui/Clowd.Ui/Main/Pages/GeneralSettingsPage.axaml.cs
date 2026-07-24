using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Config;

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
            BindEnumCombo(UpdateIntervalCombo, nameof(SettingsGeneral.UpdateCheckInterval), typeof(UpdateInterval));

            InitializeUpdateGroup();
            InitializeAutoStart();
            InitializeContextMenu();
        }

        // ---- Shell group ----

        /// <summary>Same shape as the auto-start checkbox: the box writes the setting, App applies it
        /// to the registry, and this reports back only when that failed.</summary>
        private void InitializeContextMenu()
        {
            if (!ExplorerContextMenuManager.IsSupported)
            {
                ContextMenuCheck.IsEnabled = false;
                ContextMenuCaption.Text = "The Explorer context menu is only available on Windows.";
                return;
            }

            AttachedToVisualTree += (s, e) =>
            {
                ExplorerContextMenuManager.StateChanged += OnContextMenuStateChanged;
                ShowContextMenuCaption();
            };

            DetachedFromVisualTree += (s, e) => ExplorerContextMenuManager.StateChanged -= OnContextMenuStateChanged;
        }

        private void OnContextMenuStateChanged(object sender, EventArgs e) =>
            Dispatcher.UIThread.Post(ShowContextMenuCaption);

        private void ShowContextMenuCaption()
        {
            if (ExplorerContextMenuManager.LastError is { } err)
            {
                ContextMenuCaption.Text = "Could not change the context menu setting: " + err;
                return;
            }

            // Windows 11 reserves its compact menu for packaged apps and pushes classic verbs like
            // this one into the overflow, so say where to actually look for it.
            ContextMenuCaption.Text = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? "Right-click a file or folder and choose \"Show more options\" to find it."
                : "Right-click a file or folder to find it.";
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

        private const string StableChannelLabel = "Stable";
        private const string PrereleaseChannelLabel = "Pre-release";

        // set while the channel combo is being populated, so seeding the selection doesn't look like
        // the user picking a channel (which would kick off a switch + check).
        private bool _settingChannelSelection;

        /// <summary>The update group renders <see cref="UpdateService"/>'s state rather than owning
        /// any of its own: the background scheduler can check, download and stage an update while
        /// this page is closed, or while it is open and untouched.</summary>
        private void InitializeUpdateGroup()
        {
            InitializeChannelCombo();
            RenderUpdateState();

            // UpdateService outlives the settings window (PageManager evicts the window on close), so
            // like AutoStartManager the subscription is tied to the visual tree.
            AttachedToVisualTree += (s, e) =>
            {
                UpdateService.Default.StateChanged += OnUpdateStateChanged;
                RenderUpdateState();
            };

            DetachedFromVisualTree += (s, e) => UpdateService.Default.StateChanged -= OnUpdateStateChanged;
        }

        private void OnUpdateStateChanged(object sender, EventArgs e) =>
            Dispatcher.UIThread.Post(RenderUpdateState);

        private void RenderUpdateState()
        {
            var update = UpdateService.Default;

            VersionText.Text = "Current version: " + update.CurrentVersion
                               + (update.IsPrereleaseChannel ? " (pre-release)" : "");

            UpdateStatusText.Text = update.StatusMessage;
            UpdateStatusText.IsVisible = !String.IsNullOrEmpty(update.StatusMessage);

            UpdateProgress.IsVisible = update.State == UpdateState.Downloading;
            UpdateProgress.Value = update.DownloadProgress;

            UpdateButton.Content = update.State switch
            {
                UpdateState.ReadyToRestart => "Restart to Update",
                UpdateState.UpdateAvailable => "Download Update",
                _ => "Check for Updates",
            };

            UpdateButton.IsEnabled = update.IsSupported
                                     && update.State != UpdateState.Checking
                                     && update.State != UpdateState.Downloading;

            // shown even where it cannot be used (a loose build has no installed channel to switch
            // away from) — a missing control reads as a missing feature.
            ChannelSection.IsEnabled = update.CanSwitchChannel;
            ChannelHelpText.Text = !update.CanSwitchChannel
                ? "The update channel can only be changed in an installed build."
                : update.IsPrereleaseChannel
                    ? "Pre-release builds get new features first, and are more likely to contain bugs."
                    : "Stable builds only. Switching channel checks for a new version straight away.";

            SetChannelSelection(update.IsPrereleaseChannel);
        }

        private void InitializeChannelCombo()
        {
            _settingChannelSelection = true;
            try
            {
                ChannelCombo.ItemsSource = new[] { StableChannelLabel, PrereleaseChannelLabel };
            }
            finally
            {
                _settingChannelSelection = false;
            }
        }

        private void SetChannelSelection(bool prerelease)
        {
            var index = prerelease ? 1 : 0;
            if (ChannelCombo.SelectedIndex == index)
                return;

            _settingChannelSelection = true;
            try
            {
                ChannelCombo.SelectedIndex = index;
            }
            finally
            {
                _settingChannelSelection = false;
            }
        }

        private async void OnChannelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingChannelSelection)
                return;

            await UpdateService.Default.SetPrereleaseChannelAsync(ChannelCombo.SelectedIndex == 1);
        }

        private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            var update = UpdateService.Default;

            switch (update.State)
            {
                case UpdateState.ReadyToRestart:
                    update.ApplyUpdatesAndRestart();
                    break;

                case UpdateState.UpdateAvailable:
                    await update.DownloadAvailableUpdateAsync();
                    break;

                default:
                    await update.CheckForUpdatesAsync();
                    break;
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
