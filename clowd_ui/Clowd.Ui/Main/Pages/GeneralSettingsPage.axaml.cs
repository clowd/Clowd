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
            InitializePermissions();
        }

        // ---- Permissions group (macOS only) ----

        /// <summary>
        /// The macOS privacy permissions Clowd depends on (issue #49): Screen Recording for capture,
        /// recording and the eyedropper, Accessibility for the global hotkeys. Neither is something
        /// Clowd can grant itself, so this group's whole job is to say where each one stands and get
        /// the user to the right System Settings pane. It sits at the top of the page on macOS — an
        /// ungranted Screen Recording permission makes every other setting here moot.
        /// </summary>
        /// <remarks>
        /// The group renders live state rather than a setting: the user leaves for System Settings,
        /// flips a switch and comes back, so the statuses are re-read on every window activation as
        /// well as on <see cref="MacPermissions.StateChanged"/>. Both permissions only take effect
        /// for a process at launch, which is why a granted-but-not-yet-restarted state still tells
        /// the user to restart.
        /// </remarks>
        private void InitializePermissions()
        {
            if (!MacPermissions.IsRelevant)
                return;

            PermissionsGroup.IsVisible = true;
            RenderPermissions();

            // MacPermissions is static and this page is rebuilt on every settings open (PageManager
            // evicts the window on close), so the subscriptions are tied to the visual tree — same
            // shape as the auto-start and update groups above.
            AttachedToVisualTree += (s, e) =>
            {
                MacPermissions.StateChanged += OnPermissionsChanged;

                // the window is remembered rather than looked up again on detach: by then this
                // control may already be off the tree, GetTopLevel would answer null, and the
                // Activated handler would be left attached to a window that outlives the page.
                _activationSource = TopLevel.GetTopLevel(this) as Window;
                if (_activationSource != null)
                    _activationSource.Activated += OnWindowActivated;

                RenderPermissions();
            };

            DetachedFromVisualTree += (s, e) =>
            {
                MacPermissions.StateChanged -= OnPermissionsChanged;
                if (_activationSource != null)
                {
                    _activationSource.Activated -= OnWindowActivated;
                    _activationSource = null;
                }
            };
        }

        /// <summary>The window whose <see cref="Window.Activated"/> is currently hooked, held so the
        /// handler can be removed on detach. See <see cref="InitializePermissions"/>.</summary>
        private Window _activationSource;

        private void OnPermissionsChanged(object sender, EventArgs e) => Dispatcher.UIThread.Post(RenderPermissions);

        private void OnWindowActivated(object sender, EventArgs e) => RenderPermissions();

        private void RenderPermissions()
        {
            RenderPermission(MacPermission.ScreenRecording, ScreenRecordingStatus, ScreenRecordingButton,
                             ScreenRecordingCaption,
                             "Lets Clowd capture screenshots, record video, and pick colors from the screen with the "
                             + "eyedropper. Without it, captures and the eyedropper are unavailable.");

            RenderPermission(MacPermission.Accessibility, AccessibilityStatus, AccessibilityButton, AccessibilityCaption,
                             "Lets Clowd listen for its global hotkeys while other apps are in the foreground. Without "
                             + "it, hotkeys do nothing and every action has to be started from the menu bar.");
        }

        private static void RenderPermission(MacPermission permission, TextBlock status, Button button, TextBlock caption,
                                             string what)
        {
            var granted = MacPermissions.IsGranted(permission);

            status.Text = granted ? "Granted" : "Not granted";
            status.Classes.Set("Granted", granted);
            status.Classes.Set("Missing", !granted);

            // the button stays even once granted: revoking happens in System Settings too, and a
            // control that vanishes on success leaves no way back in. It always goes straight to the
            // pane rather than trying the OS prompt first — macOS only offers each prompt once ever,
            // so a button that sometimes prompts and sometimes doesn't is unpredictable, and the
            // first-run prompt is already covered where it belongs, on the first capture attempt.
            button.Content = "Open System Settings";

            caption.Text = granted
                ? what
                : what + " Clowd has to be restarted after you grant it.";
        }

        private void OnScreenRecordingClick(object sender, RoutedEventArgs e) =>
            MacPermissions.OpenSettings(MacPermission.ScreenRecording);

        private void OnAccessibilityClick(object sender, RoutedEventArgs e) =>
            MacPermissions.OpenSettings(MacPermission.Accessibility);

        // ---- Shell group ----

        /// <summary>Same shape as the auto-start checkbox: the box writes the setting, App applies it
        /// to the registry, and this reports back only when that failed.</summary>
        private void InitializeContextMenu()
        {
            if (!ExplorerContextMenuManager.IsSupported)
            {
                // macOS: the Finder service is declared in Info.plist and enabled by the OS by
                // default (NSRequiredContext); its on/off switch is system-managed, so there is
                // nothing for a checkbox here to do — just say where the entry lives.
                ContextMenuCheck.IsVisible = false;
                ContextMenuCaption.Text = "'Upload with Clowd' appears when right-clicking files in Finder, under Services. "
                    + "It can be turned off in System Settings → Keyboard → Keyboard Shortcuts → Services.";
                ContextMenuSettingsButton.IsVisible = true;
                return;
            }

            AttachedToVisualTree += (s, e) =>
            {
                ExplorerContextMenuManager.StateChanged += OnContextMenuStateChanged;
                SparsePackageManager.StateChanged += OnContextMenuStateChanged;
                ShowContextMenuCaption();
            };

            DetachedFromVisualTree += (s, e) =>
            {
                ExplorerContextMenuManager.StateChanged -= OnContextMenuStateChanged;
                SparsePackageManager.StateChanged -= OnContextMenuStateChanged;
            };
        }

        /// <summary>Opens System Settings → Keyboard, the pane hosting the Keyboard Shortcuts →
        /// Services list where the Finder service can be toggled. The Shortcuts sheet itself is
        /// not deep-linkable, so the caption spells out the remaining clicks.</summary>
        private void OnContextMenuSettingsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("open", new[] { "x-apple.systempreferences:com.apple.Keyboard-Settings.extension?Shortcuts" });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to open System Settings: " + ex);
                SentryConfig.CaptureHandled(ex, "settings.open-keyboard-pane");
            }
        }

        private void OnContextMenuStateChanged(object sender, EventArgs e) =>
            Dispatcher.UIThread.Post(ShowContextMenuCaption);

        private void ShowContextMenuCaption()
        {
            // either half failing wins over any hint: a stale checkbox that silently does nothing
            // is worse than no checkbox.
            if ((ExplorerContextMenuManager.LastError ?? SparsePackageManager.LastError) is { } err)
            {
                ContextMenuCaption.Text = "Could not change the context menu setting: " + err;
                return;
            }

            // on Win11 the sparse MSIX package puts the entry in the compact menu; LastKnownIsEnabled
            // is the cached registration state (reading the real one shells out to PowerShell, which
            // has no place on the UI thread) and StateChanged re-renders this when it settles.
            if (SparsePackageManager.LastKnownIsEnabled)
            {
                ContextMenuCaption.Text = "Appears in the right-click menu. On older Windows versions it's under \"Show more options\".";
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

        /// <summary>The update group renders <see cref="UpdateService"/>'s state rather than owning
        /// any of its own: the background scheduler can check, download and stage an update while
        /// this page is closed, or while it is open and untouched.</summary>
        private void InitializeUpdateGroup()
        {
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

            VersionText.Text = "Current version: " + update.CurrentVersion;

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

            // shown even where it cannot be used (a loose build cannot update at all) — a missing
            // control reads as a missing feature.
            PrereleaseSection.IsEnabled = update.IsSupported;
            PrereleaseHelpText.Text = update.IsSupported
                ? "Bleeding edge releases may have newer preview features, but also may have more bugs than stable releases."
                : "Experimental builds can only be opted into in an installed build.";
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
