using Ionic.Zip;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using RT.Util.ExtensionMethods;
using Clowd.Utilities;
using NotifyIconLib;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using System.Threading;
using System.ServiceModel;
using System.Windows.Threading;

namespace Clowd
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        //public static readonly string ServerHost = System.Diagnostics.Debugger.IsAttached ? "localhost" : "clowd.ga";
        public static readonly string ServerHost = "clowd.ca";
        public static App Singleton { get; private set; }
        public AppSettings Settings { get; private set; }

        private const string NamedPipeString = "PipeClowdRunning";
        private const string MutexString = "ClowdMutex000";

        private TaskbarIcon _taskbarIcon;
        private bool _prtscrWindowOpen = false;
        private bool _initialized = false;
        private HotKey _captureHotkey;
        private System.Timers.Timer _updateTimer;
        private NAppUpdate.Framework.UpdateManager _updateManager;
        private ResourceDictionary _lightBase;
        private ResourceDictionary _darkBase;
        private Mutex _mutex;
        private ServiceHost _host;
        private string[] _args;
        private DispatcherTimer _cmdBatchTimer;
        private List<string> _cmdCache;


        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool running = false;
            try
            {
                _mutex = Mutex.OpenExisting(MutexString);
                //if we're here, clowd is running already.
                running = true;
                if (e.Args.Length > 0)
                {
                    ChannelFactory<ICommandLineProxy> pipeFactory = new ChannelFactory<ICommandLineProxy>(
                                    new NetNamedPipeBinding(),
                                    new EndpointAddress("net.pipe://localhost/" + NamedPipeString));

                    ICommandLineProxy pipeProxy = pipeFactory.CreateChannel();
                    pipeProxy.PassArgs(e.Args);
                    pipeFactory.Close();
                }
                Thread.Sleep(2000);
                Environment.Exit(0);
                return;
            }
            catch
            {
                if (running)
                    Environment.Exit(0);
                _mutex = new Mutex(true, MutexString);
                if (e.Args.Length > 0)
                {
                    _args = e.Args;
                }
            }

            Singleton = this;
            SetupServiceHost();
            // this is for testing purposes, so the localhost server has time to start.
            if (System.Diagnostics.Debugger.IsAttached)
                await Task.Delay(3000);
            SetupDpiScaling();
            SetupTrayIcon();
            SetupSettings();
            Settings.ColorScheme = ColorScheme.Light;
            SetupAccentColors();


            if (Settings.FirstRun)
            {
                // there were no settings to load, show login window.
                Settings = new AppSettings() { FirstRun = false };
                var page = new LoginPage();
                var login = TemplatedWindow.CreateWindow("CLOWD", page);
                login.Show();
            }
            else
            {
                if (Settings.Username == "anon" && String.IsNullOrEmpty(Settings.PasswordHash))
                {
                    //use clowd anonymously.
                    FinishInit();
                }
                else
                {
                    if (String.IsNullOrEmpty(Settings.Username) && String.IsNullOrEmpty(Settings.PasswordHash))
                    {
                        var page = new LoginPage();
                        var login = TemplatedWindow.CreateWindow("CLOWD", page);
                        login.Show();
                    }
                    else
                    {
                        using (var details = new Credentials(Settings.Username, Settings.PasswordHash, true))
                        {
                            var result = await UploadManager.Login(details);
                            if (result == AuthResult.Success)
                                FinishInit();
                            else
                            {
                                //show login page, wont happen if its not the first run and the settings were saved. 
                                var page = new LoginPage(result, Settings.Username);
                                var login = TemplatedWindow.CreateWindow("CLOWD", page);
                                login.Show();
                            }
                        }
                    }
                }
            }

            if (!System.Diagnostics.Debugger.IsAttached)
                SetupUpdateTimer();
        }

        private void SetupServiceHost()
        {
            var inf = new CommandLineProxy();
            inf.CommandLineExecutedEvent += OnCommandLineArgsRecieved;
            _host = new ServiceHost(inf, new[] { new Uri("net.pipe://localhost") });

            var behaviour = _host.Description.Behaviors.Find<ServiceBehaviorAttribute>();
            behaviour.InstanceContextMode = InstanceContextMode.Single;

            _host.AddServiceEndpoint(typeof(ICommandLineProxy), new NetNamedPipeBinding(), NamedPipeString);
            _host.Open();
        }
        private void SetupSettings()
        {
            AppSettings tmp;
            RT.Util.SettingsUtil.LoadSettings<AppSettings>(out tmp);
            Settings = tmp;
        }
        private void SetupAccentColors()
        {
            var scheme = Settings.ColorScheme;
            var baseColor = Settings.AccentScheme == AccentScheme.User ? Settings.UserAccentColor : AreoColor.GetColor();

            if (_lightBase == null)
            {
                _lightBase = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseLight.xaml", UriKind.RelativeOrAbsolute)
                };
            }
            if (_darkBase == null)
            {
                _darkBase = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseDark.xaml", UriKind.RelativeOrAbsolute)
                };
            }

            if (scheme == ColorScheme.Light)
            {
                //remove dark base dictionary
                if (this.Resources.MergedDictionaries.Contains(_darkBase))
                    this.Resources.MergedDictionaries.Remove(_darkBase);
                //add light base dictionary
                if (!this.Resources.MergedDictionaries.Contains(_lightBase))
                    this.Resources.MergedDictionaries.Add(_lightBase);
            }
            else if (scheme == ColorScheme.Dark)
            {
                //remove light base dictionary
                if (this.Resources.MergedDictionaries.Contains(_lightBase))
                    this.Resources.MergedDictionaries.Remove(_lightBase);
                //add dark base dictionary
                if (!this.Resources.MergedDictionaries.Contains(_darkBase))
                    this.Resources.MergedDictionaries.Add(_darkBase);
            }

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
            Interop.USER32.SetProcessDPIAware();
            IntPtr dC = Interop.USER32.GetDC(IntPtr.Zero);
            double logX = (double)Interop.Gdi32.GDI32.GetDeviceCaps(dC, Interop.Gdi32.DEVICECAP.LOGPIXELSX);
            double logY = (double)Interop.Gdi32.GDI32.GetDeviceCaps(dC, Interop.Gdi32.DEVICECAP.LOGPIXELSY);
            Interop.USER32.ReleaseDC(IntPtr.Zero, dC);
            DpiScale.ScaleUISetup(logX, logY);
        }
        private void SetupTrayIcon()
        {
            _taskbarIcon = new TaskbarIcon();
            //_taskbarIcon.IconSource = new BitmapImage(new Uri("pack://application:,,,/Images/default.ico"));
            _taskbarIcon.TrayDrop += OnTaskbarIconDrop;
            _taskbarIcon.WndProcMessageRecieved += OnWndProcMessageRecieved;

            //force the correct icon size
            System.Windows.Resources.StreamResourceInfo sri = Application.GetResourceStream(new Uri("pack://application:,,,/Images/default.ico"));
            var desiredSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
            var avaliableSizes = new[] { 64, 48, 32, 24, 20, 16 };
            var nearest = avaliableSizes.OrderBy(x => Math.Abs(x - desiredSize)).First();
            var icon = new System.Drawing.Icon(sri.Stream, new System.Drawing.Size(nearest, nearest));
            _taskbarIcon.Icon = icon;
        }
        private void SetupGlobalHotkeys()
        {
            var capture = Settings.CaptureSettings.StartCaptureShortcut;
            _captureHotkey = new HotKey(capture.Key, capture.Modifiers, (key) =>
            {
                StartCapture();
            }, false);

            if (!_captureHotkey.Register())
            {
                //hotkey was not registered, because some other application already has registered it or it is reserved.
                //TODO: Show some kind of error message here.
            }
        }
        private void SetupUpdateTimer()
        {
            _updateManager = NAppUpdate.Framework.UpdateManager.Instance;
            _updateManager.Config.UpdateExecutableName = "clowd-upd.exe";
            _updateManager.Config.UpdateProcessName = "ClowdUpdate";
            var source = new NAppUpdate.Framework.Sources.SimpleWebSource($"http://{ServerHost}/app_updates/feed.aspx");
            _updateManager.UpdateSource = source;

            _updateManager.ReinstateIfRestarted();
            _updateManager.CleanUp();

            _updateTimer = new System.Timers.Timer(Settings.UpdateCheckInterval.TotalMilliseconds);
            _updateTimer.Elapsed += OnCheckForUpdates;
            OnCheckForUpdates(null, null);
            _updateTimer.Start();
        }
        private void SetupTrayContextMenu()
        {
            ContextMenu context = new ContextMenu();
            var capture = new MenuItem() { Header = "Capture Screen" };
            capture.Click += async (s, e) =>
            {
                //wait long enough for context menu to disappear.
                await Task.Delay(500);
                StartCapture();
            };
            context.Items.Add(capture);

            var paste = new MenuItem() { Header = "Paste" };
            paste.Click += (s, e) =>
            {
                Paste();
            };
            context.Items.Add(paste);
            context.Items.Add(new Separator());

            var home = new MenuItem() { Header = "Clowd Home" };
            home.Click += (s, e) =>
            {
                var wnd = TemplatedWindow.GetWindow(typeof(HomePage));
                if (wnd == null)
                {
                    wnd = TemplatedWindow.CreateWindow("CLOWD", new HomePage());
                }
                wnd.Show();
                wnd.MakeForeground();
            };
            context.Items.Add(home);

            var exit = new MenuItem() { Header = "Exit" };
            exit.Click += (s, e) =>
            {
                if (Settings.ConfirmClose)
                {
                    var config = new TaskDialogInterop.TaskDialogOptions();
                    config.Title = "Question";
                    config.MainInstruction = "Are you sure you wish to close Clowd?";
                    config.Content = "If you close clowd, it will stop any in-progress uploads and you will be unable to upload anything new.";
                    config.VerificationText = "Don't ask me this again";
                    config.CommonButtons = TaskDialogInterop.TaskDialogCommonButtons.YesNo;
                    config.MainIcon = TaskDialogInterop.VistaTaskDialogIcon.Warning;

                    var res = TaskDialogInterop.TaskDialog.Show(config);
                    if (res.Result == TaskDialogInterop.TaskDialogSimpleResult.Yes)
                    {
                        if (res.VerificationChecked == true)
                            Settings.ConfirmClose = false;
                        Settings.Save();
                        Application.Current.Shutdown();
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

        public void FinishInit()
        {
            if (_initialized)
                return;
            _initialized = true;
            _taskbarIcon.ToolTipText = "Clowd\nClick me or drop something on me\nto see what I can do!";
            _taskbarIcon.TrayDropEnabled = true;
            SetupTrayContextMenu();
            SetupGlobalHotkeys();
            Settings.Save();
            _cmdCache = new List<string>();
            _cmdBatchTimer = new DispatcherTimer();
            _cmdBatchTimer.Interval = TimeSpan.FromSeconds(1);
            _cmdBatchTimer.Tick += OnCommandLineBatchTimerTick;
            if (_args != null)
            {
                OnCommandLineArgsRecieved(this, new CommandLineEventArgs(_args));
            }
        }

        public void StartCapture()
        {
            if (_prtscrWindowOpen)
                return;

            var wnd = new CaptureWindow();
            wnd.Closed += (s, e) =>
            {
                _prtscrWindowOpen = false;
            };
            wnd.Show();
        }
        public void Paste()
        {
            if (Clipboard.ContainsImage())
            {
                var img = System.Windows.Forms.Clipboard.GetImage();
                byte[] b;
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    using (BinaryReader br = new BinaryReader(ms))
                    {
                        b = br.ReadBytes(Convert.ToInt32(ms.Length));
                    }
                }
                UploadManager.Upload(b, "clowd-default.png");
            }
            if (Clipboard.ContainsText())
            {
                UploadManager.Upload(Clipboard.GetText().ToUtf8(), "clowd-default.txt");
            }
            if (Clipboard.ContainsFileDropList())
            {
                var collection = Clipboard.GetFileDropList();
                string[] fileArray = new string[collection.Count];
                collection.CopyTo(fileArray, 0);
                OnFilesRecieved(fileArray);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            _mutex.ReleaseMutex();
            _host.Close();
            _taskbarIcon.Dispose();
            if (_captureHotkey != null)
                _captureHotkey.Dispose();
        }
        private async void OnCheckForUpdates(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached)
            {
                return;
            }

            //await Task.Factory.FromAsync(upd.BeginCheckForUpdates, upd.EndCheckForUpdates, null);
            await Clowd.Shared.Extensions.ToTask(() => _updateManager.CheckForUpdates());

            if (_updateManager.UpdatesAvailable == 0)
            {
                _updateManager.CleanUp();
                return;
            }
            _updateTimer.Stop();

            //await Task.Factory.FromAsync(upd.BeginPrepareUpdates, upd.EndPrepareUpdates, null);
            await Clowd.Shared.Extensions.ToTask(() => _updateManager.PrepareUpdates());

            while (UploadManager.UploadsInProgress > 0 || Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible))
            {
                await Task.Delay(10000);
            }

            var config = new TaskDialogInterop.TaskDialogOptions();
            config.Title = "Updates";
            config.MainInstruction = "Updates are available for Clowd";
            config.Content = "Would you like to install these crucial updates now?"
                + Environment.NewLine + Environment.NewLine +
                "If you decline, we'll ask again in 6 hours, or after you restart Clowd.";
            config.CommonButtons = TaskDialogInterop.TaskDialogCommonButtons.YesNo;
            config.MainIcon = TaskDialogInterop.VistaTaskDialogIcon.Shield;

            var res = TaskDialogInterop.TaskDialog.Show(config);
            if (res.Result == TaskDialogInterop.TaskDialogSimpleResult.Yes)
            {
                OnExit(null);
                _updateManager.ApplyUpdates(true, false, false);
            }
        }
        private void OnCommandLineArgsRecieved(object sender, CommandLineEventArgs e)
        {
            if (_cmdBatchTimer.IsEnabled)
            {
                //restart timer.
                _cmdBatchTimer.IsEnabled = false;
            }
            _cmdCache.AddRange(e.Args);
            _cmdBatchTimer.IsEnabled = true;
        }
        private void OnCommandLineBatchTimerTick(object sender, EventArgs e)
        {
            _cmdBatchTimer.IsEnabled = false;
            if (_cmdCache.Count > 0)
            {
                OnFilesRecieved(_cmdCache.ToArray());
                _cmdCache.Clear();
            }
        }
        private void OnWndProcMessageRecieved(uint obj)
        {
            if (obj == (uint)Interop.WindowMessage.WM_DWMCOLORIZATIONCOLORCHANGED && Settings?.AccentScheme == AccentScheme.System)
            {
                SetupAccentColors();
            }
        }
        private async Task OnFilesRecieved(string[] filePaths)
        {
            string url;
            if (filePaths.Length > 1 || (filePaths.Length == 1 && Directory.Exists(filePaths[0]))
                || (filePaths.Length == 1 && Clowd.Shared.MIMEAssistant2.GetMIMEType(filePaths[0]) == null))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    var archiveName = "clowd-default.zip";
                    await Clowd.Shared.Extensions.ToTask(() =>
                    {
                        using (ZipFile zip = new ZipFile())
                        {
                            if (filePaths.Length == 1)
                                archiveName = Path.GetFileNameWithoutExtension(filePaths[0]) + ".zip";
                            foreach (var path in filePaths)
                            {
                                if (Directory.Exists(path))
                                    zip.AddDirectory(path, Path.GetFileName(path));
                                else if (File.Exists(path))
                                    zip.AddFile(path, "");
                            }
                            zip.Save(ms);
                        }
                    });
                    ms.Position = 0;
                    byte[] barr = new byte[ms.Length];
                    ms.Read(barr, 0, (int)ms.Length);
                    url = await UploadManager.Upload(barr, archiveName);
                }
            }
            else
            {
                url = await UploadManager.Upload(File.ReadAllBytes(filePaths[0]), Path.GetFileName(filePaths[0]));
            }
        }
        private void OnTaskbarIconDrop(object sender, DragEventArgs e)
        {
            var formats = e.Data.GetFormats();
            if (formats.Contains(DataFormats.FileDrop))
            {
                var data = (string[])e.Data.GetData(DataFormats.FileDrop);
                OnFilesRecieved(data);
            }
            else if (formats.Contains(DataFormats.Text))
            {
                var data = (string)e.Data.GetData(DataFormats.Text);
                UploadManager.Upload(data.ToUtf8(), "clowd-default.txt");
            }
        }
    }
}
