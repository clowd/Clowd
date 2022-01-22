using System;
using System.Data;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clowd.Capture;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;
using Clowd.Video;
using Hardcodet.Wpf.TaskbarNotification;
using LightInject;
using ModernWpf;
using Ookii.Dialogs.Wpf;
using RT.Serialization;
using RT.Util.ExtensionMethods;
using Color = System.Windows.Media.Color;

namespace Clowd
{
    public partial class App : Application
    {
        public static T GetService<T>() => Container.GetInstance<T>();

        public static new App Current => IsDesignMode ? null : (App)Application.Current;
        public static bool CanUpload => !IsDesignMode;
        public static bool IsDesignMode => System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());
        public static IScopedLog DefaultLog { get; private set; }
        public static ServiceContainer Container { get; private set; }

        private TaskbarIcon _taskbarIcon;
        private MutexArgsForwarder _processor;

        protected override async void OnStartup(StartupEventArgs e)
        {
            DefaultLog = new DefaultScopedLog("Clowd");

            var appArgs = SquirrelUtil.Startup(e.Args);

            try
            {
                base.OnStartup(e);

                // initialize GDI+ (our native lib depends on it, but does not initialize it)
                new System.Drawing.Region().Dispose();

                if (!Constants.Debugging)
                    SetupExceptionHandling();

                await SetupMutex(appArgs);
                await SetupSettings();
                SetupDependencyInjection();

                // theme
                SetupTrayIconAndTheme();
                ThemeManager.Current.ActualApplicationThemeChanged += (s, e) => SetupTrayIconAndTheme();

                // start receiving command line arguments
                _processor.Ready();

                if (SquirrelUtil.IsFirstRun)
                {
                    UI.MainWindow.ShowWindow(MainWindowPage.About);
                }
                else
                {
                    EditorWindow.ShowAllPreviouslyActiveSessions();
                    if (SquirrelUtil.JustRestarted)
                        UI.MainWindow.ShowWindow(MainWindowPage.SettingsGeneral);
                }
            }
            catch (Exception ex)
            {
                DefaultLog.Error(ex);
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
            try
            {
                SettingsRoot.LoadDefault();
            }
            catch (Exception ex)
            {
                if (await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Error,
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

            SettingsRoot.Current.Hotkeys.FileUploadShortcut.TriggerExecuted += (s, e) => Paste();
            SettingsRoot.Current.Hotkeys.CaptureActiveShortcut.TriggerExecuted += (s, e) => QuickCaptureCurrentWindow();
            SettingsRoot.Current.Hotkeys.CaptureFullscreenShortcut.TriggerExecuted += (s, e) => QuickCaptureFullScreen();
            SettingsRoot.Current.Hotkeys.CaptureRegionShortcut.TriggerExecuted += (s, e) => StartCapture();

            void setTheme()
            {
                ThemeManager.Current.ApplicationTheme = SettingsRoot.Current.General.Theme switch
                {
                    AppTheme.Light => ApplicationTheme.Light,
                    AppTheme.Dark => ApplicationTheme.Dark,
                    _ => null,
                };
            }
            SettingsRoot.Current.General.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsGeneral.Theme))
                {
                    setTheme();
                }
            };
            setTheme();
        }

        private void SetupExceptionHandling()
        {
            DefaultScopedLog.EnableSentry("https://0a572df482544fc19cdc855d17602fa4:012770b74f37410199e1424faf7c51d3@sentry.io/260666");

            System.Windows.Forms.Application.ThreadException += (object sender, ThreadExceptionEventArgs e) =>
            {
                DefaultLog.Error("WindowsFormsApplicationThreadException", e.Exception);
                MessageBox.Show("An unrecoverable error has occurred. The application will now exit.", "WindowsFormsApplicationThreadException");
            };

            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    DefaultLog.Error("AppDomainUnhandledException", ex);
                MessageBox.Show("An unrecoverable error has occurred. The application will now exit.", "AppDomainUnhandledException");
            };

