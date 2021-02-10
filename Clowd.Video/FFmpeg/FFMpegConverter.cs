using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web;

namespace Clowd.Video.FFmpeg
{
    /// <summary>Video converter component (wrapper to FFMpeg process)</summary>
    public class FFMpegConverter
    {
        private static object globalObj = new object();
        private Process FFMpegProcess;

        /// <summary>Gets or sets path where FFMpeg tool is located</summary>
        /// <remarks>
        /// By default this property points to the folder where application assemblies are located.
        /// If WkHtmlToPdf tool files are not present PdfConverter expands them from DLL resources.
        /// </remarks>
        public string FFMpegToolPath { get; set; }

        /// <summary>
        /// Gets or sets FFMpeg tool EXE file name ('ffmpeg.exe' by default)
        /// </summary>
        public string FFMpegExeName { get; set; }

        /// <summary>
        /// Gets or sets maximum execution time for conversion process (null is by default - means no timeout)
        /// </summary>
        public TimeSpan? ExecutionTimeout { get; set; }

        /// <summary>Occurs when log line is received from FFMpeg process</summary>
        public event EventHandler<FFMpegLogEventArgs> LogReceived;

        /// <summary>
        /// Gets or sets FFMpeg process priority (Normal by default)
        /// </summary>
        public ProcessPriorityClass FFMpegProcessPriority { get; set; }

        /// <summary>
        /// Gets or sets ffmpeg loglevel option (by default is "info").
        /// </summary>
        public string LogLevel { get; set; }

        /// <summary>
        /// Initializes a new instance of the FFMpegConverter class.
        /// </summary>
        /// <remarks>
        /// FFMpegConverter is NOT thread-safe. Separate instance should be used for each thread.
        /// </remarks>
        public FFMpegConverter()
        {
            this.FFMpegProcessPriority = ProcessPriorityClass.Normal;
            this.LogLevel = "info";
            this.FFMpegToolPath = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(this.FFMpegToolPath))
                this.FFMpegToolPath = Path.GetDirectoryName(typeof(FFMpegConverter).Assembly.Location);
            this.FFMpegExeName = "ffmpeg.exe";
        }

        internal string GetFFMpegExePath()
        {
            return Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
        }

        protected void WaitFFMpegProcessForExit()
        {
            if (this.FFMpegProcess == null)
                throw new FFMpegException(-1, "FFMpeg process was aborted");
            if (!this.FFMpegProcess.HasExited && !this.FFMpegProcess.WaitForExit(this.ExecutionTimeout.HasValue ? (int)this.ExecutionTimeout.Value.TotalMilliseconds : int.MaxValue))
            {
                this.EnsureFFMpegProcessStopped();
                throw new FFMpegException(-2, string.Format("FFMpeg process exceeded execution timeout ({0}) and was aborted", (object)this.ExecutionTimeout));
            }
        }

