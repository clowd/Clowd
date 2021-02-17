using ScreenVersusWpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
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

        string logPath;
        string obsUrl = "http://127.0.0.1:21889";

        private CancellationTokenSource _source;
        private CancellationToken _token;

        private static ObsCapturer _instance;
        private readonly static object _lock = new object();
        private readonly IScopedLog _log;
        private readonly string _libraryPath;

        internal ObsCapturer(IScopedLog log, string libraryPath)
        {
            lock (_lock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("There can only be one instance of ObsCapturer at once. Please dispose of the existing instance before creating another.");
                _instance = this;
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
                var obs64 = new ProcessStartInfo()
                {
                    FileName = Path.Combine(_libraryPath, "lib", "obs64.exe"),
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
                    UseShellExecute = false,
                    WorkingDirectory = Path.Combine(_libraryPath, "lib"),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                scoped.Info("Starting obs child processes");

                _watch = WatchProcess.StartAndWatch(obs64, obsexpress);
                _watch.OutputReceived += (s, e) =>
                {
                    using (var obsWatch = _log.CreateScope("OBSWatch"))
                        obsWatch.Info($"{e.Process.ProcessName}: " + e.Data);
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

                            // watch for new log file to be created and capture it
                            if ((logPath == null || !File.Exists(logPath)) && Directory.Exists(logDir))
                            {
                                logPath = Directory.EnumerateFiles(logDir).OrderByDescending(l => l).FirstOrDefault();
                                if (File.Exists(logPath))
                                {
                                    _log.Info("OBS log file captured: " + logPath);
                                }
                            }

                            try
                            {
                                var status = await client.GetJsonAsync<ObsStatusResponse>(ObsUri("/status"));

                                if (!tsc.Task.IsCompleted && status.initialized)
                                    tsc.SetResult(true);

                                if (status.recording)
                                    OnStatusRecieved((int)status.statistics.frameRate, (int)status.statistics.numberDroppedFrames, TimeSpan.Zero);

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

        public override async Task<string> StartAsync(ScreenRect captureRect, VideoCapturerSettings settings)
        {
            if (!Directory.Exists(settings.OutputDirectory))
                throw new ArgumentNullException($"{nameof(VideoCapturerSettings)}.{nameof(VideoCapturerSettings.OutputDirectory)} must be non null and point to an existing directory.");

            using (var scoped = _log.CreateProfiledScope("OBSStart"))
            {
                var dir = Path.GetFullPath(settings.OutputDirectory);

                await _setup;
                using (var client = new ClowdHttpClient())
                {
                    var req = new ObsStartRequest
                    {
                        fps = settings.Fps,
                        captureRegion = captureRect.ToSystem(),
                        cq = (int)settings.Quality,
                        hardwareAccelerated = settings.HardwareAccelerated,
                        performanceMode = settings.Performance.ToString(),
                        subsamplingMode = settings.SubsamplingMode.ToString(),
                        outputDirectory = dir,
                        maxOutputSize = new ObsSize
                        {
                            height = settings.MaxResolutionHeight,
                            width = settings.MaxResolutionWidth,
                        },
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
                _source.Cancel();
                _watch.ForceExit();
                _instance = null;
            }
        }

        public override void WriteLogToFile(string fileName)
        {
            if (File.Exists(logPath))
                File.Copy(logPath, fileName);
        }

        private class ObsResponse
        {
            public string status;
            public string message;
        }

        private class ObsStartRequest
        {
            public ObsRect captureRegion;
            public ObsSize maxOutputSize;
            public string[] speakers;
            public string[] microphones;
            public int fps;
            public int cq;
            public bool hardwareAccelerated;
            public string outputDirectory;
            public string performanceMode;
            public string subsamplingMode;
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
            public Statistics statistics;
        }
    }
}
