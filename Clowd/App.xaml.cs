using Clowd.Interop;
using Clowd.Utilities;
using Ionic.Zip;
using NotifyIconLib;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Serialization;
using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Ionic.Zlib;
using SharpRaven;
using SharpRaven.Data;
using Ookii.Dialogs.Wpf;

namespace Clowd
{
    public partial class App : Application
    {
        public static new App Current => IsDesignMode ? null : (App)Application.Current;
        public static bool CanUpload => !IsDesignMode;
        public static bool IsDesignMode => System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());

        public GeneralSettings Settings { get; private set; }
        public Color AccentColor { get; private set; }
        //public string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ClowdAppName);

        //// static instead of const for debugging purposes.
        //public const string ClowdAppName = "Clowd";
        //public const string ClowdNamedPipe = "ClowdRunningPipe";
        //public const string ClowdMutex = "ClowdMutex000";

        private TaskbarIcon _taskbarIcon;
        private bool _prtscrWindowOpen = false;
        private bool _initialized = false;
#if NAPPUPDATE
        private DispatcherTimer _updateTimer;
        private NAppUpdate.Framework.UpdateManager _updateManager;
#endif
        private ResourceDictionary _lightBase;
        private ResourceDictionary _darkBase;
        private Mutex _mutex;
        private ServiceHost _host;
        private string[] _args;
        private System.Timers.Timer _cmdNotifyTimer;
        private List<string> _cmdCache;

