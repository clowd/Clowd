using System;
using System.Data;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Toolkit.Uwp.Notifications;
using NLog;
using NLog.Config;
using NLog.Targets;
using Squirrel;
using Application = System.Windows.Application;
using Binding = System.Windows.Data.Binding;
using MessageBox = System.Windows.MessageBox;

namespace Clowd
{
    public partial class App : Application
    {
        public static new App Current => IsDesignMode ? null : (App)Application.Current;
        public static bool IsDesignMode => System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());

        public static GoogleAnalytics Analytics { get; private set; }
        
        private TaskbarIcon _taskbarIcon;
        private MutexArgsForwarder _processor;
        private ILogger _log;

        protected override async void OnStartup(StartupEventArgs e)
        {
            var appArgs = SquirrelUtil.Startup(e.Args);
            _log = SetupExceptionHandling(SquirrelUtil.IsInstalled);

            try
            {
                base.OnStartup(e);

                // initialize GDI+ (our native lib depends on it, but does not initialize it)
                new System.Drawing.Region().Dispose();

                await SetupMutex(appArgs);
                await SetupSettings();

                // theme
                WPFUI.Appearance.Theme.Apply(WPFUI.Appearance.Theme.GetSystemTheme() == WPFUI.Appearance.SystemThemeType.Light
                    ? WPFUI.Appearance.ThemeType.Light
                    : WPFUI.Appearance.ThemeType.Dark);
                SetupTrayIconAndTheme();
                WPFUI.Appearance.Theme.Changed += (_, _) => SetupTrayIconAndTheme();

                // start receiving command line arguments
                _processor.Ready();
                if (SquirrelUtil.IsFirstRun)
                {
                    PageManager.Current.GetSettingsPage().Open(SettingsPageTab.About);
                }
                else
                {
                    EditorWindow.ShowAllPreviouslyActiveSessions();
                    if (SquirrelUtil.JustRestarted)
                        PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsGeneral);
                }
            }
            catch (Exception ex)
            {
                _log.Fatal(ex);
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.ToString(), "Error starting Clowd. The program will now exit.");
                ExitApp();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ExitApp();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            ExitApp();
        }

        private async Task SetupSettings()
        {
            foreach (var assy in Assembly.GetExecutingAssembly().GetReferencedAssemblies())
            {
                Assembly.Load(assy);
            }

            try
            {
                SettingsRoot.LoadDefault();
            }
            catch (Exception ex)
            {
                _log.Fatal(ex);
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

            var keys = SettingsRoot.Current.Hotkeys;
            keys.FileUploadShortcut.TriggerExecuted += (s, e) => UploadFile();
            keys.ClipboardUploadShortcut.TriggerExecuted += (s, e) => Paste();
            keys.CaptureActiveShortcut.TriggerExecuted += (s, e) => QuickCaptureCurrentWindow();
            keys.CaptureFullscreenShortcut.TriggerExecuted += (s, e) => QuickCaptureFullScreen();
            keys.CaptureRegionShortcut.TriggerExecuted += (s, e) => StartCapture();
            keys.DrawOnScreenShortcut.TriggerExecuted += (s, e) => PageManager.Current.GetLiveDrawPage().Open();
            keys.StartStopRecordingShortcut.TriggerExecuted += (s, e) => ToggleScreenRecording();

            string uaId = null;

#if !DEBUG
            uaId = "UA-116288212-2";
#endif

            Analytics = new GoogleAnalytics(SettingsRoot.Current.General.ClientId, uaId);
            Analytics.StartSession();

            //void setTheme()
            //{
            //    WPFUI.Appearance.Theme.Set(SettingsRoot.Current.General.Theme switch
            //    {
            //        AppTheme.Light => WPFUI.Appearance.ThemeType.Light,
            //        AppTheme.Dark => WPFUI.Appearance.ThemeType.Dark,
            //        _ => WPFUI.Appearance.ThemeType.Unknown,
            //    });
            //}
            //SettingsRoot.Current.General.PropertyChanged += (s, e) =>
            //{
            //    if (e.PropertyName == nameof(SettingsGeneral.Theme))
            //    {
            //        setTheme();
            //    }
            //};
            //setTheme();
        }

        private Logger SetupExceptionHandling(bool isInstalled)
        {
            var config = new LoggingConfiguration();

#if !DEBUG
            config.AddSentry(o =>
            {
                o.Layout = "${message}";
                o.BreadcrumbLayout = "${logger}: ${message}";
                o.Dsn = "https://0a572df482544fc19cdc855d17602fa4:012770b74f37410199e1424faf7c51d3@sentry.io/260666";
                o.AttachStacktrace = true;
                o.SendDefaultPii = true;
                o.IncludeEventDataOnBreadcrumbs = true;
                o.ShutdownTimeoutSeconds = 5;
                o.AddTag("logger", "${logger}");
            });
#endif

            config.AddTarget(new DebuggerTarget("debugger"));
            config.AddTarget(new ColoredConsoleTarget("console"));
            config.AddRuleForAllLevels("console");
            config.AddRuleForAllLevels("debugger");

            var logDir = isInstalled ? Path.Combine(SquirrelRuntimeInfo.BaseDirectory, "..") : SquirrelRuntimeInfo.BaseDirectory;
            var logFile = Path.Combine(logDir, "Clowd.log");
            var logArchiveFile = Path.Combine(logDir, "Clowd.archive{###}.log");

            config.AddTarget(new FileTarget("file")
            {
                FileName = logFile,
                Layout = new NLog.Layouts.SimpleLayout("${longdate} [${level:uppercase=true:truncate=4}] - ${message} ${onexception:\r\n---\r\n${exception:format=ToString,Data}\r\n---}"),
                ConcurrentWrites = true, // should allow multiple processes to use the same file
                KeepFileOpen = true,
                ArchiveFileName = logArchiveFile,
                ArchiveNumbering = ArchiveNumberingMode.Sequence,
                ArchiveAboveSize = 1_000_000,
                MaxArchiveFiles = 2,
            });

            config.AddRule(LogLevel.Debug, LogLevel.Fatal, "file");
            SquirrelLogger.Register();
            LogManager.Configuration = config;

            var log = LogManager.GetLogger("GlobalHandler");

            System.Windows.Forms.Application.ThreadException += (object sender, ThreadExceptionEventArgs e) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                log.Fatal(e.Exception, "WindowsFormsApplicationThreadException");
                MessageBox.Show("An unrecoverable error has occurred. The application will now exit.", "WindowsFormsApplicationThreadException");
                ExitApp();
            };

            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                if (e.ExceptionObject is Exception ex)
                    log.Fatal(ex, "AppDomainUnhandledException");
                MessageBox.Show("An unrecoverable error has occurred. The application will now exit.", "AppDomainUnhandledException");
                ExitApp();
            };

            Application.Current.DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs e) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                e.Handled = true;
                log.Error(e.Exception, "DispatcherUnhandledException");
                NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Exception.ToString(), "An error has occurred.");
            };

            return log;
        }

        private async Task SetupMutex(string[] args)
        {
            _processor = new MutexArgsForwarder();
            _processor.ArgsReceived += SynchronizationContextEventHandler.CreateDelegate<CommandLineEventArgs>((s, e) =>
            {
                OnFilesReceived(e.Args);
            });

            try
            {
                if (await _processor.Startup(args) == false)
                {
                    if (Debugger.IsAttached && await NiceDialog.ShowDialogAsync(null, NiceDialogIcon.Warning,
                            "There is already an instance of clowd running. Would you like to kill it before continuing?",
                            "Debugger attached; Clowd already running",
                            "Kill Clowd", "Exit"))
                    {
                        KillOtherClowdProcess();
                        if (await _processor.Startup(args) == false)
                            throw new Exception("Unable to create new startup mutex, a mutex already exists. Another Clowd instance? Uninstaller?");
                    }
                    else
                    {
                        // clowd is already running, and we've forwarded args successfully
                        Environment.Exit(0);
                    }
                }
            }
            catch (TimeoutException)
            {
                // there is an unresponsive clowd process, try to kill it and re-start
                KillOtherClowdProcess();
                if (await _processor.Startup(args) == false)
                    throw new Exception("Unable to create new startup mutex, a mutex already exists. Another Clowd instance? Uninstaller?");
            }
        }

        private void SetupTrayIconAndTheme()
        {
            // tray icon
            if (_taskbarIcon == null)
            {
                _taskbarIcon = new TaskbarIcon();
                //_taskbarIcon.WndProcMessageReceived += OnWndProcMessageReceived;
                _taskbarIcon.ToolTipText = "Clowd\nRight click me to see what I can do!";
                _taskbarIcon.TrayMouseDoubleClick += (s, e) => PageManager.Current.GetSettingsPage().Open();
            }

            // force/refresh the correct icon size
            _taskbarIcon.Icon = AppStyles.AppIconGdi;

            void setGestureText(MenuItem item, GlobalTrigger trigger)
            {
                item.SetBinding(
                    MenuItem.InputGestureTextProperty,
                    new Binding(nameof(GlobalTrigger.KeyGestureText)) { Source = trigger });
            }

            // context menu
            ContextMenu context = new ContextMenu();
            var capture = new MenuItem() { Header = "_Capture Screen" };
            setGestureText(capture, SettingsRoot.Current.Hotkeys.CaptureRegionShortcut);
            capture.Click += async (s, e) =>
            {
                // wait long enough for context menu to disappear.
                await Task.Delay(400);
                StartCapture();
            };
            context.Items.Add(capture);

            var paste = new MenuItem() { Header = "_Upload Clipboard" };
            paste.Click += (s, e) => Paste();
            setGestureText(paste, SettingsRoot.Current.Hotkeys.ClipboardUploadShortcut);
            context.Items.Add(paste);

            var uploadFile = new MenuItem() { Header = "Upload from _File" };
            uploadFile.Click += (s, e) => UploadFile();
            setGestureText(uploadFile, SettingsRoot.Current.Hotkeys.FileUploadShortcut);
            context.Items.Add(uploadFile);

            context.Items.Add(new Separator());

            var colorp = new MenuItem() { Header = "Color Pic_ker" };
            colorp.Click += (s, e) => NiceDialog.ShowColorViewer();
            context.Items.Add(colorp);

            var screend = new MenuItem() { Header = "_Draw on Screen" };
            screend.Click += (s, e) => PageManager.Current.GetLiveDrawPage().Open();
            setGestureText(screend, SettingsRoot.Current.Hotkeys.DrawOnScreenShortcut);
            context.Items.Add(screend);

            var editor = new MenuItem() { Header = "Image _Editor" };
            editor.Click += (s, e) => EditorWindow.ShowSession(null);
            context.Items.Add(editor);

            context.Items.Add(new Separator());

            var togglerec = new MenuItem() { Header = "_Start / Stop Recording" };
            togglerec.Click += (s, e) => ToggleScreenRecording();
            togglerec.SetBinding(
                MenuItem.IsEnabledProperty,
                new Binding(nameof(PageManager.IsVideoCapturePageOpen)) { Source = PageManager.Current });
            setGestureText(togglerec, SettingsRoot.Current.Hotkeys.StartStopRecordingShortcut);
            context.Items.Add(togglerec);

            context.Items.Add(new Separator());

            var uploads = new MenuItem() { Header = "_Recents & Uploads" };
            uploads.Click += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
            context.Items.Add(uploads);

            var settings = new MenuItem() { Header = "Se_ttings" };
            settings.Click += (s, e) => PageManager.Current.GetSettingsPage().Open(SettingsPageTab.SettingsGeneral);
            context.Items.Add(settings);

            var exit = new MenuItem() { Header = "E_xit" };
            exit.Click += async (s, e) =>
            {
                if (SettingsRoot.Current.General.ConfirmClose)
                {
                    TaskDialogButton okButton = TaskDialogButton.Yes;
                    TaskDialogButton cancelButton = TaskDialogButton.Cancel;

                    var dialog = new TaskDialogPage()
                    {
                        Heading = "Are you sure you wish to close Clowd?",
                        Text = "If you close Clowd, it will stop any in-progress uploads and you will be unable to upload anything new.",
                        Icon = TaskDialogIcon.Warning,
                        Verification = "&Don’t ask me this again",
                        Buttons = new TaskDialogButtonCollection()
                        {
                            okButton,
                            cancelButton,
                        }
                    };

                    var clicked = await dialog.ShowAsNiceDialogAsync(null);
                    if (clicked == okButton)
                    {
                        if (dialog.Verification?.Checked == true)
                            SettingsRoot.Current.General.ConfirmClose = false;
                        ExitApp();
                    }
                }
                else
                {
                    ExitApp();
                }
            };
            context.Items.Add(exit);
            _taskbarIcon.ContextMenu = context;
        }

        public void ToggleScreenRecording()
        {
            var vid = PageManager.Current.GetExistingVideoCapturePage();
            if (vid == null) return;

            try
            {
                if (vid.IsRecording)
                {
                    vid.StopRecording();
                }
                else
                {
                    vid.StartRecording();
                }
            }
            catch {; } // don't really care if recorder is in a bad state.
        }

        public void StartCapture(ScreenRect region = null)
        {
            PageManager.Current.GetScreenCapturePage().Open(region);
        }

        public void QuickCaptureFullScreen()
        {
            var curs = Platform.Current.GetMousePosition();
            var scre = Platform.Current.GetScreenFromPoint(curs);
            StartCapture(scre.Bounds);
        }

        public void QuickCaptureCurrentWindow()
        {
            var wnd = Platform.Current.GetForegroundWindow();
            StartCapture(wnd.DwmRenderBounds);
        }

        public async void UploadFile(Window owner = null)
        {
            var result = await NiceDialog.ShowSelectFilesDialog(owner, "Select files to upload", SettingsRoot.Current.General.LastUploadPath, true);
            if (result != null)
                OnFilesReceived(result);
        }

        public async void Paste()
        {
            var data = await ClipboardDataObject.GetClipboardData();

            if (data.ContainsImage())
            {
                UploadManager.UploadImage(data.GetImage(), "Clipboard Image");
            }
            else if (data.ContainsText())
            {
                UploadManager.UploadText(data.GetText(), "Clipboard Text");
            }
            else if (data.ContainsFileDropList())
            {
                var collection = data.GetFileDropList();
                OnFilesReceived(collection);
            }
        }

        public void ExitApp()
        {
            try { SettingsRoot.Current.Save(); }
            catch { }

            try { _taskbarIcon.Dispose(); }
            catch { }

            try { LogManager.Flush(); }
            catch { }

            ToastNotificationManagerCompat.Uninstall();
            Environment.Exit(0);
        }

        private async void OnFilesReceived(string[] filePaths)
        {
            await UploadManager.UploadSeveralFiles(filePaths);
        }

        private void KillOtherClowdProcess()
        {
            var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName("Clowd").Where(p => p.Id != current.Id).ToArray();
            foreach (var p in processes)
                p.Kill();
            foreach (var p in processes)
                p.WaitForExit();
        }
    }
}
