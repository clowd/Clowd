using Clowd.Config;
using Clowd.PlatformUtil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Video
{
    public class ObsCapturer : VideoCapturerBase
    {
        Task _setup;
        Task _status;
        WatchProcess _watch;

        private const string obsHost = "127.0.0.1";
        private const string obsPort = "21889";
        private static readonly string obsUrl = $"http://{obsHost}:{obsPort}";
        private static readonly string obsSocket = $"ws://{obsHost}:{obsPort}/volmeter";

        private CancellationTokenSource _source;
        private CancellationToken _token;
        private StringBuilder _output = new StringBuilder();

        private static ObsCapturer _instance;
        private readonly static object _lock = new object();
        private readonly IScopedLog _log;
        private readonly string _libraryPath;

        public ObsCapturer(IScopedLog log, string libraryPath)
        {
            lock (_lock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("There can only be one instance of ObsCapturer at once. Please dispose of the existing instance before creating another.");
                _instance = this;
            }

            try
            {
                if (!Directory.Exists(libraryPath))
                    throw new ArgumentException("OBS does not exist at the path: " + libraryPath);

                if (!File.Exists(Path.Combine(libraryPath, "obs-express.exe")))
                    throw new ArgumentException("OBS does not exist at the path: " + libraryPath);

                if (!File.Exists(Path.Combine(libraryPath, "lib", "obs64.exe")))
                    throw new ArgumentException("OBS does not exist at the path: " + libraryPath);
            }
            catch
            {
                // if obs does not exist, and we're debugging, lets search for a nearby submodule or appdata obs
                var submoduledir = Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\modules\\obs-express\\bin");
                var appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd");
                string obspath = null;

                if (Debugger.IsAttached && File.Exists(Path.Combine(submoduledir, "obs-express.exe")))
                {
                    libraryPath = submoduledir;
                }
                else if (Debugger.IsAttached && Directory.Exists(appdata) &&
                    ((obspath = Directory.EnumerateFiles(appdata, "obs-express.exe", SearchOption.AllDirectories).FirstOrDefault()) != null))
                {
                    libraryPath = Path.GetDirectoryName(obspath);
                }
                else
                {
                    throw;
                }
            }

            _source = new CancellationTokenSource();
            _token = _source.Token;
            _log = log;
            _libraryPath = libraryPath;
            _setup = Task.Run(Initialize);
        }

        public async override Task Initialize()
        {
            using (var scoped = _log.CreateProfiledScope("InitOBS"))
            {
                List<ProcessStartInfo> psis = new List<ProcessStartInfo>();
                try
                {
                    var pjsonPath = Path.Combine(_libraryPath, "package.json");
                    var pjson = JsonConvert.DeserializeObject<PJsonVersion>(File.ReadAllText(pjsonPath));

                    if (String.IsNullOrWhiteSpace(pjson.osnVersion))
                        throw new Exception("osnVersion null or empty");

                    string pipeName = $"clowd-{Guid.NewGuid()}";

                    var obs64 = new ProcessStartInfo()
                    {
                        FileName = Path.Combine(_libraryPath, "lib", "obs64.exe"),
                        Arguments = $"{pipeName} {pjson.osnVersion}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(_libraryPath, "lib"),
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };

                    var obsexpress = new ProcessStartInfo()
                    {
                        FileName = Path.Combine(_libraryPath, "obs-express.exe"),
                        Arguments = $"-c {pipeName}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(_libraryPath, "lib"),
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };

                    psis.Add(obs64);
                    psis.Add(obsexpress);
                }
                catch (Exception ex)
                {
                    scoped.Error("Unable to parse osn version from package.json. Falling back to legacy OBS hosting", ex);
                    var obsexpress = new ProcessStartInfo()
                    {
                        FileName = Path.Combine(_libraryPath, "obs-express.exe"),
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(_libraryPath, "lib"),
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    psis.Add(obsexpress);
                }

                scoped.Info("Starting obs child processes");

                _watch = WatchProcess.StartAndWatch(psis.ToArray());
                _watch.OutputReceived += (s, e) =>
                {
                    string msg = $"{e.ProcessName}: " + e.Data;
                    _log.Debug(msg);
                    _output.AppendLine($"[{DateTime.Now.ToShortTimeString()}]" + msg);
                };

                scoped.Info("Running background tasks");

                string logDir = Path.Combine(_libraryPath, "obs-data", "node-obs", "logs");
                TaskCompletionSource<bool> tsc = new TaskCompletionSource<bool>();
                _status = Task.Run(async () =>
                {
                    using (var client = new ClowdHttpClient())
                    {
                        int errorCount = 0;
                        while (true)
                        {
                            if (_token.IsCancellationRequested) return;
                            await Task.Delay(1000);
                            if (_token.IsCancellationRequested) return;

                            try
                            {
                                var status = await client.GetJsonAsync<ObsStatusResponse>(ObsUri("/status"));

                                if (!tsc.Task.IsCompleted && status.initialized)
                                    tsc.SetResult(true);

                                if (status.recording)
                                    OnStatusRecieved((int)status.statistics.frameRate, (int)status.statistics.numberDroppedFrames, TimeSpan.FromMilliseconds(status.recordingTime));

                                errorCount = 0;
                            }
                            catch (Exception ex)
                            {
                                errorCount++;
                                if (errorCount % 10 == 0)
                                    _log.Error("STATUS CHECK FAILING - Count: " + errorCount, ex);
                            }
                        }
                    }
                });

                scoped.Info("Waiting for initialized http response");

                await tsc.Task.WithTimeout(10000);

                BusyStatus = null;
                scoped.Info("Done/Ready");
            }
        }

        public override async Task<string> StartAsync(ScreenRect captureRect, SettingsVideo settings)
        {
            if (!Directory.Exists(settings.OutputDirectory))
                throw new ArgumentNullException($"{nameof(SettingsVideo)}.{nameof(SettingsVideo.OutputDirectory)} must be non null and point to an existing directory.");

            using (var scoped = _log.CreateProfiledScope("OBSStart"))
            {
                var dir = Path.GetFullPath(settings.OutputDirectory);

                await _setup;
                using (var client = new ClowdHttpClient())
                {
                    var req = new ObsStartRequest
                    {
                        fps = settings.Fps,
                        captureRegion = captureRect,
                        cq = (int)settings.Quality,
                        hardwareAccelerated = settings.HardwareAccelerated,
                        performanceMode = settings.Performance.ToString(),
                        subsamplingMode = settings.SubsamplingMode.ToString(),
                        outputDirectory = dir,
                        maxOutputHeight = settings.MaxResolutionWidth,
                        maxOutputWidth = settings.MaxResolutionWidth,
                        trackMouseClicks = settings.TrackMouseClicks,
                    };

                    if (settings.CaptureMicrophone && settings.CaptureMicrophoneDevice != null)
                        req.microphones = new string[] { settings.CaptureMicrophoneDevice.DeviceId };
                    else
                        req.microphones = new string[0];

                    if (settings.CaptureSpeaker && settings.CaptureSpeakerDevice != null)
                        req.speakers = new string[] { settings.CaptureSpeakerDevice.DeviceId };
                    else
                        req.speakers = new string[0];

                    var videoPathTask = WatchForNewFile(dir, "mkv");

                    scoped.Info("Start recording...");
                    var resp = await client.PostJsonAsync<ObsStartRequest, ObsResponse>(ObsUri("/recording/start"), req);
                    if (resp.status != "ok")
                        throw new Exception("A capture error occurred: " + resp.message);

                    if (await Task.WhenAny(videoPathTask, Task.Delay(10000)) == videoPathTask)
                    {
                        return videoPathTask.Result;
                    }
                    else
                    {
                        return null; // timeout
                    }
                }
            }
        }

        public override IAudioLevelListener CreateListener(IAudioDevice device)
        {
            _setup.Wait();

            return new ObsAudioListener(device);
        }

        public Task<string> WatchForNewFile(string directory, string extension)
        {
            TaskCompletionSource<string> tsc = new TaskCompletionSource<string>();

            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.Path = directory;
            watcher.NotifyFilter = (NotifyFilters)0b1111111; // NotifyFilters.CreationTime | NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.;
            watcher.Filter = "*.*";// + extension;
            watcher.Created += new FileSystemEventHandler((s, e) =>
            {
                if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    watcher.Dispose();
                    tsc.SetResult(e.FullPath);
                }
            });
            watcher.EnableRaisingEvents = true;

            _token.Register(() =>
            {
                watcher.Dispose();
                if (!tsc.Task.IsCompleted)
                    tsc.SetException(new Exception("The file watch task was cancelled"));
            });

            return tsc.Task;
        }

        private Uri ObsUri(string path) => new Uri(obsUrl.TrimEnd('/') + "/" + path.TrimStart('/'));

        public override async Task StopAsync()
        {
            using (var client = new ClowdHttpClient())
            {
                await client.PostNothingAsync<ObsResponse>(ObsUri("/recording/stop"));
            }
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                _instance = null;
                _log.Info("Disposing ObsCapturer Instance");
                _source.Cancel();
                _watch.ForceExit();
            }
        }

        public override void WriteLogToFile(string fileName)
        {
            File.WriteAllText(fileName, _output.ToString());
        }

        private class ObsAudioListener : IAudioLevelListener
        {
            public IAudioDevice Device { get; }
            public ClientWebSocket WebSocket { get; }
            public CancellationTokenSource TokenSource { get; }

            private readonly object _lock = new object();

            private double _peak;

            public ObsAudioListener(IAudioDevice device)
            {
                Device = device;
                TokenSource = new CancellationTokenSource();
                WebSocket = new ClientWebSocket();
                WebSocket.ConnectAsync(new Uri(obsSocket + $"?device_type={device.DeviceType}&device_id={device.DeviceId}"),
                    new CancellationTokenSource(10000).Token).ConfigureAwait(false).GetAwaiter().GetResult();

                Task.Factory.StartNew(ThreadProc, TokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            private void ThreadProc()
            {
                try
                {
                    ReceiveLoop();
                }
                finally
                {
                    try
                    {
                        WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                        WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                    catch { }
                    WebSocket.Dispose();
                }
            }

            private void ReceiveLoop()
            {
                var buffer = new byte[8192];
                StringBuilder sb = new StringBuilder();
                WebSocketReceiveResult receiveResult;

                while (!TokenSource.IsCancellationRequested)
                {
                    do
                    {
                        receiveResult = WebSocket.ReceiveAsync(buffer, TokenSource.Token)
                            .ConfigureAwait(false).GetAwaiter().GetResult();

                        if (receiveResult.MessageType != WebSocketMessageType.Close)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, receiveResult.Count));
                    }
                    while (!receiveResult.EndOfMessage);

                    ReceiveMessage(sb.ToString());
                    sb.Clear();
                }
            }

            private void ReceiveMessage(string message)
            {
                var obj = JsonConvert.DeserializeObject<ObsVolmeter>(message);
                lock (_lock)
                {
                    _peak = obj.peak;
                }
            }

            public void Dispose()
            {
                TokenSource.Cancel();
            }

            public double GetPeakLevel()
            {
                lock (_lock)
                {
                    return _peak;
                }
            }
        }

#pragma warning disable CS0649 // is never assigned to

        private class PJsonVersion
        {
            public string version;
            public string osnVersion;
        }

        private class ObsResponse
        {
            public string status;
            public string message;
        }

        private class ObsVolmeter : ObsResponse
        {
            public double peak;
            public double magnitude;
        }

        private class ObsStartRequest
        {
            public ObsRect captureRegion;
            public int maxOutputWidth;
            public int maxOutputHeight;
            public string[] speakers;
            public string[] microphones;
            public int fps;
            public int cq;
            public bool hardwareAccelerated;
            public string outputDirectory;
            public string performanceMode;
            public string subsamplingMode;
            public bool trackMouseClicks;
        }

        private class ObsSize
        {
            public int width;
            public int height;
        }

        private class ObsRect : ObsSize
        {
            public int x;
            public int y;
            //public static implicit operator ObsRect(ScreenRect rect) => new ObsRect { x = rect.Left, y = rect.Top, width = rect.Width, height = rect.Height };
            public static implicit operator ObsRect(Rectangle rect) => new ObsRect { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
            public static implicit operator ObsRect(ScreenRect rect) => new ObsRect { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
        }

        private class Statistics
        {
            public double CPU;
            public double numberDroppedFrames;
            public double percentageDroppedFrames;
            public double streamingBandwidth;
            public double streamingDataOutput;
            public double recordingBandwidth;
            public double recordingDataOutput;
            public double frameRate;
            public double averageTimeToRenderFrame;
            public double memoryUsage;
            public string diskSpaceAvailable;
        }

        private class ObsStatusResponse
        {
            public bool initialized;
            public bool recording;
            public uint recordingTime;
            public Statistics statistics;
        }
    }
}