        protected override async void OnStartup(StartupEventArgs e)
        {
            //RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            //https://web.archive.org/web/20100817183900/http://blogs.msdn.com/b/jgoldb/archive/2010/04/12/what-s-new-for-performance-in-wpf-in-net-4.aspx
            //https://stackoverflow.com/questions/3060329/system-windows-media-rendercapability-tier-returns-not-the-render-mode
            //https://stackoverflow.com/questions/149763/how-do-you-determine-if-wpf-is-using-hardware-or-software-rendering
            //https://stackoverflow.com/questions/149763/how-do-you-determine-if-wpf-is-using-hardware-or-software-rendering

            //HwndSource hwndSource = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            //HwndTarget hwndTarget = hwndSource.CompositionTarget;
            //var mode = hwndTarget.RenderMode;
            //var m2 = RenderOptions.ProcessRenderMode
            //var teir = RenderCapability.Tier >> 16;


            base.OnStartup(e);
            SetupExceptionHandling();

            try
            {
                _mutex = Mutex.OpenExisting(Constants.ClowdMutex);
                // if we're here, clowd is running already, so pass our command line args and check heartbeat.
                try
                {
                    ChannelFactory<ICommandLineProxy> pipeFactory = new ChannelFactory<ICommandLineProxy>(
                        new NetNamedPipeBinding(),
                        new EndpointAddress("net.pipe://localhost/" + Constants.ClowdNamedPipe));

                    ICommandLineProxy pipeProxy = pipeFactory.CreateChannel();
                    if (!pipeProxy.Heartbeat())
                        throw new Exception($"{Constants.ClowdAppName} unresponsive. This exception shouldnt happen.");

                    if (e.Args.Length > 0)
                    {
                        pipeProxy.PassArgs(e.Args);
                    }
                    pipeFactory.Close();
                    Thread.Sleep(2001);
                    Environment.Exit(0);
                }
                catch
                {
                    var current = Process.GetCurrentProcess();
                    var processes = Process.GetProcessesByName("Clowd").Where(p => p.Id != current.Id).ToArray();
                    if (!processes.Any())
                    {
                        var ex = new InvalidOperationException("The mutex was opened successfully, " +
                                                              $"but there are no {Constants.ClowdAppName} processes running. Uninstaller?");
                        ex.Data.Add("Processes", Process.GetProcesses().Select(p => p.ProcessName).ToArray());
                        ex.ToSentry();
                        Environment.Exit(1);
                    }

                    var isYes = NiceDialog.ShowPromptBlocking(
                        null,
                        NiceDialogIcon.Warning,
                        $"{Constants.ClowdAppName} is already running but seems to be unresponsive. Would you like to restart {Constants.ClowdAppName}?",
                        $"{Constants.ClowdAppName} is unresponsive",
                        "Restart",
                        "Close",
                        NiceDialogIcon.Information,
                        "You may lose anything in-progress work or uploads.");

                    if (isYes)
                    {
                        foreach (var p in processes)
                            p.Kill();
                        foreach (var p in processes)
                            p.WaitForExit();

                        Thread.Sleep(1000);
                        _mutex = Mutex.OpenExisting(Constants.ClowdMutex);
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // the mutex could not be opened, means it does not exist and there is no other clowd
                // instances running. Open a new mutex.
                _mutex = new Mutex(true, Constants.ClowdMutex);
                if (e.Args.Length > 0)
                {
                    _args = e.Args;
                }
            }
            catch (Exception ex)
            {
                // we still want to report this, beacuse it was unexpected, but because we successfully opened the mutex
                // we know there is another Clowd instance running (or uninstaller) and we should close.
                ex.ToSentry();
                Environment.Exit(1);
            }

            SetupServiceHost();
            SetupDpiScaling();
            SetupSettings();
            SetupTrayIcon();
            SetupAccentColors();

            if (Settings.FirstRun)
            {
                // there were no settings to load, show login window.
                Settings.FirstRun = false;
                Settings.Save();
            }

            FinishInit();
        }
        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            _mutex.ReleaseMutex();
            _host.Close();
            _taskbarIcon.Dispose();
        }

        private void SetupExceptionHandling()
        {
            Sentry.Init("https://0a572df482544fc19cdc855d17602fa4:012770b74f37410199e1424faf7c51d3@sentry.io/260666");

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


            // only want to report and potentially swallow errors if we're not debugging
            if (Debugger.IsAttached)
            {
                Sentry.Default.Enabled = false;
                return;
            }

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
                var evt = new SentryEvent(args.Exception);
                evt.Message = "ApplicationThreadException";
                evt.Level = ErrorLevel.Fatal;
                evt.Submit();
            };

            DispatcherUnhandledExceptionEventHandler OnApplicationDispatcherUnhandledException = (sender, args) =>
            {
                var evt = new SentryEvent(args.Exception);
                evt.Message = "DispatcherUnhandledException";
                evt.Level = ErrorLevel.Fatal;
                evt.Submit();
            };

            try
            {
                System.Windows.Forms.Application.ThreadException += OnApplicationThreadException;
            }
            catch (Exception ex)
            {
                ex.ToSentry();
            }

            try
            {
                Application.Current.DispatcherUnhandledException += OnApplicationDispatcherUnhandledException;
            }
            catch (Exception ex)
            {
                ex.ToSentry();
            }
#endif
        }
        private void SetupServiceHost()
        {
            var inf = new CommandLineProxy();
            inf.CommandLineExecutedEvent += OnCommandLineArgsReceived;
            _host = new ServiceHost(inf, new[] { new Uri("net.pipe://localhost") });

            var behaviour = _host.Description.Behaviors.Find<ServiceBehaviorAttribute>();
            behaviour.InstanceContextMode = InstanceContextMode.Single;

            _host.AddServiceEndpoint(typeof(ICommandLineProxy), new NetNamedPipeBinding(), Constants.ClowdNamedPipe);
            _host.Open();
        }
        private void SetupSettings()
        {
            GeneralSettings tmp;
            Classify.DefaultOptions = new ClassifyOptions();
            Classify.DefaultOptions.AddTypeProcessor(typeof(Color), new ClassifyColorTypeOptions());
            Classify.DefaultOptions.AddTypeSubstitution(new ClassifyColorTypeOptions());

            SettingsUtil.LoadSettings(out tmp);
            Settings = tmp;
        }
        private void SetupAccentColors()
        {
            //var scheme = Settings.ColorScheme;
            //var baseColor = Settings.AccentScheme == AccentScheme.User ? Settings.UserAccentColor : AreoColor.GetColor();
            var baseColor = Settings.AccentScheme == AccentScheme.User ? Settings.UserAccentColor : AreoColor.GetColor();


            _lightBase = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseLight.xaml", UriKind.RelativeOrAbsolute)
            };
            if (!this.Resources.MergedDictionaries.Contains(_lightBase))
                this.Resources.MergedDictionaries.Add(_lightBase);
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
            this.Resources["IdealForegroundColorBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]);
            ((Freezable)this.Resources["IdealForegroundColorBrush"]).Freeze();
            this.Resources["IdealForegroundDisabledBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]) { Opacity = 0.4 };
            ((Freezable)this.Resources["IdealForegroundDisabledBrush"]).Freeze();
            this.Resources["AccentSelectedColorBrush"] = new SolidColorBrush((Color)this.Resources["IdealForegroundColor"]);
            ((Freezable)this.Resources["AccentSelectedColorBrush"]).Freeze();
        }
        private void SetupDpiScaling()
        {
            ScreenVersusWpf.ScreenTools.InitializeDpi(ScreenVersusWpf.ScreenTools.GetSystemDpi());
        }
        private void SetupTrayIcon()
        {
            _taskbarIcon = new TaskbarIcon();
            //_taskbarIcon.TrayDrop += OnTaskbarIconDrop;
            _taskbarIcon.WndProcMessageReceived += OnWndProcMessageReceived;

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
                uploads.Click += (s, e) => UploadManager.ShowWindow();
                context.Items.Add(uploads);
            }

            context.Items.Add(new Separator());

            var colorp = new MenuItem() { Header = "Color Pic_ker" };
            colorp.Click += (s, e) => NiceDialog.ShowColorDialogAsync(null, Colors.Transparent);
            context.Items.Add(colorp);

            var screend = new MenuItem() { Header = "_Draw on Screen" };
            screend.Click += (s, e) => AntFu7.LiveDraw.LiveDrawWindow.ShowNewOrExisting();
            context.Items.Add(screend);

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
                ShowSettings();
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

#if NAPPUPDATE
        private void SetupUpdateTimer()
        {
            //// NAppUpdater uses relative paths, so the current directory must be set accordingly.
            //Environment.CurrentDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            //_updateManager = NAppUpdate.Framework.UpdateManager.Instance;
            //_updateManager.Config.UpdateExecutableName = "clowd-upd.exe";
            //_updateManager.Config.TempFolder = Path.Combine(AppDataDirectory, "update");
            //_updateManager.Config.BackupFolder = Path.Combine(AppDataDirectory, "backup");
            //_updateManager.Config.UpdateProcessName = "ClowdUpdate";
            //var source = new NAppUpdate.Framework.Sources.SimpleWebSource($"http://{ClowdServerDomain}/app_updates/feed.aspx");
            //_updateManager.UpdateSource = source;

            //_updateManager.ReinstateIfRestarted();
            //if (_updateManager.State == NAppUpdate.Framework.UpdateManager.UpdateProcessState.AfterRestart)
            //{
            //    var config = new TaskDialogInterop.TaskDialogOptions();
            //    config.Title = "Clowd";
            //    config.MainInstruction = "Updates were installed successfully.";
            //    config.CommonButtons = TaskDialogInterop.TaskDialogCommonButtons.Close;
            //    config.MainIcon = TaskDialogInterop.VistaTaskDialogIcon.Information;
            //    TaskDialogInterop.TaskDialog.Show(config);
            //}
            //_updateManager.CleanUp();

            //_updateTimer = new DispatcherTimer();
            //_updateTimer.Interval = Settings.UpdateCheckInterval;
            //_updateTimer.Tick += OnCheckForUpdates;
            //OnCheckForUpdates(null, null);
            //_updateTimer.Start();
        }
#endif

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
        public void FinishInit()
        {
            if (_initialized)
            {
                Sentry.Default.SubmitLog("FinishInit() called more than once.", ErrorLevel.Warning);
                return;
            }
            _initialized = true;
            _taskbarIcon.ToolTipText = "Clowd\nRight click me or drop something on me\nto see what I can do!";

            // because of the mouse hook in the tray drop mechanism, hitting a breakpoint will cause clowd to stop 
            // responding to message events, which will lock up the mouse cursor - so we disable it if debugging.
            //if (!System.Diagnostics.Debugger.IsAttached)
            //    _taskbarIcon.TrayDropEnabled = Settings.TrayDropEnabled;

            SetupTrayContextMenu();
            Settings.Save();
            _cmdCache = new List<string>();
            _cmdNotifyTimer = new System.Timers.Timer();
            _cmdNotifyTimer.Interval = 500;
            _cmdNotifyTimer.Elapsed += OnCommandLineBatchTimerTick;
            if (_args != null)
            {
                OnCommandLineArgsReceived(this, new CommandLineEventArgs(_args));
            }

            // this is stupid, but if we have not shown at least one window, the call to IsWindowOnCurrentVirtualDesktop() is super slow.
            // if we've created a window, this becomes fast. Don't ask me why, but this fixes it.
            var wnd = NiceDialog.ShowNewFakeOwnerWindow(false);
            wnd.Close();
        }
        public void StartCapture(ScreenRect? region = null)
        {
            if (!_initialized)
                return;

            CaptureWindow2.ShowNewCapture();
        }
        public void QuickCaptureFullScreen()
        {
            if (!_initialized)
                return;
            StartCapture(ScreenTools.VirtualScreen.Bounds);
        }
        public void QuickCaptureCurrentWindow()
        {
            if (!_initialized)
                return;
            var foreground = USER32.GetForegroundWindow();
            var bounds = USER32EX.GetTrueWindowBounds(foreground);
            StartCapture(ScreenRect.FromSystem(bounds));
        }
        public async void UploadFile(Window owner = null)
        {
            if (!_initialized)
                return;

            var result = await NiceDialog.ShowSelectFilesDialog(owner, "Select files to upload", Settings.LastUploadPath, true);
            if (result != null)
                OnFilesReceived(result);
        }
        public void Paste()
        {
            if (!_initialized)
                return;

            if (Clipboard.ContainsImage())
            {
                var img = System.Windows.Forms.Clipboard.GetImage();
                var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                UploadManager.UploadImage(ms, "png", viewName: "Pasted Image");
            }
            else if (Clipboard.ContainsText())
            {
                var ms = new MemoryStream(Clipboard.GetText().ToUtf8());
                UploadManager.UploadText(ms, "txt", viewName: "Pasted Text");
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var collection = Clipboard.GetFileDropList();
                string[] fileArray = new string[collection.Count];
                collection.CopyTo(fileArray, 0);
                OnFilesReceived(fileArray);
            }
        }

        //public void ShowHome()
        //{
        //    if (!_initialized)
        //        return;

        //    var wnd = TemplatedWindow.GetWindow(typeof(HomePage))
        //        ?? TemplatedWindow.GetWindow(typeof(SettingsPage))
        //        ?? TemplatedWindow.CreateWindow("Clowd", openSettings ? (Control)(new SettingsPage()) : new HomePage());

        //    wnd.Show();
        //    wnd.MakeForeground();
        //}

        public void ShowSettings(SettingsCategory? category = null)
        {
            if (!_initialized)
                return;

            Window wnd;
            if ((wnd = TemplatedWindow.GetWindow(typeof(SettingsPage))) != null)
            {
                wnd.MakeForeground();
            }
            else if ((wnd = TemplatedWindow.GetWindow(typeof(HomePage))) != null)
            {
                TemplatedWindow.SetContent(wnd, new SettingsPage());
                wnd.MakeForeground();
            }
            else
            {
                wnd = TemplatedWindow.CreateWindow("Clowd", new SettingsPage());
                wnd.Show();
                wnd.MakeForeground();
            }

            if (category != null)
                TemplatedWindow.GetContent<SettingsPage>(wnd).SetCurrentTab(category.Value);
        }

#if NAPPUPDATE
        private async void OnCheckForUpdates(object sender, EventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached)
            {
                return;
            }

            //await Task.Factory.FromAsync(upd.BeginCheckForUpdates, upd.EndCheckForUpdates, null);
            try
            {
                await Task.Factory.StartNew(() => _updateManager.CheckForUpdates());
            }
            catch (Exception ex)
            when (ex is WebException || (ex as AggregateException)?.InnerException is WebException)
            {
                // web exception doesnt matter here. 
                _updateManager.CleanUp();
                return;
            }

            if (_updateManager.UpdatesAvailable == 0)
            {
                _updateManager.CleanUp();
                return;
            }
            _updateTimer.Stop();

            //await Task.Factory.FromAsync(upd.BeginPrepareUpdates, upd.EndPrepareUpdates, null);
            await Task.Factory.StartNew(() => _updateManager.PrepareUpdates());

            while (Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible))
            {
#warning also should check for in-progress uploads before prompting
                await Task.Delay(10000);
            }

