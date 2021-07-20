using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel;
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
using LightInject;
using NotifyIconLib;
using Ookii.Dialogs.Wpf;
using RT.Serialization;
using RT.Util;
using RT.Util.ExtensionMethods;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

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
            try
            {
                base.OnStartup(e);

                // initialize GDI+ (our native lib depends on it, but does not initialize it)
                new System.Drawing.Region().Dispose();

                // default classify settings
                Classify.DefaultOptions = new ClassifyOptions();
                Classify.DefaultOptions.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
                Classify.DefaultOptions.AddTypeSubstitution(new ClassifyColorTypeOptions());

                DefaultLog = new DefaultScopedLog("Clowd");
                if (!Constants.Debugging)
                    DefaultScopedLog.EnableSentry("https://0a572df482544fc19cdc855d17602fa4:012770b74f37410199e1424faf7c51d3@sentry.io/260666");

                SetupExceptionHandling();
                await SetupMutex(e);

                try
                {
                    ClowdSettings.LoadDefault();
                }
                catch (Exception ex)
                {
                    if (await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Error,
                        "There was an error loading the application configuration.\r\nWould you like to reset the config to default or exit the application?",
                        "Error loading app config", "Reset Config", "Exit Application", NiceDialogIcon.Information, ex.ToString()))
                    {
                        ClowdSettings.CreateNew();
                        ClowdSettings.Current.Save();
                    }
                    else
                    {
                        Environment.Exit(1);
                    }
                }

                SetupDependencyInjection();
                SetupTrayIcon();
                SetupTrayContextMenu();

                // start receiving command line arguments
                _processor.Ready();

                try
                {
                    // until proper module updater is built... we check for OBS updates manually.
                    var obs = Container.GetInstance<IModuleInfo<IVideoCapturer>>();
                    await obs.CheckForUpdates(false);
                    if (obs.UpdateAvailable != null)
                    {
                        DefaultLog.Info("Update available for OBS module... Downloading");
                        await obs.Install(obs.UpdateAvailable);
                        DefaultLog.Info("Update available for OBS module... Done");
                    }
                }
                catch (Exception ecx)
                {
                    DefaultLog.Error(ecx);
                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ecx.ToString(), "Error updating OBS, will try again later.");
                }
            }
            catch (Exception ex)
            {
                DefaultLog.Error(ex);
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.ToString(), "Error starting Clowd. The program will now exit.");
                Environment.Exit(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _processor.Dispose();
            _taskbarIcon.Dispose();
            base.OnExit(e);
        }

        private void SetupExceptionHandling()
        {

#if false && DEBUG
            if (Debugger.IsAttached)
                return;

            Action<Exception> showError = (Exception e) => { MessageBox.Show($"Unhandled exception: {e.Message}\n{e.GetType()}\n\n{e.StackTrace}", "Unhandled exception", MessageBoxButton.OK, MessageBoxImage.Error); };

            System.Windows.Forms.Application.ThreadException += (object sender, ThreadExceptionEventArgs e) => { showError(e.Exception); };
            Application.Current.DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs e) => { showError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                if (e.ExceptionObject is Exception)
                    showError((Exception)e.ExceptionObject);
                else
                    MessageBox.Show($"Unhandled exception: {e.ExceptionObject}");
            };
#else
            // create event handlers for unhandled exceptions
            //Sentry.Default.BeforeSend = (req) =>
            //{
            //    // here we should check if the event is Fatal, if it is, show dialog and attach user feedback to the message

            //    if (!e.IsUnhandledError)
            //        return;

            //    // we want to show an error dialog, give the user a chance to add details, but we will want to 
            //    // send the error regardless of what the users chooses to do.
            //    if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            //    {
            //        Application.Current.Dispatcher.Invoke(new Func<EventSubmittingEventArgs, bool>(ShowDialog), DispatcherPriority.Send, e);
            //    }
            //    else
            //    {
            //        ShowDialog(e);
            //    }
            //};

            ThreadExceptionEventHandler OnApplicationThreadException = (sender, args) =>
            {
                DefaultLog.Error("ApplicationThreadException", args.Exception);
            };

            DispatcherUnhandledExceptionEventHandler OnApplicationDispatcherUnhandledException = (sender, args) =>
            {
                DefaultLog.Error("DispatcherUnhandledException", args.Exception);
            };

            try
            {
                System.Windows.Forms.Application.ThreadException += OnApplicationThreadException;
            }
            catch (Exception ex)
            {
                DefaultLog.Error(ex);
            }

            try
            {
                Application.Current.DispatcherUnhandledException += OnApplicationDispatcherUnhandledException;
            }
            catch (Exception ex)
            {
                DefaultLog.Error(ex);
            }
