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
        private CaptureStandbySupervisor _captureStandby;
        private readonly object _captureStandbyLock = new();
        private bool _captureWarmFaulted;
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

            // the SDK's AI generators resolve the inference binary through this delegate on every
            // run — installed before Startup so the --video-edit/--video-spike harnesses (which
            // return before the tray lifetime is set up) get it too.
            Clowd.VideoSDK.Ai.AiLoader.Configure(AiBinaryLocator.Resolve);

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
                // hidden spike harness (`--video-spike file.mp4`): a bare playback window used to
                // measure the video engine. Must run BEFORE single-instance forwarding — a spike
                // process must never forward its args to (or be swallowed by) a resident Clowd.
                if (UI.VideoEditor.VideoSpikeWindow.TryHandleArgs(args))
                    return;

                // hidden editor harness (`--video-edit file.mp4`): opens the video editor on an
                // arbitrary file with no session (persistence disabled, render output next to the
                // file). Used by e2e testing; same single-instance rules as the spike above.
                if (UI.VideoEditor.VideoEditorWindow.TryHandleArgs(args))
                    return;

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

                // warm the camera list so the first picker to open has one without waiting. Cheap
                // now that enumeration is native (~60 ms), but it also covers the fallback path,
                // where it is the difference between a warm cache and a 5 s stall in front of the
                // user (see CameraDeviceManager).
                _ = CameraDeviceManager.GetCamerasAsync();

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

                // macOS: Finder right-click → "Upload with Clowd" (NSServices) delivers the
                // selection here in-process — background handoff, same as forwarded CLI args.
                MacServicesProvider.Initialize(files => Dispatcher.UIThread.Post(() => OnFilesReceived(files)));

                // macOS: relaunching a running app (Dock click, Launchpad, Spotlight, `open -a`)
                // does not start a second process — LaunchServices sends the existing instance a
                // reopen event instead, surfaced by Avalonia as ActivationKind.Reopen. This is
                // the mac counterpart of MutexArgsForwarder.ShowMainWindowRequested.
                if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                {
                    activatable.Activated += (s, e) =>
                    {
                        if (e.Kind == ActivationKind.Reopen)
                            Dispatcher.UIThread.Post(ShowMainWindowForAppActivation);
                    };
                }

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
                    // on General so the auto-start / minimized options are the first thing seen.
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
            _processor.ShowMainWindowRequested += (s, e) => Dispatcher.UIThread.Post(ShowMainWindowForAppActivation);

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

            var videoEditor = new NativeMenuItem("Video Editor");
            videoEditor.Click += (s, e) => Clowd.UI.VideoEditor.VideoEditorWindow.ShowBlankProject();
            menu.Add(videoEditor);

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

            _hotkeys.SetAction(HotkeyId.FileUpload, () => UploadFilePrompt());
            _hotkeys.SetAction(HotkeyId.ClipboardUpload, () => UploadClipboard());
            _hotkeys.SetAction(HotkeyId.StartStopRecording, ToggleRecording);

            HotkeyManager.Current = _hotkeys;
            ConfigureCaptureHotkeys();

            SettingsRoot.Current.Capture.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsCapture.KeepCapturerWarm))
                    Dispatcher.UIThread.Post(ConfigureCaptureHotkeys);
            };
        }

        /// <summary>
        /// The screenshot hotkeys have exactly one owner at a time. Warm: the standby capturer
        /// hooks them itself (handy-keys low-level hook — see standby_hotkeys.rs for why they
        /// must live in that process), and SharpHook must stay out so two hooks never race to
        /// suppress and handle the same press. Not warm — setting off, or overridden after
        /// standby faulted — SharpHook drives the classic one-shot path. Called at startup and
        /// again on every ownership change (setting toggled, supervisor fallback), always on
        /// the UI thread.
        /// </summary>
        private void ConfigureCaptureHotkeys()
        {
            if (_hotkeys == null)
                return; // shutdown already ran; nothing left to (re)wire

            bool warm = SettingsRoot.Current.Capture.KeepCapturerWarm && !_captureWarmFaulted && !_exiting;
            lock (_captureStandbyLock)
            {
                if (warm && _captureStandby == null)
                    _captureStandby = new CaptureStandbySupervisor(OnCaptureStandbyFallback);
                else if (!warm && _captureStandby != null)
                {
                    _captureStandby.Dispose();
                    _captureStandby = null;
                }
            }
            _hotkeys.SetAction(HotkeyId.CaptureRegion, warm ? null : () => StartCapture(CaptureMode.Region));
            _hotkeys.SetAction(HotkeyId.CaptureFullscreen, warm ? null : () => StartCapture(CaptureMode.Screen));
            _hotkeys.SetAction(HotkeyId.CaptureActive, warm ? null : () => StartCapture(CaptureMode.Window));
        }

        /// <summary>The supervisor's give-up signal (missing permission/binary, repeated
        /// crashes). Overrides warm capture off for the rest of this process — the saved
        /// setting is untouched, so a machine with a transient problem heals on restart.</summary>
        private void OnCaptureStandbyFallback() => Dispatcher.UIThread.Post(() =>
        {
            _captureWarmFaulted = true;
            ConfigureCaptureHotkeys();
        });

        private void ShutdownGlobalHotkeys()
        {
            try
            {
                lock (_captureStandbyLock)
                {
                    _captureStandby?.Dispose();
                    _captureStandby = null;
                }
            }
            catch { }
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
                var prompt = await NiceDialog.ShowVerifiableDialogAsync(null, NiceDialogIcon.Warning,
                    "If you close Clowd, it will stop any in-progress uploads and you will be unable to upload anything new.",
                    "Are you sure you wish to close Clowd?", "Close Clowd", "Cancel",
                    "Don't ask again");

                if (prompt.Result)
                {
                    // only honoured when the exit goes ahead: ticking the box and then backing out
                    // should not silently disable the prompt. ExitApp writes the settings file.
                    if (prompt.Verified)
                        SettingsRoot.Current.General.ConfirmClose = false;

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

            // and end an in-flight scrolling capture, for the opposite reason: it has no partial
            // artifact worth keeping, but its driver would otherwise carry on scrolling (and
            // photographing) whatever window it was pointed at after Clowd is gone.
            try
            {
                if (ScrollCapturePage.ActiveInstance is { } scrolling)
                    await scrolling.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error stopping the scrolling capture during exit: " + ex);
                SentryConfig.CaptureHandled(ex, "exit.stop-scroll-capture");
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

        /// <summary>The user "launched" Clowd while it was already running (Windows second
        /// instance with no args, macOS reopen). Bring back whatever is already on screen: the
        /// main window if it is up (without yanking the user off their tab), otherwise the
        /// editors they have open. Only when nothing is showing does this open the main window
        /// on Recents.</summary>
        private void ShowMainWindowForAppActivation()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.Windows.OfType<MainWindow>().Any(w => w.IsVisible))
                {
                    PageManager.Current.GetSettingsPage().Open();
                    return;
                }

                // no main window, but the user still has editors open — "launch Clowd" then means
                // "show me the Clowd I already have", which is what the Dock does for every other
                // app. Opening a main window nobody asked for on top of them is the wrong answer.
                var open = desktop.Windows.Where(w => w.IsVisible && IsUserEditingWindow(w)).ToArray();
                if (open.Length > 0)
                {
                    // all of them come forward (that is what a mac reopen means), but the one the
                    // user was last in is raised last so it keeps the focus.
                    foreach (var window in open.OrderBy(w => w.IsActive))
                    {
                        if (window.WindowState == WindowState.Minimized)
                            window.WindowState = WindowState.Normal;

                        window.Activate();
                    }

                    return;
                }
            }

            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
        }

        /// <summary>Windows the user works in, as opposed to the transient HUD pieces that ride
        /// along with a capture (recording border/toolbar, scroll status) and the dialogs owned by
        /// a parent window. Only these keep an activation from opening the main window.</summary>
        private static bool IsUserEditingWindow(Window window)
            => window is EditorWindow or Clowd.UI.VideoEditor.VideoEditorWindow;

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