        protected void EnsureFFMpegProcessStopped()
        {
            if (this.FFMpegProcess == null)
                return;
            if (this.FFMpegProcess.HasExited)
                return;
            try
            {
                this.FFMpegProcess.Kill();
                this.FFMpegProcess = (Process)null;
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// Invoke FFMpeg process with custom command line arguments
        /// </summary>
        /// <param name="ffmpegArgs">string with arguments</param>
        public void Invoke(string ffmpegArgs)
        {
            this.EnsureFFMpegLibs();
            try
            {
                string ffMpegExePath = this.GetFFMpegExePath();
                if (!File.Exists(ffMpegExePath))
                    throw new FileNotFoundException("Cannot find ffmpeg tool: " + ffMpegExePath);

                this.FFMpegLogHandler($"> {ffMpegExePath} {ffmpegArgs}");
                this.FFMpegLogHandler($">");

                ProcessStartInfo startInfo = new ProcessStartInfo(ffMpegExePath, ffmpegArgs);
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow = true;
                startInfo.UseShellExecute = false;
                startInfo.WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath);
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = true;

                if (this.FFMpegProcess != null)
                    throw new InvalidOperationException("FFMpeg process is already started");
                this.FFMpegProcess = Process.Start(startInfo);

                // redirect / handle FFMPEG logs
                if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
                    this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
                string lastErrorLine = string.Empty;
                this.FFMpegProcess.ErrorDataReceived += (DataReceivedEventHandler)((o, args) =>
                {
                    if (args.Data == null)
                        return;
                    lastErrorLine = args.Data;
                    this.FFMpegLogHandler(args.Data);
                });
                this.FFMpegProcess.BeginErrorReadLine();

                // start watcher process.. will kill ffmpeg if this process exists without closing it first
                var watcherProcess = WatchProcess.Watch(this.FFMpegProcess.Id);
                watcherProcess.OutputDataReceived += (s, e) =>
                {
                    if (e != null && e.Data != null)
                        this.FFMpegLogHandler(e.Data);
                };
                watcherProcess.BeginOutputReadLine();

                // wait for ffmpeg to exit and process exit code
                this.WaitFFMpegProcessForExit();

                if (this.FFMpegProcess.ExitCode != 0)
                    throw new FFMpegException(this.FFMpegProcess.ExitCode, lastErrorLine);
                this.FFMpegProcess.Close();
                this.FFMpegProcess = (Process)null;
            }
            finally
            {
                this.EnsureFFMpegProcessStopped();
            }
        }

        internal void FFMpegLogHandler(string line)
        {
            if (this.LogReceived == null)
                return;
            this.LogReceived((object)this, new FFMpegLogEventArgs(line));
        }

        /// <summary>
        /// Extracts ffmpeg binaries (if needed) to the location specified by FFMpegToolPath />.
        /// </summary>
        /// <remarks><para>If missed ffmpeg is extracted automatically before starting conversion process.
        /// In some cases it is better to do that explicetily on the application start by calling ExtractFFmpeg method.</para>
        /// <para>This method is not available in LT version (without embedded ffmpeg binaries).</para></remarks>
        public void ExtractFFmpeg()
        {
            this.EnsureFFMpegLibs();
        }

        private void EnsureFFMpegLibs()
        {
            if (!File.Exists(GetFFMpegExePath()))
            {
                lock (FFMpegConverter.globalObj)
                {
                    var bitness = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                    Assembly executingAssembly = Assembly.GetExecutingAssembly();
                    string[] manifestResourceNames = executingAssembly.GetManifestResourceNames();
                    string str = "Clowd.Video.Embed.";
                    foreach (string name in manifestResourceNames)
                    {
                        if (name.StartsWith(str) && name.Contains(bitness))
                        {
                            string path = Path.Combine(this.FFMpegToolPath, Path.GetFileNameWithoutExtension(name.Substring(str.Length)));

                            if (!File.Exists(GetFFMpegExePath()))
                                FFMpegExeName = Path.GetFileName(path);

                            if (File.Exists(path))
                            {
                                if (File.GetLastWriteTime(path) > File.GetLastWriteTime(executingAssembly.Location))
                                    continue;
                            }
                            using (GZipStream gzipStream = new GZipStream(executingAssembly.GetManifestResourceStream(name), CompressionMode.Decompress, false))
                            {
                                using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    byte[] buffer = new byte[65536];
                                    int count;
                                    while ((count = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
                                        fileStream.Write(buffer, 0, count);
                                }
                            }
                        }
                    }
                }
            }

            var manifestPath = GetFFMpegExePath() + ".manifest";
            if (!File.Exists(manifestPath))
            {
                // we need to make ffmpeg per-monitor dpi aware so gdigrab / dshow filters can get correct screen coordinates.
                // https://docs.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
                File.WriteAllText(manifestPath, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\" xmlns:asmv3=\"urn:schemas-microsoft-com:asm.v3\">\r\n<asmv3:application>\r\n<asmv3:windowsSettings>\r\n<dpiAware xmlns=\"http://schemas.microsoft.com/SMI/2005/WindowsSettings\">true</dpiAware>\r\n<dpiAwareness xmlns=\"http://schemas.microsoft.com/SMI/2016/WindowsSettings\">PerMonitorV2</dpiAwareness>\r\n</asmv3:windowsSettings>\r\n</asmv3:application>\r\n</assembly>");
            }
        }

        /// <summary>
        /// Abort FFMpeg process started by ConvertMedia or ConcatMedia methods
        /// </summary>
        /// <remarks>This method IMMEDIATELY stops FFMpeg by killing the process. Resulting file may be inconsistent.</remarks>
        public void Abort()
        {
            this.EnsureFFMpegProcessStopped();
        }

        /// <summary>
        /// Stop FFMpeg process "softly" by sending 'q' command to FFMpeg console.
        /// This method doesn't stop FFMpeg process immediately and may take some time.
        /// </summary>
        /// <returns>true if 'q' command was sent sucessfully and FFPeg process has exited. If this method returns false FFMpeg process should be stopped with Abort method.</returns>
        public bool Stop()
        {
            if (this.FFMpegProcess == null || this.FFMpegProcess.HasExited || !this.FFMpegProcess.StartInfo.RedirectStandardInput)
                return false;
            this.FFMpegProcess.StandardInput.WriteLine("q\n");
            this.FFMpegProcess.StandardInput.Close();
            this.WaitFFMpegProcessForExit();
            return true;
        }
    }
}
