using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;

namespace Clowd
{
    public partial class App : Application
    {
        public static new App Current => Application.Current as App;

        private MutexArgsForwarder _processor;
        private TrayIcon _trayIcon;
        private GlobalHotkeyHost _hotkeyHost;
        private bool _exiting;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            SetupExceptionHandling();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // tray-resident lifetime: the tray Exit menu item is the only shutdown path (§6).
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += OnShutdownRequested;

                Startup(desktop.Args ?? Array.Empty<string>());
            }

            base.OnFrameworkInitializationCompleted();
        }

        private async void Startup(string[] args)
        {
            try
            {
                await SetupMutex(args);
                bool firstRun = await SetupSettings();

                SetupTrayIcon();

                // rebuild the tray menu when any hotkey gesture changes so the appended gesture
                // text stays current (decision table #48 / §6). IsRegistered/Error are live
                // status updates bubbled from the hotkey host and don't affect menu headers.
                SettingsRoot.Current.Hotkeys.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is nameof(GlobalTrigger.IsRegistered) or nameof(GlobalTrigger.Error))
                        return;
                    Dispatcher.UIThread.Post(SetupTrayIcon);
                };

                SetupGlobalHotkeys();

                // start receiving command line arguments forwarded from secondary instances
                _processor.Ready();

                if (firstRun)
                {
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.About);
                }
                else if (SettingsRoot.Current.Editor.RestoreSessionsOnClowdStart)
                {
                    EditorWindow.ShowAllPreviouslyActiveSessions();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Fatal error during startup: " + ex);
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.ToString(),
                    "Error starting Clowd. The program will now exit.");
                ExitApp();
            }
        }

        /// <summary>Returns true when this looks like the first run (no settings file existed).</summary>
        private async Task<bool> SetupSettings()
        {
            bool firstRun;
            try
            {
                firstRun = !Directory.Exists(PathConstants.SettingsData)
                           || !Directory.EnumerateFiles(PathConstants.SettingsData, "*Settings.xml").Any();
            }
            catch
            {
                firstRun = false;
            }

            try
            {
                SettingsRoot.LoadDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load settings: " + ex);
                if (await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Error,
                        "There was an error loading the application configuration.\r\nWould you like to reset the config to default or exit the application?",
                        "Error loading app config", "Reset Config", "Exit Application", NiceDialogIcon.Information, ex.ToString()))
                {
                    SettingsRoot.CreateNew();
                    SettingsRoot.Current.Save();
                }
                else
                {
                    Environment.Exit(1);
                }
            }

            return firstRun;
        }

        private async Task SetupMutex(string[] args)
        {
            _processor = new MutexArgsForwarder();
            _processor.ArgsReceived += (s, e) => Dispatcher.UIThread.Post(() => OnFilesReceived(e.Args));

            try
            {
                if (await _processor.Startup(args) == false)
                {
                    // clowd is already running and we've forwarded our args successfully.
                    Environment.Exit(0);
                }
            }
            catch (TimeoutException)
            {
                // there is an unresponsive clowd process; try to kill it and re-start.
                KillOtherClowdProcess();
                if (await _processor.Startup(args) == false)
                    throw new Exception("Unable to create new startup mutex, a mutex already exists. Another Clowd instance? Uninstaller?");
            }
        }

        private void SetupTrayIcon()
        {
            if (_trayIcon == null)
            {
                _trayIcon = new TrayIcon
                {
                    Icon = AppStyles.AppIcon,
                    ToolTipText = "Clowd",
                    // the macOS native-menu exporter binds to the first Menu instance and
                    // throws "The menu being updated does not match" if it is replaced, so
                    // this single NativeMenu is only ever mutated in place (below).
                    Menu = new NativeMenu(),
                };

                // decision table #48: no double-click on Avalonia TrayIcon — a single click
                // opens the settings window.
                _trayIcon.Clicked += (s, e) => PageManager.Current.GetSettingsPage().Open();

                TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            }

            static string WithGesture(string header, GlobalTrigger trigger)
            {
                // gesture text is appended to the header (NativeMenuItem has no InputGestureText).
                var g = trigger?.KeyGestureText;
                return String.IsNullOrEmpty(g) ? header : $"{header} ({g})";
            }

            var menu = _trayIcon.Menu;
            menu.Items.Clear();

            var capture = new NativeMenuItem(WithGesture("Capture Screen", SettingsRoot.Current.Hotkeys.CaptureRegionShortcut));
            capture.Click += async (s, e) =>
            {
                // wait long enough for the menu to disappear (matches WPF).
                await Task.Delay(400);
                StartCapture();
            };
            menu.Add(capture);

            var colorp = new NativeMenuItem("Color Picker");
            colorp.Click += (s, e) => NiceDialog.ShowColorViewer();
            menu.Add(colorp);

            var editor = new NativeMenuItem("Image Editor");
            editor.Click += (s, e) => EditorWindow.ShowSession(null);
            menu.Add(editor);

            menu.Add(new NativeMenuItemSeparator());

            var uploads = new NativeMenuItem("Recents & Uploads");
            uploads.Click += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
            menu.Add(uploads);

            var settings = new NativeMenuItem("Settings");
            settings.Click += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsGeneral);
            menu.Add(settings);

            menu.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("Exit");
            exit.Click += async (s, e) => await ExitAppWithConfirmation();
            menu.Add(exit);
        }

        public void StartCapture(PlatformUtil.ScreenRect region = null)
        {
            PageManager.Current.GetScreenCapturePage().Open(region);
        }

        /// <summary>
        /// Wires the <see cref="GlobalTrigger"/> actions (mirroring the WPF App.SetupSettings wiring,
        /// adapted to what exists in this build) and installs the SharpHook-backed
        /// <see cref="GlobalHotkeyHost"/> as <see cref="GlobalTrigger.Host"/>. Installing the host
        /// registers every trigger that has both a gesture and a listener; the OS keyboard hook itself
        /// starts lazily on the first such registration, so no hook runs if no gestures are set.
        /// </summary>
        private void SetupGlobalHotkeys()
        {
            var keys = SettingsRoot.Current.Hotkeys;

            // capture/upload/recording are provided by the separate Rust process (or not yet ported)
            // — those triggers route to the existing stub page or a NiceDialog notice.
            keys.FileUploadShortcut.TriggerExecuted += (s, e) => NiceDialog.ShowNoticeAsync(
                null, NiceDialogIcon.Information, "Upload providers are not available in this build.", "Upload unavailable");
            keys.ClipboardUploadShortcut.TriggerExecuted += (s, e) => NiceDialog.ShowNoticeAsync(
                null, NiceDialogIcon.Information, "Upload providers are not available in this build.", "Upload unavailable");
            keys.CaptureRegionShortcut.TriggerExecuted += (s, e) => StartCapture();
            keys.CaptureFullscreenShortcut.TriggerExecuted += (s, e) => StartCapture();
            keys.CaptureActiveShortcut.TriggerExecuted += (s, e) => StartCapture();
            keys.DrawOnScreenShortcut.TriggerExecuted += (s, e) => PageManager.Current.GetLiveDrawPage().Open();
            keys.StartStopRecordingShortcut.TriggerExecuted += (s, e) => NiceDialog.ShowNoticeAsync(
                null, NiceDialogIcon.Information, "Screen recording is not available in this build.", "Recording unavailable");

            _hotkeyHost = new GlobalHotkeyHost();
            GlobalTrigger.Host = _hotkeyHost;
        }

        private void ShutdownGlobalHotkeys()
        {
            try
            {
                GlobalTrigger.Host = null;
                _hotkeyHost?.Dispose();
                _hotkeyHost = null;
            }
            catch { }
        }

        public async Task ExitAppWithConfirmation()
        {
            if (SettingsRoot.Current?.General?.ConfirmClose == true)
            {
                // the WPF TaskDialog "don't ask me again" verification checkbox is not ported;
                // the prompt can be disabled in General settings instead.
                if (await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Warning,
                        "If you close Clowd, it will stop any in-progress uploads and you will be unable to upload anything new.",
                        "Are you sure you wish to close Clowd?", "Close Clowd", "Cancel"))
                {
                    ExitApp();
                }
            }
            else
            {
                ExitApp();
            }
        }

        public void ExitApp()
        {
            if (_exiting)
                return;
            _exiting = true;

            // close all open windows first so per-window persistence runs before the process dies
            // (EditorWindow.Closing renders the session preview and clears OpenEditor, §5.7).
            CloseAllWindows();

            ShutdownGlobalHotkeys();

            try { SettingsRoot.Current?.Save(); }
            catch { }

            try { _trayIcon?.Dispose(); }
            catch { }

            try { _processor?.Dispose(); }
            catch { }

            Environment.Exit(0);
        }

        private void OnShutdownRequested(object sender, ShutdownRequestedEventArgs e)
        {
            // OS session ending / explicit lifetime shutdown — close editor windows so their
            // Closing persistence runs (§5.7), persist settings, release the single-instance
            // pipe, and let the shutdown proceed.
            CloseAllWindows();

            ShutdownGlobalHotkeys();

            try { SettingsRoot.Current?.Save(); }
            catch { }

            try { _processor?.Dispose(); }
            catch { }
        }

        private void CloseAllWindows()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            foreach (var window in desktop.Windows.ToArray())
            {
                try { window.Close(); }
                catch { }
            }
        }

        private void OnFilesReceived(string[] filePaths)
        {
            // the WPF app uploaded forwarded files; upload providers do not ship in this build.
            Debug.WriteLine("Files received from secondary instance: " + String.Join(", ", filePaths));
            NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Information,
                "Upload providers are not available in this build.", "Upload unavailable");
        }

        // decision table #68: replaces WPF DispatcherUnhandledException.
        private void SetupExceptionHandling()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                Debug.WriteLine("AppDomainUnhandledException: " + e.ExceptionObject);
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Debug.WriteLine("UnobservedTaskException: " + e.Exception);
                e.SetObserved();
            };

            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                e.Handled = true;
                Debug.WriteLine("DispatcherUnhandledException: " + e.Exception);
                NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Exception.ToString(), "An error has occurred.");
            };
        }

        private static void KillOtherClowdProcess()
        {
            var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName).Where(p => p.Id != current.Id).ToArray();

            foreach (var p in processes)
            {
                try { p.Kill(); }
                catch { }
            }

            foreach (var p in processes)
            {
                try { p.WaitForExit(3000); }
                catch { }
            }
        }
    }
}