#endif
        }

        private async Task SetupMutex(StartupEventArgs e)
        {
            _processor = new MutexArgsForwarder(Constants.ClowdMutex);
            _processor.ArgsReceived += SynchronizationContextEventHandler.CreateDelegate<CommandLineEventArgs>((s, e) =>
            {
                OnFilesReceived(e.Args);
            });

            try
            {
                bool mutexCreated = _processor.Startup(e.Args);
                if (!mutexCreated)
                {
                    if (Constants.Debugging && await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Warning,
                        "There is already an instance of clowd running. Would you like to kill it before continuing?",
                        "Debugger attached; Clowd already running",
                        "Kill Clowd", "Exit"))
                    {
                        KillOtherClowdProcess();
                        if (!_processor.Startup(e.Args))
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
                if (!_processor.Startup(e.Args))
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
            container.Register<IScreenCapturePage, ScreenCaptureWindow>(new PerScopeLifetime());
            container.Register<ILiveDrawPage, AntFu7.LiveDraw.LiveDrawWindow>(new PerScopeLifetime());
            container.Register<IVideoCapturePage, VideoCaptureWindow>(new PerScopeLifetime());

            // we create this TasksView here in main thread so there won't be issues with MTA threads requesting this object in the future
            var tasksView = new TasksView();
            container.Register<ITasksView>(_ => tasksView);

            // video
            container.Register<IModuleInfo<IVideoCapturer>, Video.ObsModule>(nameof(Video.ObsModule), new PerContainerLifetime());
            container.Register<IVideoCapturer>(f =>
            {
                var module = f.GetInstance<IModuleInfo<IVideoCapturer>>();
                if (module.InstalledVersion == null)
                    return null;
                return module.GetNewInstance();
            }, new PerScopeLifetime());

            Container = container;
        }

        private void SetupTrayIcon()
        {
            if (_taskbarIcon == null)
            {
                _taskbarIcon = new TaskbarIcon();
                _taskbarIcon.WndProcMessageReceived += OnWndProcMessageReceived;
                _taskbarIcon.ToolTipText = "Clowd\nRight click me or drop something on me\nto see what I can do!";
            }

            //force the correct icon size
            string iconLocation = PlatformUtil.Windows.DarkMode.IsDarkModeEnabled() ? "/Images/default-white.ico" : "/Images/default.ico";
            System.Windows.Resources.StreamResourceInfo sri = Application.GetResourceStream(new Uri("pack://application:,,," + iconLocation));
            var desiredSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
            var avaliableSizes = new[] { 64, 48, 40, 32, 24, 20, 16 };
            var nearest = avaliableSizes.OrderBy(x => Math.Abs(x - desiredSize)).First();
            _taskbarIcon.Icon = new System.Drawing.Icon(sri.Stream, new System.Drawing.Size(nearest, nearest));
        }

        internal void SetupTrayContextMenu()
        {
            ContextMenu context = new ContextMenu();
            var capture = new MenuItem() { Header = "_Capture Screen" };
            capture.Click += async (s, e) =>
            {
                //wait long enough for context menu to disappear.
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

            //var editor = new MenuItem() { Header = "Image _Editor" };
            //editor.Click += (s, e) => ImageEditorPage.ShowNewEditor();
            //context.Items.Add(editor);

            context.Items.Add(new Separator());

            var settings = new MenuItem() { Header = "_Settings" };
            settings.Click += (s, e) =>
            {
                var main = Windows.Cast<Window>().Select(w => w as MainWindow).Where(w => w != null).FirstOrDefault();
                if (main != null)
                    main.PlatformWindow.Activate();
                else
                    new MainWindow().Show();
            };
            context.Items.Add(settings);

            var exit = new MenuItem() { Header = "E_xit" };
            exit.Click += async (s, e) =>
            {
                if (ClowdSettings.Current.General.ConfirmClose)
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
                                ClowdSettings.Current.General.ConfirmClose = false;
                            ClowdSettings.Current.Save();
                            Application.Current.Shutdown();
                        }
                    }
                }
                else
                {
                    ClowdSettings.Current.Save();
                    Application.Current.Shutdown();
                }
            };
            context.Items.Add(exit);

            _taskbarIcon.ContextMenu = context;
        }

        public void StartCapture(ScreenRect region = null)
        {
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
        }
        public void QuickCaptureFullScreen()
        {
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
        }
        public void QuickCaptureCurrentWindow()
        {
            var window = Platform.Current.GetForegroundWindow();
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open(window.Handle);
        }
        public async void UploadFile(Window owner = null)
        {
            var result = await NiceDialog.ShowSelectFilesDialog(owner, "Select files to upload", ClowdSettings.Current.General.LastUploadPath, true);
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

        private void OnWndProcMessageReceived(uint obj)
        {
            if (obj == (uint)PlatformUtil.Windows.WindowMessage.WM_THEMECHANGED)
            {
                SetupTrayIcon();
            }
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
