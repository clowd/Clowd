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
using Clowd.Interop;
using Clowd.UI;
using Clowd.UI.Helpers;
using Clowd.Util;
using LightInject;
using NotifyIconLib;
using Ookii.Dialogs.Wpf;
using RT.Serialization;
using RT.Util;
using RT.Util.ExtensionMethods;
using ScreenVersusWpf;
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
        public GeneralSettings Settings { get; private set; }
        public Color AccentColor { get; private set; }
        public static bool Debugging => Debugger.IsAttached;

        private TaskbarIcon _taskbarIcon;
        private ResourceDictionary _lightBase;
        private ResourceDictionary _darkBase;
        private MutexArgsForwarder _processor;

        protected override async void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);

                // initialize GDI+ (our native lib depends on it, but does not initialize it)
                new System.Drawing.Region().Dispose();

                DefaultLog = new DefaultScopedLog("Clowd");
                if (!Debugging)
                    DefaultScopedLog.EnableSentry("https://0a572df482544fc19cdc855d17602fa4:012770b74f37410199e1424faf7c51d3@sentry.io/260666");

                SetupExceptionHandling();
                ScreenTools.InitializeDpi(ScreenTools.GetSystemDpi());
                await SetupMutex(e);
                SetupSettings();
                SetupDependencyInjection();
                SetupAccentColors();
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
                    if (Debugging && await NiceDialog.ShowPromptAsync(null, NiceDialogIcon.Warning,
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

        private void SetupSettings()
        {
            GeneralSettings tmp;
            Classify.DefaultOptions = new ClassifyOptions();
            Classify.DefaultOptions.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
            Classify.DefaultOptions.AddTypeSubstitution(new ClassifyColorTypeOptions());

            SettingsUtil.LoadSettings(out tmp);
            Settings = tmp;

            if (Settings.FirstRun)
            {
                Settings.FirstRun = false;
                // TODO do something here on first run..
            }

            Settings.Save();
        }

        private void SetupDependencyInjection()
        {
            var container = new ServiceContainer();
            container.Register<IScopedLog>(_ => new DefaultScopedLog(Constants.ClowdAppName), new PerContainerLifetime());
            container.Register<IServiceFactory>(_ => container);

            // settings
            container.Register<GeneralSettings>(_ => Settings);
            container.Register<VideoCapturerSettings>(f => f.GetInstance<GeneralSettings>().VideoSettings);

            // ui
            container.Register<IPageManager, PageManager>();
            container.Register<IScreenCapturePage, ScreenCaptureWindow>(new PerScopeLifetime());
            container.Register<ILiveDrawPage, AntFu7.LiveDraw.LiveDrawWindow>(new PerScopeLifetime());
            container.Register<IVideoCapturePage, VideoCaptureWindow>(new PerScopeLifetime());
            container.Register<ISettingsPage>(_ => TemplatedWindow.SingletonWindowFactory<SettingsPage>(), new PerScopeLifetime());

            // we create this TasksView here in main thread so there won't be issues with MTA threads requesting this object in the future
            var tasksView = new TasksView();
            container.Register<ITasksView>(_ => tasksView);






            //Window wnd;
            //if ((wnd = TemplatedWindow.GetWindow(typeof(SettingsPage))) != null)
            //{
            //    wnd.MakeForeground();
            //}
            //else if ((wnd = TemplatedWindow.GetWindow(typeof(HomePage))) != null)
            //{
            //    TemplatedWindow.SetContent(wnd, new SettingsPage());
            //    wnd.MakeForeground();
            //}
            //else
            //{
            //    wnd = TemplatedWindow.CreateWindow("Clowd", new SettingsPage());
            //    wnd.Show();
            //    wnd.MakeForeground();
            //}

            //if (category != null)
            //    TemplatedWindow.GetContent<SettingsPage>(wnd).SetCurrentTab(category.Value);








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

        private void SetupAccentColors()
        {
            //var scheme = Settings.ColorScheme;
            //var baseColor = Settings.AccentScheme == AccentScheme.User ? Settings.UserAccentColor : AreoColor.GetColor();
            var baseColor = Settings.AccentScheme == AccentScheme.User ? Settings.UserAccentColor : AreoColor.GetColor();


            //_lightBase = new ResourceDictionary
            //{
            //    Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseLight.xaml", UriKind.RelativeOrAbsolute)
            //};
            //if (!this.Resources.MergedDictionaries.Contains(_lightBase))
            //    this.Resources.MergedDictionaries.Add(_lightBase);
            //if (_lightBase == null)
            //{
            //    _lightBase = new ResourceDictionary
            //    {
            //        Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseLight.xaml", UriKind.RelativeOrAbsolute)
            //    };
            //}
            //if (_darkBase == null)
            //{
            //    _darkBase = new ResourceDictionary
            //    {
            //        Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseDark.xaml", UriKind.RelativeOrAbsolute)
            //    };
            //}

            //if (scheme == ColorScheme.Light)
            //{
            //    //remove dark base dictionary
            //    if (this.Resources.MergedDictionaries.Contains(_darkBase))
            //        this.Resources.MergedDictionaries.Remove(_darkBase);
            //    //add light base dictionary
            //    if (!this.Resources.MergedDictionaries.Contains(_lightBase))
            //        this.Resources.MergedDictionaries.Add(_lightBase);
            //}
            //else if (scheme == ColorScheme.Dark)
            //{
            //    //remove light base dictionary
            //    if (this.Resources.MergedDictionaries.Contains(_lightBase))
            //        this.Resources.MergedDictionaries.Remove(_lightBase);
            //    //add dark base dictionary
            //    if (!this.Resources.MergedDictionaries.Contains(_darkBase))
            //        this.Resources.MergedDictionaries.Add(_darkBase);
            //}

            var hsl = HSLColor.FromRGB(baseColor);
            hsl.Lightness = hsl.Lightness - 10;
            baseColor = hsl.ToRGB();

            //http://stackoverflow.com/a/596243/184746
            double luminance = Math.Sqrt(0.299 * Math.Pow(baseColor.R, 2) + 0.587 * Math.Pow(baseColor.G, 2) + 0.114 * Math.Pow(baseColor.B, 2));
            if (luminance > 170)
            {
                //create a dark foreground color, this accent color is light.
                var dark = HSLColor.FromRGB(baseColor);
                dark.Lightness = 15;
                this.Resources["IdealForegroundColor"] = dark.ToRGB();
            }
            else
            {
                this.Resources["IdealForegroundColor"] = Colors.White;
            }

            this.AccentColor = baseColor;
            this.Resources["HighlightColor"] = baseColor;
            this.Resources["AccentColor"] = Color.FromArgb(204, baseColor.R, baseColor.G, baseColor.B); //80%
            this.Resources["AccentColor2"] = Color.FromArgb(153, baseColor.R, baseColor.G, baseColor.B); //60%
            this.Resources["AccentColor3"] = Color.FromArgb(102, baseColor.R, baseColor.G, baseColor.B); //40%
            this.Resources["AccentColor4"] = Color.FromArgb(51, baseColor.R, baseColor.G, baseColor.B); //20%

            this.Resources["HighlightBrush"] = new SolidColorBrush(baseColor);
            ((Freezable)this.Resources["HighlightBrush"]).Freeze();
            this.Resources["AccentColorBrush"] = new SolidColorBrush((Color)this.Resources["AccentColor"]);
            ((Freezable)this.Resources["AccentColorBrush"]).Freeze();
            this.Resources["AccentColorBrush2"] = new SolidColorBrush((Color)this.Resources["AccentColor2"]);
            ((Freezable)this.Resources["AccentColorBrush2"]).Freeze();
            this.Resources["AccentColorBrush3"] = new SolidColorBrush((Color)this.Resources["AccentColor3"]);
            ((Freezable)this.Resources["AccentColorBrush3"]).Freeze();
            this.Resources["AccentColorBrush4"] = new SolidColorBrush((Color)this.Resources["AccentColor4"]);
            ((Freezable)this.Resources["AccentColorBrush4"]).Freeze();
            this.Resources["WindowTitleColorBrush"] = new SolidColorBrush((Color)this.Resources["AccentColor"]);
            ((Freezable)this.Resources["WindowTitleColorBrush"]).Freeze();
            var gstops = new GradientStopCollection()
            {
                new GradientStop((Color)this.Resources["HighlightColor"], 0),
                new GradientStop((Color)this.Resources["AccentColor3"], 1),
            };
            this.Resources["ProgressBrush"] = new LinearGradientBrush(gstops, new Point(1.002, 0.5), new Point(0.001, 0.5));
            ((Freezable)this.Resources["ProgressBrush"]).Freeze();
            this.Resources["CheckmarkFill"] = new SolidColorBrush((Color)this.Resources["AccentColor"]);
            ((Freezable)this.Resources["CheckmarkFill"]).Freeze();
            this.Resources["RightArrowFill"] = new SolidColorBrush((Color)this.Resources["AccentColor"]);
            ((Freezable)this.Resources["RightArrowFill"]).Freeze();
            this.Resources["IdealForegroundBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]);
            ((Freezable)this.Resources["IdealForegroundBrush"]).Freeze();
            this.Resources["IdealForegroundDisabledBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]) { Opacity = 0.4 };
            ((Freezable)this.Resources["IdealForegroundDisabledBrush"]).Freeze();
            this.Resources["AccentSelectedColorBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]);
            ((Freezable)this.Resources["AccentSelectedColorBrush"]).Freeze();
        }

        private void SetupTrayIcon()
        {
            _taskbarIcon = new TaskbarIcon();
            //_taskbarIcon.TrayDrop += OnTaskbarIconDrop;
            _taskbarIcon.WndProcMessageReceived += OnWndProcMessageReceived;
            _taskbarIcon.ToolTipText = "Clowd\nRight click me or drop something on me\nto see what I can do!";

            //force the correct icon size
            string iconLocation = SysInfo.IsWindows8OrLater ? "/Images/default-white.ico" : "/Images/default.ico";
            System.Windows.Resources.StreamResourceInfo sri = Application.GetResourceStream(new Uri("pack://application:,,," + iconLocation));
            var desiredSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
            var avaliableSizes = new[] { 64, 48, 40, 32, 24, 20, 16 };
            var nearest = avaliableSizes.OrderBy(x => Math.Abs(x - desiredSize)).First();
            var icon = new System.Drawing.Icon(sri.Stream, new System.Drawing.Size(nearest, nearest));
            _taskbarIcon.Icon = icon;
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

            var editor = new MenuItem() { Header = "Image _Editor" };
            editor.Click += (s, e) => ImageEditorPage.ShowNewEditor();
            context.Items.Add(editor);

            context.Items.Add(new Separator());

            //var home = new MenuItem() { Header = "Open _Clowd" };
            //home.Click += (s, e) =>
            //{
            //    ShowHome();
            //};
            //context.Items.Add(home);

            var settings = new MenuItem() { Header = "_Settings" };
            settings.Click += (s, e) =>
            {
                Container.GetInstance<IPageManager>().CreateSettingsPage().Open();
            };
            context.Items.Add(settings);

            var exit = new MenuItem() { Header = "E_xit" };
            exit.Click += async (s, e) =>
            {
                if (Settings.ConfirmClose)
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
                                Settings.ConfirmClose = false;
                            Settings.Save();
                            Application.Current.Shutdown();
                        }
                    }
                }
                else
                {
                    Settings.Save();
                    Application.Current.Shutdown();
                }
            };
            context.Items.Add(exit);

            _taskbarIcon.ContextMenu = context;
        }

        public void ResetSettings()
        {
            Settings.Dispose();
            Settings = new GeneralSettings()
            {
                FirstRun = Settings.FirstRun,
                LastUploadPath = Settings.LastUploadPath,
                LastSavePath = Settings.LastSavePath,
            };
            Settings.SaveQuiet();
        }

        public void StartCapture(ScreenRect? region = null)
        {
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
        }
        public void QuickCaptureFullScreen()
        {
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open();
        }
        public void QuickCaptureCurrentWindow()
        {
            Container.GetInstance<IPageManager>().CreateScreenCapturePage().Open(USER32.GetForegroundWindow());
        }
        public async void UploadFile(Window owner = null)
        {
            var result = await NiceDialog.ShowSelectFilesDialog(owner, "Select files to upload", Settings.LastUploadPath, true);
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
            if (obj == (uint)Interop.WindowMessage.WM_DWMCOLORIZATIONCOLORCHANGED
                && Settings?.AccentScheme == AccentScheme.System)
            {
                SetupAccentColors();
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
