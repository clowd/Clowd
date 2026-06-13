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
        private HotkeyManager _hotkeys;
        private bool _exiting;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>Best-effort clipboard access for code with no window of its own (e.g.
        /// UploadManager): borrows the clipboard of any open window.</summary>
        public static Avalonia.Input.Platform.IClipboard GetPrimaryClipboard()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow
                             ?? desktop.Windows.FirstOrDefault(w => w.IsActive)
                             ?? desktop.Windows.FirstOrDefault();
                return window?.Clipboard;
            }

            return null;
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
                // text stays current (decision table #48 / §6). SettingsHotkey is pure data now —
                // every PropertyChanged is a gesture change.
                SettingsRoot.Current.Hotkeys.PropertyChanged += (s, e) => Dispatcher.UIThread.Post(SetupTrayIcon);

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
                firstRun = !File.Exists(SettingsService.FilePath);
            }
            catch
            {
                firstRun = false;
            }

            try
            {
                // pure parse — no side effects; Current is assigned explicitly here.
                SettingsRoot.Current = SettingsService.Load();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load settings: " + ex);
                if (await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Error,
                        "There was an error loading the application configuration.\r\nWould you like to reset the config to default or exit the application?",
                        "Error loading app config", "Reset Config", "Exit Application", NiceDialogIcon.Information, ex.ToString()))
                {
                    SettingsRoot.Current = new SettingsRoot();
                    SettingsService.Save(SettingsRoot.Current);
                }
                else
                {
                    Environment.Exit(1);
                }
            }

            // formerly a hidden side effect of settings deserialization; now an explicit startup step.
            // DiscoverProviders scans loaded assemblies, so Clowd.Upload must be pulled in first.
            _ = typeof(Upload.MimeProvider).Assembly;
            SettingsRoot.Current.Uploads.DiscoverProviders();

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

            static string WithGesture(string header, SimpleKeyGesture gesture)
            {
                // gesture text is appended to the header (NativeMenuItem has no InputGestureText).
                var g = gesture?.ToString();
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

            var progress = new NativeMenuItem("Upload Progress");
            progress.Click += (s, e) => (PageManager.Current.Tasks as TasksViewManager)?.ShowOverlay();
            menu.Add(progress);

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
        /// Creates the <see cref="HotkeyManager"/> over the SharpHook-backed
        /// <see cref="GlobalHotkeyHost"/> and wires the hotkey actions explicitly (mirroring the WPF
        /// App.SetupSettings wiring, adapted to what exists in this build). Gestures are read from
        /// settings; the OS keyboard hook itself starts lazily on the first registration, so no hook
        /// runs if no gestures are set.
        /// </summary>
        private void SetupGlobalHotkeys()
        {
            _hotkeys = new HotkeyManager(new GlobalHotkeyHost(), SettingsRoot.Current.Hotkeys);

            // capture/recording are provided by the separate Rust process (or not yet ported)
            // — those hotkeys route to the existing stub page or a NiceDialog notice.
            _hotkeys.SetAction(HotkeyId.FileUpload, () => UploadFilePrompt());
            _hotkeys.SetAction(HotkeyId.ClipboardUpload, () => UploadClipboard());
            _hotkeys.SetAction(HotkeyId.CaptureRegion, () => StartCapture());
            _hotkeys.SetAction(HotkeyId.CaptureFullscreen, () => StartCapture());
            _hotkeys.SetAction(HotkeyId.CaptureActive, () => StartCapture());
            _hotkeys.SetAction(HotkeyId.DrawOnScreen, () => PageManager.Current.GetLiveDrawPage().Open());
            _hotkeys.SetAction(HotkeyId.StartStopRecording, () => NiceDialog.ShowNoticeAsync(
                null, NiceDialogIcon.Information, "Screen recording is not available in this build.", "Recording unavailable"));

            HotkeyManager.Current = _hotkeys;
        }

        private void ShutdownGlobalHotkeys()
        {
            try
            {
                _hotkeys?.Dispose(); // also disposes the underlying GlobalHotkeyHost
                _hotkeys = null;
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

            try
            {
                if (SettingsRoot.Current != null)
                    SettingsService.Save(SettingsRoot.Current);
            }
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

            try
            {
                if (SettingsRoot.Current != null)
                    SettingsService.Save(SettingsRoot.Current);
            }
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

        private async void OnFilesReceived(string[] filePaths)
        {
            Debug.WriteLine("Files received from secondary instance: " + String.Join(", ", filePaths));
            await UploadManager.UploadSeveralFiles(filePaths);
        }

        private async void UploadFilePrompt()
        {
            var files = await NiceDialog.ShowSelectFilesDialog(null, "Select files to upload",
                SettingsRoot.Current.General.LastSavePath, true);

            if (files != null)
                await UploadManager.UploadSeveralFiles(files);
        }

        /// <summary>Uploads the current clipboard contents (image, else text), mirroring the WPF
        /// Paste() hotkey handler.</summary>
        private async void UploadClipboard()
        {
            var clipboard = GetPrimaryClipboard();

            try
            {
                var (bitmap, _) = await ClipboardImpl.GetClipboardCanvasData(clipboard);
                if (bitmap != null)
                {
                    await UploadManager.UploadImage(bitmap, "Clipboard Image");
                    return;
                }

                var text = clipboard != null ? await clipboard.GetTextAsync() : null;
                if (!String.IsNullOrEmpty(text))
                {
                    await UploadManager.UploadText(text, "Clipboard Text");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Clipboard upload failed: " + ex);
            }

            await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Information,
                "The clipboard does not contain content that can be uploaded.", "Nothing to upload");
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