            var config = new TaskDialogInterop.TaskDialogOptions();
            config.Title = "Clowd";
            config.MainInstruction = "Updates are available for Clowd";
            config.Content = "Would you like to install these crucial updates now?";
            config.CommonButtons = TaskDialogInterop.TaskDialogCommonButtons.YesNo;
            config.MainIcon = TaskDialogInterop.VistaTaskDialogIcon.Shield;

            var res = TaskDialogInterop.TaskDialog.Show(config);
            if (res.Result == TaskDialogInterop.TaskDialogSimpleResult.Yes)
            {
                OnExit(null);
                _updateManager.ApplyUpdates(true, false, false);
            }
        }
#endif

        private void OnCommandLineArgsReceived(object sender, CommandLineEventArgs e)
        {
            Console.WriteLine("command line args recieved:" + String.Join(",", e.Args));

            //restart timer if it's currently running.
            _cmdNotifyTimer.Enabled = false;

            foreach (var f in e.Args)
            {
                if (File.Exists(f) || Directory.Exists(f))
                    _cmdCache.Add(f);
            }

            _cmdNotifyTimer.Enabled = true;
        }
        private void OnCommandLineBatchTimerTick(object sender, EventArgs e)
        {
            Console.WriteLine("TICK");
            // we can turn this off now and process any collected files. timer will be started again if we recieve additional cli args
            _cmdNotifyTimer.Enabled = false;

            if (_cmdCache.Count > 0)
            {
                Console.WriteLine("command line args being processed");
                var files = _cmdCache.ToArray();
                _cmdCache.Clear();

                // pass to dispatcher so UI can be displayed
                this.Dispatcher.Invoke(() =>
                {
                    OnFilesReceived(files);
                });
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
    }
}
