using Clowd.Config;
using Clowd.PlatformUtil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using RT.Util.ExtensionMethods;

namespace Clowd.Video
{
    public class ObsCapturer : VideoCapturerBase
    {
        public static string LibraryDirPath { get; } = Path.Combine(AppContext.BaseDirectory, "obs-express");
        public static string BinDirPath => Path.Combine(LibraryDirPath, "bin", "64bit");
        public static string ObsExpressExePath => Path.Combine(BinDirPath, "obs-express.exe");
        public static string FFmpegExePath => Path.Combine(BinDirPath, "ffmpeg.exe");

        private static ObsCapturer _instance;
        private static readonly object _lock = new object();
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        private WatchProcess _watch;
        private StringBuilder _output = new();
        private string _filePath;
        private TaskCompletionSource<bool> _signalInit = new();
        private TaskCompletionSource<string> _signalStart = new();
        private TaskCompletionSource<bool> _signalStop = new();

        static ObsCapturer()
        {
#if DEBUG
            // if obs is missing, try to find obs in build cache, only if we're debugging.
            if (!Directory.Exists(LibraryDirPath))
            {
                DirectoryInfo di = new DirectoryInfo(AppContext.BaseDirectory);
                do
                {
                    var dir = Path.Combine(di.FullName, ".cache", "obs-express");
                    var exe = Path.Combine(dir, "bin", "64bit", "obs-express.exe");
                    if (File.Exists(exe))
                    {
                        LibraryDirPath = dir;
                        break;
                    }

                    di = di.Parent;
                } while (di != null);
            }
#endif
        }

        public ObsCapturer()
        {
            lock (_lock)
            {
                if (_instance != null)
                    throw new InvalidOperationException(
                        "There can only be one instance of ObsCapturer at once. Please dispose of the existing instance before creating another.");
                _instance = this;
            }

#if DEBUG
            // friendly developer message for missing OBS!
            if (!Directory.Exists(LibraryDirPath))
                throw new ArgumentException("OBS could not be found. This is a development build, so you (the developer) probably forgot to run the 'build.cmd' " +
                                            "script at the root of this project to download a pre-compiled version of OBS. Run that script and try again!");
#endif

            if (!Directory.Exists(LibraryDirPath))
                throw new ArgumentException("OBS does not exist or is corrupt at the path: " + LibraryDirPath);

            if (!File.Exists(ObsExpressExePath))
                throw new ArgumentException("OBS does not exist or is corrupt at the path: " + ObsExpressExePath);
        }

        public override Task Initialize(ScreenRect captureRect, SettingsVideo settings)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(settings.OutputDirectory))
                    throw new Exception("OutputDirectory must not be null");

                if (!Directory.Exists(settings.OutputDirectory))
                    Directory.CreateDirectory(settings.OutputDirectory);

                _filePath = Path.Combine(settings.OutputDirectory, PathConstants.GetFreePatternFileName(settings.OutputDirectory, settings.FilenamePattern)) + ".mp4";

                List<string> arguments = new()
                {
                    "--captureRegion", $"{captureRect.X},{captureRect.Y},{captureRect.Width},{captureRect.Height}",
                    "--fps", settings.Fps.ToString(),
                    "--crf", ((int)settings.Quality).ToString(),
                    "--maxOutputWidth", settings.MaxResolutionWidth.ToString(),
                    "--maxOutputHeight", settings.MaxResolutionHeight.ToString(),
                    "--pause",
                    "--output", _filePath,
                };

                if (settings.ShowClickAnimation)
                {
                    arguments.Add("--trackerEnabled");
                    arguments.Add("--trackerColor");
                    arguments.Add($"{settings.ClickAnimationColor.R},{settings.ClickAnimationColor.G},{settings.ClickAnimationColor.B}");
                }

                if (settings.HardwareAccelerated)
                {
                    arguments.Add("--hwAccel");
                }

