using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Localization;
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
        public static IClipboard GetPrimaryClipboard()
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

                MacDockIcon.Initialize(desktop);

                Startup(desktop.Args ?? Array.Empty<string>());
            }

            base.OnFrameworkInitializationCompleted();
        }

        private async void Startup(string[] args)
        {
            try
            {
                await SetupMutex(args);
                bool firstRun = await SetupSettings() || Program.IsVelopackFirstRun;

                ApplyTheme();

                // must run before anything resolves a string. An empty Language setting keeps the
                // OS UI language, which Loc captured on first touch just now.
                Loc.ApplyCulture(SettingsRoot.Current.General.Language);

                // the saved settings are the source of truth for the shell registrations; reconcile
                // them with the OS once at startup, then follow whenever the user toggles a checkbox.
                AutoStartManager.Sync(SettingsRoot.Current.General.RegisterAutoStart);
                ExplorerContextMenuManager.Sync(SettingsRoot.Current.General.RegisterExplorerContextMenu);

                // the sparse package sync shells out to PowerShell (seconds, not milliseconds), so
                // unlike the registry-backed managers above it stays off the UI thread.
                _ = Task.Run(() => SparsePackageManager.Sync(SettingsRoot.Current.General.RegisterExplorerContextMenu));

                SettingsRoot.Current.General.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SettingsGeneral.Theme))
                        Dispatcher.UIThread.Post(ApplyTheme);
                    else if (e.PropertyName == nameof(SettingsGeneral.Language))
                        Dispatcher.UIThread.Post(() => Loc.ApplyCulture(SettingsRoot.Current.General.Language));
                    else if (e.PropertyName == nameof(SettingsGeneral.RegisterAutoStart))
                        AutoStartManager.TrySetEnabled(SettingsRoot.Current.General.RegisterAutoStart);
                    else if (e.PropertyName == nameof(SettingsGeneral.RegisterExplorerContextMenu))
                    {
                        var enabled = SettingsRoot.Current.General.RegisterExplorerContextMenu;
                        ExplorerContextMenuManager.TrySetEnabled(enabled);
                        _ = Task.Run(() => SparsePackageManager.TrySetEnabled(enabled));
                    }
                };

                SetupTrayIcon();

                // rebuild the tray menu when any hotkey gesture changes so the shortcut text shown
                // beside each item stays current (decision table #48 / §6). SettingsHotkey is pure
                // data now — every PropertyChanged is a gesture change.
                SettingsRoot.Current.Hotkeys.PropertyChanged += (s, e) => Dispatcher.UIThread.Post(SetupTrayIcon);

                // same mechanism for the language: the tray menu is native and cannot be bound, so
                // it is rebuilt in place whenever the UI culture changes.
                Loc.CultureChanged += (s, e) => Dispatcher.UIThread.Post(SetupTrayIcon);

                SetupGlobalHotkeys();

                // periodic update checks, and (opt-in) downloading + applying them while idle.
                UpdateService.Default.Start();

                // start receiving command line arguments forwarded from secondary instances
                _processor.Ready();

                if (Program.IsSilentUpdateRestart)
                {
                    // relaunched by the updater after a background update: come back up exactly as
                    // the user left it — in the tray, with whatever editors were open restored.
                    if (SettingsRoot.Current.Editor.RestoreSessionsOnClowdStart)
                        EditorWindow.ShowAllPreviouslyActiveSessions();
                }
                else if (firstRun)
                {
                    // first launch after an install: show the window regardless of StartMinimized,
                    // on General so the auto-start / minimised options are the first thing seen.
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsGeneral);
                }
                else
                {
                    if (!SettingsRoot.Current.General.StartMinimized)
                        PageManager.Current.GetSettingsPage().Open();

                    if (SettingsRoot.Current.Editor.RestoreSessionsOnClowdStart)
                        EditorWindow.ShowAllPreviouslyActiveSessions();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Fatal error during startup: " + ex);
                SentryConfig.CaptureHandled(ex, "startup");
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
                SentryConfig.CaptureHandled(ex, "settings.load");
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
                    Icon = AppStyles.TrayIcon,
                    ToolTipText = "Clowd",
                    // the macOS native-menu exporter binds to the first Menu instance and
                    // throws "The menu being updated does not match" if it is replaced, so
                    // this single NativeMenu is only ever mutated in place (below).
                    Menu = new NativeMenu(),
                };

                // decision table #48: no double-click on Avalonia TrayIcon — the single-click
                // action is user-configurable (General settings).
                _trayIcon.Clicked += (s, e) =>
                {
                    if (SettingsRoot.Current?.General?.TrayClick == TrayClickAction.CaptureRegion)
                        StartCapture();
                    else
                        PageManager.Current.GetSettingsPage().Open();
                };

                TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            }

            // Shortcuts are surfaced as separate, right-aligned menu text — Avalonia binds
            // NativeMenuItem.Gesture → MenuItem.InputGesture on the Windows managed tray menu (the
            // Semi theme renders it in muted, right-aligned gesture text) and to the native key
            // equivalent on macOS. Only assign when a gesture is actually set.
            static void ApplyGesture(NativeMenuItem item, SimpleKeyGesture gesture)
            {
                if (gesture != null && !String.IsNullOrEmpty(gesture.ToString()))
                    item.Gesture = gesture.ToKeyGesture();
            }

            var menu = _trayIcon.Menu;
            menu.Items.Clear();

            var capture = new NativeMenuItem("Capture Screen");
            ApplyGesture(capture, SettingsRoot.Current.Hotkeys.CaptureRegionShortcut);
            capture.Click += async (s, e) =>
            {
                // wait long enough for the menu to disappear (matches WPF).
                await Task.Delay(400);
                StartCapture();
            };
            menu.Add(capture);

            var record = new NativeMenuItem("Start / Stop Recording");
            ApplyGesture(record, SettingsRoot.Current.Hotkeys.StartStopRecordingShortcut);
            record.Click += async (s, e) =>
            {
                // wait long enough for the menu to disappear (matches WPF).
                await Task.Delay(400);
                ToggleRecording();
            };
            menu.Add(record);

            var colorp = new NativeMenuItem("Color Picker");
            colorp.Click += (s, e) => NiceDialog.ShowColorViewer();
            menu.Add(colorp);

            var editor = new NativeMenuItem("Image Editor");
            editor.Click += (s, e) => EditorWindow.ShowSession(null);
            menu.Add(editor);

            menu.Add(new NativeMenuItemSeparator());

            // every supported action that has a global hotkey is also reachable from this menu — the
            // menu is the fallback when a hotkey fails to register (see the Hotkeys settings page).
            var uploadFile = new NativeMenuItem("Upload File…");
            ApplyGesture(uploadFile, SettingsRoot.Current.Hotkeys.FileUploadShortcut);
            uploadFile.Click += (s, e) => UploadFilePrompt();
            menu.Add(uploadFile);

            var uploadClip = new NativeMenuItem("Upload Clipboard");
            ApplyGesture(uploadClip, SettingsRoot.Current.Hotkeys.ClipboardUploadShortcut);
            uploadClip.Click += (s, e) => UploadClipboard();
            menu.Add(uploadClip);

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

        public void StartCapture(CaptureMode mode = CaptureMode.Region, bool video = false)
        {
            PageManager.Current.GetScreenCapturePage().Open(mode, video);
        }

        /// <summary>The Start/Stop Recording hotkey and tray action (DESIGN §4.3): toggles the
        /// active recording session, or launches the capture overlay in video mode to pick a
        /// recording region. Toggle() during the WAIT state is a no-op (§4.2).</summary>
        public void ToggleRecording()
        {
            if (VideoCapturePage.ActiveInstance is { } page)
                page.Toggle();
            else
                StartCapture(CaptureMode.Region, video: true);
        }

        private void ApplyTheme()
        {
            RequestedThemeVariant = SettingsRoot.Current?.General?.Theme switch
            {
                AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
                AppTheme.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
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

            // capture and the recording region picker are provided by the separate Rust process.
            _hotkeys.SetAction(HotkeyId.FileUpload, () => UploadFilePrompt());
            _hotkeys.SetAction(HotkeyId.ClipboardUpload, () => UploadClipboard());
            _hotkeys.SetAction(HotkeyId.CaptureRegion, () => StartCapture(CaptureMode.Region));
            _hotkeys.SetAction(HotkeyId.CaptureFullscreen, () => StartCapture(CaptureMode.Screen));
            _hotkeys.SetAction(HotkeyId.CaptureActive, () => StartCapture(CaptureMode.Window));
            _hotkeys.SetAction(HotkeyId.StartStopRecording, ToggleRecording);

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

        public async void ExitApp()
        {
            if (_exiting)
                return;
            _exiting = true;

            // finish/cancel an active recording FIRST (bounded by the capturer's stop timeout):
            // exiting mid-recording otherwise flushes a valid video.mp4 via stdin EOF but never
            // writes session.json — the recording would be invisible in Recents and its
            // directory would leak forever.
            try
            {
                if (VideoCapturePage.ActiveInstance is { } recording)
                    await recording.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error finishing recording during exit: " + ex);
                SentryConfig.CaptureHandled(ex, "exit.finish-recording");
            }

            // close all open windows first so per-window persistence runs before the process dies
            // (EditorWindow.Closing renders the session preview and clears OpenEditor, §5.7).
            CloseAllWindows();

            ShutdownGlobalHotkeys();

            try { UpdateService.Default.Stop(); }
            catch { }

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

            // best-effort only (cannot await here without holding up the OS): the mp4 itself is
            // flushed by obs-express on stdin EOF regardless (§1.2); this races to also register
            // the session so the recording shows up in Recents on the next launch.
            try
            {
                _ = VideoCapturePage.ActiveInstance?.ShutdownAsync();
            }
            catch { }

            CloseAllWindows();

            ShutdownGlobalHotkeys();

            try { UpdateService.Default.Stop(); }
            catch { }

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
                    await UploadManager.UploadImage(bitmap, "Clipboard Upload");
                    return;
                }

                var text = clipboard != null ? await clipboard.TryGetTextAsync() : null;
                if (!String.IsNullOrEmpty(text))
                {
                    await UploadManager.UploadText(text, "Clipboard Upload");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Clipboard upload failed: " + ex);
                SentryConfig.CaptureHandled(ex, "upload.clipboard");
            }

            await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Information,
                "The clipboard does not contain content that can be uploaded.", "Nothing to upload");
        }

        // decision table #68: replaces WPF DispatcherUnhandledException.
        //
        // Sentry's own integrations already cover AppDomain.UnhandledException and
        // TaskScheduler.UnobservedTaskException, so those two are left alone. The Avalonia
        // dispatcher is Sentry's blind spot — nothing else observes it, and marking the event
        // Handled keeps it from ever reaching the AppDomain — so it is reported explicitly.
        // Every SentryConfig call is a no-op in debug builds.
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
                SentryConfig.CaptureUnhandled(e.Exception, "Dispatcher.UnhandledException");
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