            Application.Current.DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs e) =>
            {
                e.Handled = true;
                DefaultLog.Error("DispatcherUnhandledException", e.Exception);
                NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, e.Exception.ToString(), "An error has occurred.");
            };
        }

        private async Task SetupMutex(string[] args)
        {
            _processor = new MutexArgsForwarder(Constants.ClowdMutex);
            _processor.ArgsReceived += SynchronizationContextEventHandler.CreateDelegate<CommandLineEventArgs>((s, e) =>
            {
                OnFilesReceived(e.Args);
            });

            try
            {
                bool mutexCreated = _processor.Startup(args);
                if (!mutexCreated)
                {
                    if (Constants.Debugging && await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Warning,
                        "There is already an instance of clowd running. Would you like to kill it before continuing?",
                        "Debugger attached; Clowd already running",
                        "Kill Clowd", "Exit"))
                    {
                        KillOtherClowdProcess();
                        if (!_processor.Startup(args))
                            throw new Exception("Unable to create new startup mutex, a mutex already exists. Another Clowd instance? Uninstaller?");
                    }
                    else
                    {
                        // clowd is already running, and we've forwarded args successfully
                        Environment.Exit(0);
                    }
                }
            }
            catch (HttpRequestException)
            {
                // there is an unresponsive clowd process, try to kill it and re-start
                KillOtherClowdProcess();
                if (!_processor.Startup(args))
                    throw new Exception("Unable to create new startup mutex, a mutex already exists. Another Clowd instance? Uninstaller?");
            }
        }

        private void SetupDependencyInjection()
        {
            var container = new ServiceContainer();
            container.Register<IScopedLog>(_ => new DefaultScopedLog(Constants.ClowdAppName), new PerContainerLifetime());
            container.Register<IServiceFactory>(_ => container);

            // ui
            container.Register<IPageManager, PageManager>();
            //container.Register<IScreenCapturePage, ScreenCaptureWindow>(new PerScopeLifetime());
            container.Register<ILiveDrawPage, AntFu7.LiveDraw.LiveDrawWindow>(new PerScopeLifetime());
            container.Register<IVideoCapturePage, VideoCaptureWindow>(new PerScopeLifetime());

            // we create this TasksView here in main thread so there won't be issues with MTA threads requesting this object in the future
            var tasksView = new TasksView();
            container.Register<ITasksView>(_ => tasksView);

            // video
            container.Register<IVideoCapturer>(f => new ObsCapturer(DefaultLog, Path.Combine(AppContext.BaseDirectory, "obs-express")), new PerScopeLifetime());

            Container = container;
        }

        private void SetupTrayIconAndTheme()
        {
            // tray icon
            if (_taskbarIcon == null)
            {
                _taskbarIcon = new TaskbarIcon();
                //_taskbarIcon.WndProcMessageReceived += OnWndProcMessageReceived;
                _taskbarIcon.ToolTipText = "Clowd\nRight click me or drop something on me\nto see what I can do!";
                _taskbarIcon.TrayMouseDoubleClick += (s, e) => UI.MainWindow.ShowWindow();
            }

            // force/refresh the correct icon size
            _taskbarIcon.Icon = AppStyles.AppIconGdi;

            // context menu
            ContextMenu context = new ContextMenu();
            var capture = new MenuItem() { Header = "_Capture Screen" };
            capture.Click += async (s, e) =>
            {
                // wait long enough for context menu to disappear.
                await Task.Delay(400);
                StartCapture();
            };
            context.Items.Add(capture);

            if (App.CanUpload)
            {
                var paste = new MenuItem() { Header = "_Paste" };
                paste.Click += (s, e) => Paste();
                context.Items.Add(paste);

                var uploadFile = new MenuItem() { Header = "Upload _Files" };
                uploadFile.Click += (s, e) => UploadFile();
                context.Items.Add(uploadFile);

                var uploads = new MenuItem() { Header = "Show _Uploads" };
                uploads.Click += (s, e) => GetService<ITasksView>().Show();
                context.Items.Add(uploads);
            }

            context.Items.Add(new Separator());

            var colorp = new MenuItem() { Header = "Color Pic_ker" };
            colorp.Click += (s, e) => NiceDialog.ShowColorDialogAsync(null, Colors.Transparent);
            context.Items.Add(colorp);

            var screend = new MenuItem() { Header = "_Draw on Screen" };
            screend.Click += (s, e) =>
            {
                Container.GetInstance<IPageManager>().CreateLiveDrawPage().Open();
            };
            context.Items.Add(screend);

            var editor = new MenuItem() { Header = "Image _Editor" };
            editor.Click += (s, e) => EditorWindow.ShowSession(SessionManager.Current.CreateNewSession());
            context.Items.Add(editor);

            context.Items.Add(new Separator());

            var settings = new MenuItem() { Header = "_Settings" };
            settings.Click += (s, e) =>
            {
                UI.MainWindow.ShowWindow();
            };
            context.Items.Add(settings);

            var exit = new MenuItem() { Header = "E_xit" };
            exit.Click += async (s, e) =>
            {
                if (SettingsRoot.Current.General.ConfirmClose)
                {
                    using (TaskDialog dialog = new TaskDialog())
                    {
                        dialog.WindowTitle = "Clowd";
                        dialog.MainInstruction = "Are you sure you wish to close Clowd?";
                        dialog.Content = "If you close clowd, it will stop any in-progress uploads and you will be unable to upload anything new.";
                        dialog.VerificationText = "Don't ask me this again";
                        dialog.MainIcon = TaskDialogIcon.Warning;

                        TaskDialogButton okButton = new TaskDialogButton(ButtonType.Yes);
                        TaskDialogButton cancelButton = new TaskDialogButton(ButtonType.No);
                        dialog.Buttons.Add(okButton);
                        dialog.Buttons.Add(cancelButton);

                        var clicked = await dialog.ShowAsNiceDialogAsync(null);
                        if (clicked == okButton)
                        {
                            if (dialog.IsVerificationChecked == true)
                                SettingsRoot.Current.General.ConfirmClose = false;
                            ExitApp();
                        }
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

        public void StartCapture(ScreenRect region = null)
        {
            //Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
            ScreenCaptureWindow.Open();
        }
        public void QuickCaptureFullScreen()
        {
            ScreenCaptureWindow.Open();
            //Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
        }
        public void QuickCaptureCurrentWindow()
        {
            ScreenCaptureWindow.Open();
            //var window = Platform.Current.GetForegroundWindow();
            //Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open(window.Handle);
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
                var img = data.GetImage();
                var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                UploadManager.UploadImage(ms, "png", viewName: "Pasted Image");
            }
            else if (data.ContainsText())
            {
                var ms = new MemoryStream(data.GetText().ToUtf8());
                UploadManager.UploadText(ms, "txt", viewName: "Pasted Text");
            }
            else if (data.ContainsFileDropList())
            {
                var collection = data.GetFileDropList();
                OnFilesReceived(collection);
            }
        }

        public void ExitApp()
        {
            try { SettingsRoot.Current.Save(); } catch { }
            try { _taskbarIcon.Dispose(); } catch { }
            Environment.Exit(0);
        }

        private async void OnFilesReceived(string[] filePaths)
        {
            await UploadManager.UploadFiles(filePaths);
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
