// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.FFMpegConverter
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web;

namespace NReco.VideoConverter
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

        /// <summary>
        /// Occurs when FFMpeg outputs media info (total duration, convert progress)
        /// </summary>
        public event EventHandler<ConvertProgressEventArgs> ConvertProgress;

        /// <summary>Occurs when log line is received from FFMpeg process</summary>
        public event EventHandler<FFMpegLogEventArgs> LogReceived;

        /// <summary>
        /// Gets or sets FFMpeg process priority (Normal by default)
        /// </summary>
        public ProcessPriorityClass FFMpegProcessPriority { get; set; }

        /// <summary>
        /// Gets or sets user credential used for starting FFMpeg process.
        /// </summary>
        /// <remarks>By default this property is null and FFMpeg process uses credential of parent process (application pool in case of ASP.NET).</remarks>
        public FFMpegUserCredential FFMpegProcessUser { get; set; }

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
            if (HttpContext.Current != null)
                this.FFMpegToolPath = HttpRuntime.AppDomainAppPath + "bin";
            if (string.IsNullOrEmpty(this.FFMpegToolPath))
                this.FFMpegToolPath = Path.GetDirectoryName(typeof(FFMpegConverter).Assembly.Location);
            this.FFMpegExeName = "ffmpeg.exe";
        }

        private void CopyStream(Stream inputStream, Stream outputStream, int bufSize)
        {
            byte[] buffer = new byte[bufSize];
            int count;
            while ((count = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                outputStream.Write(buffer, 0, count);
        }

        /// <summary>
        /// Converts media represented by local file and writes result to specified local file
        /// </summary>
        /// <param name="inputFile">local path to input media file</param>
        /// <param name="outputFile">local path to ouput media file</param>
        /// <param name="outputFormat">desired output format (like "mp4" or "flv")</param>
        public void ConvertMedia(string inputFile, string outputFile, string outputFormat)
        {
            this.ConvertMedia(inputFile, (string)null, outputFile, outputFormat, (ConvertSettings)null);
        }

        /// <summary>
        /// Converts media represented by local file and writes result to specified local file with specified settings.
        /// </summary>
        /// <param name="inputFile">local path to input media file</param>
        /// <param name="inputFormat">input format (null for automatic format suggestion)</param>
        /// <param name="outputFile">local path to output media file</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">explicit convert settings</param>
        public void ConvertMedia(
          string inputFile,
          string inputFormat,
          string outputFile,
          string outputFormat,
          ConvertSettings settings)
        {
            if (inputFile == null)
                throw new ArgumentNullException(nameof(inputFile));
            if (outputFile == null)
                throw new ArgumentNullException(nameof(outputFile));
            if (File.Exists(inputFile) && string.IsNullOrEmpty(Path.GetExtension(inputFile)) && inputFormat == null)
                throw new Exception("Input format is required for file without extension");
            if (string.IsNullOrEmpty(Path.GetExtension(outputFile)) && outputFormat == null)
                throw new Exception("Output format is required for file without extension");
            this.ConvertMedia(new Media()
            {
                Filename = inputFile,
                Format = inputFormat
            }, new Media()
            {
                Filename = outputFile,
                Format = outputFormat
            }, settings ?? new ConvertSettings());
        }

        /// <summary>
        /// Converts media represented by local file and writes result to specified stream
        /// </summary>
        /// <param name="inputFile">local path to input media file</param>
        /// <param name="outputStream">output stream</param>
        /// <param name="outputFormat">output media format</param>
        public void ConvertMedia(string inputFile, Stream outputStream, string outputFormat)
        {
            this.ConvertMedia(inputFile, (string)null, outputStream, outputFormat, (ConvertSettings)null);
        }

        /// <summary>
        /// Converts media represented by local file and writes result to specified stream with specified convert settings.
        /// </summary>
        /// <param name="inputFile">local path to input media file</param>
        /// <param name="inputFormat">input format (null for automatic format suggestion)</param>
        /// <param name="outputStream">output stream</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">convert settings</param>
        public void ConvertMedia(
          string inputFile,
          string inputFormat,
          Stream outputStream,
          string outputFormat,
          ConvertSettings settings)
        {
            if (inputFile == null)
                throw new ArgumentNullException(nameof(inputFile));
            if (File.Exists(inputFile) && string.IsNullOrEmpty(Path.GetExtension(inputFile)) && inputFormat == null)
                throw new Exception("Input format is required for file without extension");
            if (outputFormat == null)
                throw new ArgumentNullException(nameof(outputFormat));
            this.ConvertMedia(new Media()
            {
                Filename = inputFile,
                Format = inputFormat
            }, new Media()
            {
                DataStream = outputStream,
                Format = outputFormat
            }, settings ?? new ConvertSettings());
        }

        /// <summary>
        /// Converts several input files into one resulting output file.
        /// </summary>
        /// <param name="inputs">one or more FFMpeg input specifiers</param>
        /// <param name="output">output file name</param>
        /// <param name="outputFormat">output file format (optional, can be null)</param>
        /// <param name="settings">output settings</param>
        public void ConvertMedia(
          FFMpegInput[] inputs,
          string output,
          string outputFormat,
          OutputSettings settings)
        {
            if (inputs == null || inputs.Length == 0)
                throw new ArgumentException("At least one ffmpeg input should be specified");
            FFMpegInput input1 = inputs[inputs.Length - 1];
            StringBuilder stringBuilder = new StringBuilder();
            for (int index = 0; index < inputs.Length - 1; ++index)
            {
                FFMpegInput input2 = inputs[index];
                if (input2.Format != null)
                    stringBuilder.Append(" -f " + input2.Format);
                if (input2.CustomInputArgs != null)
                    stringBuilder.AppendFormat(" {0} ", (object)input2.CustomInputArgs);
                stringBuilder.AppendFormat(" -i {0} ", (object)this.CommandArgParameter(input2.Input));
            }
            ConvertSettings settings1 = new ConvertSettings();
            settings.CopyTo((OutputSettings)settings1);
            settings1.CustomInputArgs = stringBuilder.ToString() + input1.CustomInputArgs;
            this.ConvertMedia(input1.Input, input1.Format, output, outputFormat, settings1);
        }

        /// <summary>
        /// Create a task for live stream conversion (real-time) without input source. Input data should be passed with Write method.
        /// </summary>
        /// <param name="inputFormat">input stream media format</param>
        /// <param name="outputStream">output media stream</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">convert settings</param>
        /// <returns>instance of <see cref="T:NReco.VideoConverter.ConvertLiveMediaTask" /></returns>
        public ConvertLiveMediaTask ConvertLiveMedia(
          string inputFormat,
          Stream outputStream,
          string outputFormat,
          ConvertSettings settings)
        {
            return this.ConvertLiveMedia((Stream)null, inputFormat, outputStream, outputFormat, settings);
        }

        /// <summary>
        /// Create a task for live stream conversion (real-time) that reads data from FFMpeg input source and write conversion result to output stream
        /// </summary>
        /// <param name="inputSource">input source string identifier (file path, UDP or TCP source, local video device name)</param>
        /// <param name="inputFormat">input stream media format</param>
        /// <param name="outputStream">output media stream</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">convert settings</param>
        /// <returns>instance of <see cref="T:NReco.VideoConverter.ConvertLiveMediaTask" /></returns>
        public ConvertLiveMediaTask ConvertLiveMedia(
          string inputSource,
          string inputFormat,
          Stream outputStream,
          string outputFormat,
          ConvertSettings settings)
        {
            this.EnsureFFMpegLibs();
            return this.CreateLiveMediaTask(this.ComposeFFMpegCommandLineArgs(inputSource, inputFormat, "-", outputFormat, settings), (Stream)null, outputStream, settings);
        }

        /// <summary>
        /// Create a task for live stream conversion (real-time) that reads data from stream and writes conversion result to the file
        /// </summary>
        /// <param name="inputStream">input live stream (null if data is provided by calling "Write" method)</param>
        /// <param name="inputFormat">input stream media format</param>
        /// <param name="outputFile">output file path</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">convert settings</param>
        /// <returns>instance of <see cref="T:NReco.VideoConverter.ConvertLiveMediaTask" /></returns>
        public ConvertLiveMediaTask ConvertLiveMedia(
          Stream inputStream,
          string inputFormat,
          string outputFile,
          string outputFormat,
          ConvertSettings settings)
        {
            this.EnsureFFMpegLibs();
            return this.CreateLiveMediaTask(this.ComposeFFMpegCommandLineArgs("-", inputFormat, outputFile, outputFormat, settings), inputStream, (Stream)null, settings);
        }

        /// <summary>
        /// Create a task for live stream conversion (real-time) that reads data from input stream and write conversion result to output stream
        /// </summary>
        /// <param name="inputStream">input live stream (null if data is provided by calling "Write" method)</param>
        /// <param name="inputFormat">input stream media format</param>
        /// <param name="outputStream">output media stream</param>
        /// <param name="outputFormat">output media format</param>
        /// <param name="settings">convert settings</param>
        /// <returns>instance of <see cref="T:NReco.VideoConverter.ConvertLiveMediaTask" /></returns>
        public ConvertLiveMediaTask ConvertLiveMedia(
          Stream inputStream,
          string inputFormat,
          Stream outputStream,
          string outputFormat,
          ConvertSettings settings)
        {
            this.EnsureFFMpegLibs();
            return this.CreateLiveMediaTask(this.ComposeFFMpegCommandLineArgs("-", inputFormat, "-", outputFormat, settings), inputStream, outputStream, settings);
        }

        private ConvertLiveMediaTask CreateLiveMediaTask(
          string toolArgs,
          Stream inputStream,
          Stream outputStream,
          ConvertSettings settings)
        {
            FFMpegProgress progress = new FFMpegProgress(new Action<ConvertProgressEventArgs>(this.OnConvertProgress), this.ConvertProgress != null);
            if (settings != null)
            {
                progress.Seek = settings.Seek;
                progress.MaxDuration = settings.MaxDuration;
            }
            return new ConvertLiveMediaTask(this, toolArgs, inputStream, outputStream, progress);
        }

        /// <summary>
        /// Extract video thumbnail (first frame) from local video file
        /// </summary>
        /// <param name="inputFile">path to local video file</param>
        /// <param name="outputJpegStream">output stream for thumbnail in jpeg format</param>
        public void GetVideoThumbnail(string inputFile, Stream outputJpegStream)
        {
            this.GetVideoThumbnail(inputFile, outputJpegStream, new float?());
        }

        /// <summary>
        /// Extract video thumbnail (first frame) from local video file
        /// </summary>
        /// <param name="inputFile">path to local video file</param>
        /// <param name="outputFile">path to thumbnail jpeg file</param>
        public void GetVideoThumbnail(string inputFile, string outputFile)
        {
            this.GetVideoThumbnail(inputFile, outputFile, new float?());
        }

        /// <summary>
        /// Extract video frame from local video file at specified position
        /// </summary>
        /// <param name="inputFile">path to local video file</param>
        /// <param name="outputJpegStream">output stream for thumbnail in jpeg format</param>
        /// <param name="frameTime">video position (in seconds)</param>
        public void GetVideoThumbnail(string inputFile, Stream outputJpegStream, float? frameTime)
        {
            Media input = new Media()
            {
                Filename = inputFile
            };
            Media output = new Media()
            {
                DataStream = outputJpegStream,
                Format = "mjpeg"
            };
            ConvertSettings convertSettings = new ConvertSettings();
            convertSettings.VideoFrameCount = new int?(1);
            convertSettings.Seek = frameTime;
            convertSettings.MaxDuration = new float?(1f);
            ConvertSettings settings = convertSettings;
            this.ConvertMedia(input, output, settings);
        }

        /// <summary>
        /// Extract video frame from local video file at specified position
        /// </summary>
        /// <param name="inputFile">path to local video file</param>
        /// <param name="outputFile">path to thumbnail jpeg file</param>
        /// <param name="frameTime">video position (in seconds)</param>
        public void GetVideoThumbnail(string inputFile, string outputFile, float? frameTime)
        {
            Media input = new Media()
            {
                Filename = inputFile
            };
            Media output = new Media()
            {
                Filename = outputFile,
                Format = "mjpeg"
            };
            ConvertSettings convertSettings = new ConvertSettings();
            convertSettings.VideoFrameCount = new int?(1);
            convertSettings.Seek = frameTime;
            convertSettings.MaxDuration = new float?(1f);
            ConvertSettings settings = convertSettings;
            this.ConvertMedia(input, output, settings);
        }

        private string CommandArgParameter(string arg)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append('"');
            stringBuilder.Append(arg);
            stringBuilder.Append('"');
            return stringBuilder.ToString();
        }

        internal void InitStartInfo(ProcessStartInfo startInfo)
        {
            if (this.FFMpegProcessUser == null)
                return;
            if (this.FFMpegProcessUser.Domain != null)
                startInfo.Domain = this.FFMpegProcessUser.Domain;
            if (this.FFMpegProcessUser.UserName != null)
                startInfo.UserName = this.FFMpegProcessUser.UserName;
            if (this.FFMpegProcessUser.Password == null)
                return;
            startInfo.Password = this.FFMpegProcessUser.Password;
        }

        internal string GetFFMpegExePath()
        {
            return Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
        }

        /// <summary>Concatenate several video files</summary>
        /// <param name="inputFiles">list of local video files</param>
        /// <param name="outputFile">path to contactenation result file</param>
        /// <param name="outputFormat">desired output format</param>
        /// <param name="settings">convert settings</param>
        /// <remarks>
        /// Note: all video files should have the same video frame size and audio stream.
        /// </remarks>
        public void ConcatMedia(
          string[] inputFiles,
          string outputFile,
          string outputFormat,
          ConcatSettings settings)
        {
            this.EnsureFFMpegLibs();
            string ffMpegExePath = this.GetFFMpegExePath();
            if (!File.Exists(ffMpegExePath))
                throw new FileNotFoundException("Cannot find ffmpeg tool: " + ffMpegExePath);
            StringBuilder stringBuilder = new StringBuilder();
            foreach (string inputFile in inputFiles)
            {
                if (!File.Exists(inputFile))
                    throw new FileNotFoundException("Cannot find input video file: " + inputFile);
                stringBuilder.AppendFormat(" -i {0} ", (object)this.CommandArgParameter(inputFile));
            }
            StringBuilder outputArgs = new StringBuilder();
            this.ComposeFFMpegOutputArgs(outputArgs, outputFormat, (OutputSettings)settings);
            outputArgs.Append(" -filter_complex \"");
            outputArgs.AppendFormat("concat=n={0}", (object)inputFiles.Length);
            if (settings.ConcatVideoStream)
                outputArgs.Append(":v=1");
            if (settings.ConcatAudioStream)
                outputArgs.Append(":a=1");
            if (settings.ConcatVideoStream)
                outputArgs.Append(" [v]");
            if (settings.ConcatAudioStream)
                outputArgs.Append(" [a]");
            outputArgs.Append("\" ");
            if (settings.ConcatVideoStream)
                outputArgs.Append(" -map \"[v]\" ");
            if (settings.ConcatAudioStream)
                outputArgs.Append(" -map \"[a]\" ");
            string arguments = string.Format("-y -loglevel {3} {0} {1} {2}", (object)stringBuilder.ToString(), (object)outputArgs, (object)this.CommandArgParameter(outputFile), (object)this.LogLevel);
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(ffMpegExePath, arguments);
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath);
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                this.InitStartInfo(startInfo);
                if (this.FFMpegProcess != null)
                    throw new InvalidOperationException("FFMpeg process is already started");
                this.FFMpegProcess = Process.Start(startInfo);
                if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
                    this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
                string lastErrorLine = string.Empty;
                FFMpegProgress ffmpegProgress = new FFMpegProgress(new Action<ConvertProgressEventArgs>(this.OnConvertProgress), this.ConvertProgress != null);
                if (settings != null)
                    ffmpegProgress.MaxDuration = settings.MaxDuration;
                this.FFMpegProcess.ErrorDataReceived += (DataReceivedEventHandler)((o, args) =>
               {
                   if (args.Data == null)
                       return;
                   lastErrorLine = args.Data;
                   ffmpegProgress.ParseLine(args.Data);
                   this.FFMpegLogHandler(args.Data);
               });
                this.FFMpegProcess.OutputDataReceived += (DataReceivedEventHandler)((o, args) => { });
                this.FFMpegProcess.BeginOutputReadLine();
                this.FFMpegProcess.BeginErrorReadLine();
                this.WaitFFMpegProcessForExit();
                if (this.FFMpegProcess.ExitCode != 0)
                    throw new FFMpegException(this.FFMpegProcess.ExitCode, lastErrorLine);
                this.FFMpegProcess.Close();
                this.FFMpegProcess = (Process)null;
                ffmpegProgress.Complete();
            }
            catch (Exception ex)
            {
                this.EnsureFFMpegProcessStopped();
                throw;
            }
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

        protected void ComposeFFMpegOutputArgs(
          StringBuilder outputArgs,
          string outputFormat,
          OutputSettings settings)
        {
            if (settings == null)
                return;
            if (settings.MaxDuration.HasValue)
                outputArgs.AppendFormat((IFormatProvider)CultureInfo.InvariantCulture, " -t {0}", (object)settings.MaxDuration);
            if (outputFormat != null)
                outputArgs.AppendFormat(" -f {0} ", (object)outputFormat);
            if (settings.AudioSampleRate.HasValue)
                outputArgs.AppendFormat(" -ar {0}", (object)settings.AudioSampleRate);
            if (settings.AudioCodec != null)
                outputArgs.AppendFormat(" -acodec {0}", (object)settings.AudioCodec);
            if (settings.VideoFrameCount.HasValue)
                outputArgs.AppendFormat(" -vframes {0}", (object)settings.VideoFrameCount);
            if (settings.VideoFrameRate.HasValue)
                outputArgs.AppendFormat(" -r {0}", (object)settings.VideoFrameRate);
            if (settings.VideoCodec != null)
                outputArgs.AppendFormat(" -vcodec {0}", (object)settings.VideoCodec);
            if (settings.VideoFrameSize != null)
                outputArgs.AppendFormat(" -s {0}", (object)settings.VideoFrameSize);
            if (settings.CustomOutputArgs == null)
                return;
            outputArgs.AppendFormat(" {0} ", (object)settings.CustomOutputArgs);
        }

        protected string ComposeFFMpegCommandLineArgs(
          string inputFile,
          string inputFormat,
          string outputFile,
          string outputFormat,
          ConvertSettings settings)
        {
            StringBuilder stringBuilder = new StringBuilder();
            if (settings.AppendSilentAudioStream)
                stringBuilder.Append(" -f lavfi -i aevalsrc=0 ");
            if (settings.Seek.HasValue)
                stringBuilder.AppendFormat((IFormatProvider)CultureInfo.InvariantCulture, " -ss {0}", (object)settings.Seek);
            if (inputFormat != null)
                stringBuilder.Append(" -f " + inputFormat);
            if (settings.CustomInputArgs != null)
                stringBuilder.AppendFormat(" {0} ", (object)settings.CustomInputArgs);
            StringBuilder outputArgs = new StringBuilder();
            this.ComposeFFMpegOutputArgs(outputArgs, outputFormat, (OutputSettings)settings);
            if (settings.AppendSilentAudioStream)
                outputArgs.Append(" -shortest ");
            return string.Format("-y -loglevel {4} {0} -i {1} {2} {3}", (object)stringBuilder.ToString(), (object)this.CommandArgParameter(inputFile), (object)outputArgs.ToString(), (object)this.CommandArgParameter(outputFile), (object)this.LogLevel);
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

                ProcessStartInfo startInfo = new ProcessStartInfo(ffMpegExePath, ffmpegArgs);
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow = true;
                startInfo.UseShellExecute = false;
                startInfo.WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath);
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = true;
                this.InitStartInfo(startInfo);
                if (this.FFMpegProcess != null)
                    throw new InvalidOperationException("FFMpeg process is already started");
                this.FFMpegProcess = Process.Start(startInfo);
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

        internal void OnConvertProgress(ConvertProgressEventArgs args)
        {
            if (this.ConvertProgress == null)
                return;
            this.ConvertProgress((object)this, args);
        }

        internal void ConvertMedia(Media input, Media output, ConvertSettings settings)
        {
            this.EnsureFFMpegLibs();
            string str1 = input.Filename;
            if (str1 == null)
            {
                str1 = Path.GetTempFileName();
                using (FileStream fileStream = new FileStream(str1, FileMode.Create, FileAccess.Write, FileShare.None))
                    this.CopyStream(input.DataStream, (Stream)fileStream, 262144);
            }
            string str2 = output.Filename ?? Path.GetTempFileName();
            if (!(output.Format == "flv"))
            {
                if (!(Path.GetExtension(str2).ToLower() == ".flv"))
                    goto label_10;
            }
            if (!settings.AudioSampleRate.HasValue)
                settings.AudioSampleRate = new int?(44100);
            label_10:
            try
            {
                string ffMpegExePath = this.GetFFMpegExePath();
                if (!File.Exists(ffMpegExePath))
                    throw new FileNotFoundException("Cannot find ffmpeg tool: " + ffMpegExePath);
                string arguments = this.ComposeFFMpegCommandLineArgs(str1, input.Format, str2, output.Format, settings);
                ProcessStartInfo startInfo = new ProcessStartInfo(ffMpegExePath, arguments);
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow = true;
                startInfo.UseShellExecute = false;
                startInfo.WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath);
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                this.InitStartInfo(startInfo);
                if (this.FFMpegProcess != null)
                    throw new InvalidOperationException("FFMpeg process is already started");
                this.FFMpegProcess = Process.Start(startInfo);
                if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
                    this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
                string lastErrorLine = string.Empty;
                FFMpegProgress ffmpegProgress = new FFMpegProgress(new Action<ConvertProgressEventArgs>(this.OnConvertProgress), this.ConvertProgress != null);
                if (settings != null)
                {
                    ffmpegProgress.Seek = settings.Seek;
                    ffmpegProgress.MaxDuration = settings.MaxDuration;
                }
                this.FFMpegProcess.ErrorDataReceived += (DataReceivedEventHandler)((o, args) =>
               {
                   if (args.Data == null)
                       return;
                   lastErrorLine = args.Data;
                   ffmpegProgress.ParseLine(args.Data);
                   this.FFMpegLogHandler(args.Data);
               });
                this.FFMpegProcess.OutputDataReceived += (DataReceivedEventHandler)((o, args) => { });
                this.FFMpegProcess.BeginOutputReadLine();
                this.FFMpegProcess.BeginErrorReadLine();
                this.WaitFFMpegProcessForExit();
                if (this.FFMpegProcess.ExitCode != 0)
                    throw new FFMpegException(this.FFMpegProcess.ExitCode, lastErrorLine);
                this.FFMpegProcess.Close();
                this.FFMpegProcess = (Process)null;
                ffmpegProgress.Complete();
                if (output.Filename != null)
                    return;
                using (FileStream fileStream = new FileStream(str2, FileMode.Open, FileAccess.Read, FileShare.None))
                    this.CopyStream((Stream)fileStream, output.DataStream, 262144);
            }
            catch (Exception ex)
            {
                this.EnsureFFMpegProcessStopped();
                throw;
            }
            finally
            {
                if (str1 != null && input.Filename == null && File.Exists(str1))
                    File.Delete(str1);
                if (str2 != null && output.Filename == null && File.Exists(str2))
                    File.Delete(str2);
            }
        }

        /// <summary>
        /// Extracts ffmpeg binaries (if needed) to the location specified by <see cref="P:NReco.VideoConverter.FFMpegConverter.FFMpegToolPath" />.
        /// </summary>
        /// <remarks><para>If missed ffmpeg is extracted automatically before starting conversion process.
        /// In some cases it is better to do that explicetily on the application start by calling <see cref="M:NReco.VideoConverter.FFMpegConverter.ExtractFFmpeg" /> method.</para>
        /// <para>This method is not available in LT version (without embedded ffmpeg binaries).</para></remarks>
        public void ExtractFFmpeg()
        {
            this.EnsureFFMpegLibs();
        }

        private void EnsureFFMpegLibs()
        {
            // find optimal ffmpeg exe in tool path directory
            //var bitness = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            //var optimal = Directory.GetFiles(this.FFMpegToolPath, "ffmpeg*" + bitness + "*").OrderByDescending(s => s).FirstOrDefault();
            //if (String.IsNullOrEmpty(optimal))
            //{
            //}
            //else
            //{
            //    this.FFMpegExeName = Path.GetFileName(optimal);
            //}

            lock (FFMpegConverter.globalObj)
            {
                var bitness = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                Assembly executingAssembly = Assembly.GetExecutingAssembly();
                string[] manifestResourceNames = executingAssembly.GetManifestResourceNames();
                string str = "NReco.VideoConverter.FFMpeg.";
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