                if (!settings.ShowMouseCursor)
                {
                    arguments.Add("--noCursor");
                }

                if (settings.CaptureMicrophone && settings.CaptureMicrophoneDevice?.DeviceId != null)
                {
                    arguments.Add("--microphones");
                    arguments.Add(settings.CaptureMicrophoneDevice?.DeviceId);
                }

                if (settings.CaptureSpeaker && settings.CaptureSpeakerDevice?.DeviceId != null)
                {
                    arguments.Add("--speakers");
                    arguments.Add(settings.CaptureSpeakerDevice?.DeviceId);
                }

                var obsexpress = new ProcessStartInfo()
                {
                    FileName = ObsExpressExePath,
                    UseShellExecute = false,
                    WorkingDirectory = BinDirPath,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };

                foreach (var a in arguments)
                    obsexpress.ArgumentList.Add(a);

                _log.Info("Starting obs child processes");

                _watch = WatchProcess.StartAndWatch(obsexpress);
                _watch.OutputReceived += OutputReceived;
                _watch.ProcessExited += ProcessExited;
            }
            catch (Exception ex)
            {
                _signalInit.SetException(ex);
                _log.Error(ex);
            }

            return _signalInit.Task;
        }

        private void ProcessExited(object sender, WatchProcessExitedEventArgs e)
        {
            if (!e.ProcessName.EqualsIgnoreCase("obs-express"))
                return;

            lock (_signalStop)
            {
                if (_signalStop.Task.IsCompleted)
                    return;

                // if the process exits before the stopped_recording event there is something wrong.
                OnCriticalError("The recording process has exited unexpectedly.");
                _signalStop.SetResult(false);
            }
        }

        private void OutputReceived(object sender, WatchLogEventArgs e)
        {
            if (!e.ProcessName.EqualsIgnoreCase("obs-express"))
                return;

            try
            {
                bool shouldLog = true;
                var data = e.Data.Trim();
                if (data.StartsWith("{") && data.EndsWith("}"))
                {
                    var jobj = JObject.Parse(data);
                    var msg_type = jobj["type"]?.ToString();
                    switch (msg_type)
                    {
                        case "status":
                            shouldLog = false;
                            int fps = Convert.ToInt32(jobj["fps"]);
                            int dropped = Convert.ToInt32(jobj["dropped"]);
                            long timsMs = Convert.ToInt32(jobj["timeMs"]);
                            OnStatusRecieved(fps, dropped, TimeSpan.FromMilliseconds(timsMs));
                            break;
                        case "initialized":
                            _signalInit.SetResult(true);
                            break;
                        case "started_recording":
                            _signalStart.SetResult(_filePath);
                            break;
                        case "stopped_recording":
                            lock (_signalStop)
                                _signalStop.SetResult(true);

                            int code = Convert.ToInt32(jobj["code"]);
                            string message = jobj["message"]?.ToString() ?? "";
                            string error = jobj["error"]?.ToString() ?? "";
                            if (code != 0)
                                OnCriticalError(message + Environment.NewLine + error);
                            break;
                    }
                }

                if (shouldLog)
                {
                    string msg = $"{e.ProcessName}: " + data;
                    _log.Debug(msg);
                    _output.AppendLine($"[{DateTime.Now.ToShortTimeString()}]" + msg);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "error parsing video output");
            }
        }

        public override async Task<string> StartAsync()
        {
            await _signalInit.Task;
            _watch.WriteToStdIn("");
            return await _signalStart.Task;
        }

        public override Task StopAsync()
        {
            _watch.WriteToStdIn("q");
            return _signalStop.Task;
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                _instance = null;
                _log.Info("Disposing ObsCapturer Instance");
                try { _watch?.WriteToStdIn("q"); }
                catch {; }

                _watch?.WaitTimeoutThenForceExit(5000);
            }
        }

        public override void WriteLogToFile(string fileName)
        {
            File.WriteAllText(fileName, _output.ToString());
        }
    }
}
