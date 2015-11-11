using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Video converter component (wrapper to FFMpeg process)
	/// </summary>
	/// <sort>10</sort>
	public class FFMpegConverter
	{
		protected Process FFMpegProcess;

		protected Regex DurationRegex = new Regex("Duration:\\s(?<duration>[0-9:.]+)([,]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

		protected Regex ProgressRegex = new Regex("time=(?<progress>[0-9:.]+)\\s", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

		protected static object globalObj;

		/// <summary>
		/// Get or set maximum execution time for conversion process (null is by default - means no timeout)
		/// </summary>
		public TimeSpan? ExecutionTimeout
		{
			get;
			set;
		}

		/// <summary>
		/// Get or set FFMpeg tool EXE file name ('ffmpeg.exe' by default)
		/// </summary>
		public string FFMpegExeName
		{
			get;
			set;
		}

		/// <summary>
		/// Get or set FFMpeg process priority (Normal by default)
		/// </summary>
		public ProcessPriorityClass FFMpegProcessPriority
		{
			get;
			set;
		}

		/// <summary>
		/// Get or set path where FFMpeg tool is located
		/// </summary>
		/// <remarks>
		/// By default this property points to the folder where application assemblies are located.
		/// If WkHtmlToPdf tool files are not present PdfConverter expands them from DLL resources.
		/// </remarks>
		public string FFMpegToolPath
		{
			get;
			set;
		}

		static FFMpegConverter()
		{
			FFMpegConverter.globalObj = new object();
		}

		/// <summary>
		/// Initializes a new instance of the FFMpegConverter class.
		/// </summary>
		/// <remarks>
		/// FFMpegConverter is NOT thread-safe. Separate instance should be used for each thread.
		/// </remarks>
		public FFMpegConverter()
		{
			this.FFMpegProcessPriority = ProcessPriorityClass.Normal;
			this.FFMpegToolPath = AppDomain.CurrentDomain.BaseDirectory;
			if (string.IsNullOrEmpty(this.FFMpegToolPath))
			{
				this.FFMpegToolPath = Path.GetDirectoryName(typeof(FFMpegConverter).Assembly.Location);
			}
			this.FFMpegExeName = "ffmpeg.exe";
		}

		/// <summary>
		/// Abort FFMpeg process started by ConvertMedia or ConcatMedia methods
		/// </summary>
		/// <remarks>This method IMMEDIATELY stops FFMpeg by killing the process. Resulting file may be inconsistent.</remarks>
		public void Abort()
		{
			this.EnsureFFMpegProcessStopped();
		}

		protected string CommandArgParameter(string arg)
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append('\"');
			stringBuilder.Append(arg);
			stringBuilder.Append('\"');
			return stringBuilder.ToString();
		}

		protected string ComposeFFMpegCommandLineArgs(string inputFile, string inputFormat, string outputFile, string outputFormat, ConvertSettings settings)
		{
			StringBuilder stringBuilder = new StringBuilder();
			if (settings.AppendSilentAudioStream)
			{
				stringBuilder.Append(" -f lavfi -i aevalsrc=0 ");
			}
			if (settings.Seek.HasValue)
			{
				CultureInfo invariantCulture = CultureInfo.InvariantCulture;
				object[] seek = new object[] { settings.Seek };
				stringBuilder.AppendFormat(invariantCulture, " -ss {0}", seek);
			}
			if (inputFormat != null)
			{
				stringBuilder.Append(string.Concat(" -f ", inputFormat));
			}
			if (settings.CustomInputArgs != null)
			{
				stringBuilder.AppendFormat(" {0} ", settings.CustomInputArgs);
			}
			StringBuilder stringBuilder1 = new StringBuilder();
			this.ComposeFFMpegOutputArgs(stringBuilder1, outputFormat, settings);
			if (settings.AppendSilentAudioStream)
			{
				stringBuilder1.Append(" -shortest ");
			}
			object[] str = new object[] { stringBuilder.ToString(), this.CommandArgParameter(inputFile), stringBuilder1.ToString(), this.CommandArgParameter(outputFile) };
			return string.Format("-y -loglevel info {0} -i {1} {2} {3}", str);
		}

		protected void ComposeFFMpegOutputArgs(StringBuilder outputArgs, string outputFormat, OutputSettings settings)
		{
			if (settings == null)
			{
				return;
			}
			if (settings.MaxDuration.HasValue)
			{
				CultureInfo invariantCulture = CultureInfo.InvariantCulture;
				object[] maxDuration = new object[] { settings.MaxDuration };
				outputArgs.AppendFormat(invariantCulture, " -t {0}", maxDuration);
			}
			if (outputFormat != null)
			{
				outputArgs.AppendFormat(" -f {0} ", outputFormat);
			}
			if (settings.AudioSampleRate.HasValue)
			{
				outputArgs.AppendFormat(" -ar {0}", settings.AudioSampleRate);
			}
			if (settings.AudioCodec != null)
			{
				outputArgs.AppendFormat(" -acodec {0}", settings.AudioCodec);
			}
			if (settings.VideoFrameCount.HasValue)
			{
				outputArgs.AppendFormat(" -vframes {0}", settings.VideoFrameCount);
			}
			if (settings.VideoFrameRate.HasValue)
			{
				outputArgs.AppendFormat(" -r {0}", settings.VideoFrameRate);
			}
			if (settings.VideoCodec != null)
			{
				outputArgs.AppendFormat(" -vcodec {0}", settings.VideoCodec);
			}
			if (settings.VideoFrameSize != null)
			{
				outputArgs.AppendFormat(" -s {0}", settings.VideoFrameSize);
			}
			if (settings.CustomOutputArgs != null)
			{
				outputArgs.AppendFormat(" {0} ", settings.CustomOutputArgs);
			}
		}

		/// <summary>
		/// Concatenate several video files
		/// </summary>
		/// <param name="inputFiles">list of local video files</param>
		/// <param name="outputFile">path to contactenation result file</param>
		/// <param name="outputFormat">desired output format</param>
		/// <param name="settings">convert settings</param>
		/// <remarks>
		/// Note: all video files should have the same video frame size and audio stream.
		/// </remarks>
		public void ConcatMedia(string[] inputFiles, string outputFile, string outputFormat, ConcatSettings settings)
		{
			this.EnsureFFMpegLibs();
			string str = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
			if (!File.Exists(str))
			{
				throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str));
			}
			StringBuilder stringBuilder = new StringBuilder();
			string[] strArrays = inputFiles;
			for (int i = 0; i < (int)strArrays.Length; i++)
			{
				string str1 = strArrays[i];
				if (!File.Exists(str1))
				{
					throw new FileNotFoundException(string.Concat("Cannot find input video file: ", str1));
				}
				stringBuilder.AppendFormat(" -i {0} ", this.CommandArgParameter(str1));
			}
			StringBuilder stringBuilder1 = new StringBuilder();
			this.ComposeFFMpegOutputArgs(stringBuilder1, outputFormat, settings);
			stringBuilder1.Append(" -filter_complex \"");
			stringBuilder1.AppendFormat("concat=n={0}", (int)inputFiles.Length);
			if (settings.ConcatVideoStream)
			{
				stringBuilder1.Append(":v=1");
			}
			if (settings.ConcatAudioStream)
			{
				stringBuilder1.Append(":a=1");
			}
			if (settings.ConcatVideoStream)
			{
				stringBuilder1.Append(" [v]");
			}
			if (settings.ConcatAudioStream)
			{
				stringBuilder1.Append(" [a]");
			}
			stringBuilder1.Append("\" ");
			if (settings.ConcatVideoStream)
			{
				stringBuilder1.Append(" -map \"[v]\" ");
			}
			if (settings.ConcatAudioStream)
			{
				stringBuilder1.Append(" -map \"[a]\" ");
			}
			string str2 = string.Format("-nostdin -y -loglevel info {0} {1} {2}", stringBuilder.ToString(), stringBuilder1, this.CommandArgParameter(outputFile));
			try
			{
				ProcessStartInfo processStartInfo = new ProcessStartInfo(str, string.Concat("-nostdin ", str2))
				{
					WindowStyle = ProcessWindowStyle.Hidden,
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath),
					RedirectStandardInput = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};
				if (this.FFMpegProcess != null)
				{
					throw new InvalidOperationException("FFMpeg process is already started");
				}
				this.FFMpegProcess = Process.Start(processStartInfo);
				if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
				{
					this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
				}
				string empty = string.Empty;
				TimeSpan timeSpan1 = TimeSpan.Zero;
				ConvertProgressEventArgs convertProgressEventArg = null;
				this.FFMpegProcess.ErrorDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
					if (args.Data == null)
					{
						return;
					}
					empty = args.Data;
					if (this.ConvertProgress != null)
					{
						System.Text.RegularExpressions.Match match = this.DurationRegex.Match(args.Data);
						if (match.Success)
						{
							TimeSpan zero = TimeSpan.Zero;
							if (TimeSpan.TryParse(match.Groups["duration"].Value, out zero))
							{
								timeSpan1 = timeSpan1.Add(zero);
								convertProgressEventArg = new ConvertProgressEventArgs(TimeSpan.Zero, timeSpan1);
							}
						}
						System.Text.RegularExpressions.Match match1 = this.ProgressRegex.Match(args.Data);
						if (match1.Success && timeSpan1 != TimeSpan.Zero)
						{
							TimeSpan timeSpan = TimeSpan.Zero;
							if (TimeSpan.TryParse(match1.Groups["progress"].Value, out timeSpan))
							{
								convertProgressEventArg = new ConvertProgressEventArgs(timeSpan, timeSpan1);
								this.ConvertProgress(this, convertProgressEventArg);
							}
						}
					}
				});
				this.FFMpegProcess.OutputDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
				});
				this.FFMpegProcess.BeginOutputReadLine();
				this.FFMpegProcess.BeginErrorReadLine();
				this.WaitFFMpegProcessForExit();
				if (this.FFMpegProcess.ExitCode != 0)
				{
					throw new FFMpegException(this.FFMpegProcess.ExitCode, empty);
				}
				this.FFMpegProcess.Close();
				this.FFMpegProcess = null;
				if (this.ConvertProgress != null && convertProgressEventArg != null && convertProgressEventArg.Processed < convertProgressEventArg.TotalDuration)
				{
					this.ConvertProgress(this, new ConvertProgressEventArgs(convertProgressEventArg.TotalDuration, convertProgressEventArg.TotalDuration));
				}
			}
			catch (Exception exception)
			{
				this.EnsureFFMpegProcessStopped();
				throw;
			}
		}

		/// <summary>
		/// Create a task for live stream conversion (real-time) without input source. Input data should be passed with Write method.
		/// </summary>
		/// <param name="inputFormat">input stream media format</param>
		/// <param name="outputStream">output media stream</param>
		/// <param name="outputFormat">output media format</param>
		/// <param name="settings">convert settings</param>
		/// <returns>instance of <see cref="T:NReco.VideoConverter.ConvertLiveMediaTask" /></returns>
		public ConvertLiveMediaTask ConvertLiveMedia(string inputFormat, Stream outputStream, string outputFormat, ConvertSettings settings)
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
		public ConvertLiveMediaTask ConvertLiveMedia(string inputSource, string inputFormat, Stream outputStream, string outputFormat, ConvertSettings settings)
		{
			this.EnsureFFMpegLibs();
			string str = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
			if (!File.Exists(str))
			{
				throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str));
			}
			string str1 = this.ComposeFFMpegCommandLineArgs(inputSource, inputFormat, "-", outputFormat, settings);
			return new ConvertLiveMediaTask(str, str1, null, outputStream);
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
		public ConvertLiveMediaTask ConvertLiveMedia(Stream inputStream, string inputFormat, string outputFile, string outputFormat, ConvertSettings settings)
		{
			this.EnsureFFMpegLibs();
			string str = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
			if (!File.Exists(str))
			{
				throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str));
			}
			string str1 = this.ComposeFFMpegCommandLineArgs("-", inputFormat, outputFile, outputFormat, settings);
			return new ConvertLiveMediaTask(str, str1, inputStream, null);
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
		public ConvertLiveMediaTask ConvertLiveMedia(Stream inputStream, string inputFormat, Stream outputStream, string outputFormat, ConvertSettings settings)
		{
			this.EnsureFFMpegLibs();
			string str = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
			if (!File.Exists(str))
			{
				throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str));
			}
			string str1 = this.ComposeFFMpegCommandLineArgs("-", inputFormat, "-", outputFormat, settings);
			return new ConvertLiveMediaTask(str, str1, inputStream, outputStream);
		}

		/// <summary>
		/// Converts media represented by local file and writes result to specified local file
		/// </summary>
		/// <param name="inputFile">local path to input media file</param>
		/// <param name="outputFile">local path to ouput media file</param>
		/// <param name="outputFormat">desired output format (like "mp4" or "flv")</param>
		public void ConvertMedia(string inputFile, string outputFile, string outputFormat)
		{
			this.ConvertMedia(inputFile, null, outputFile, outputFormat, null);
		}

		/// <summary>
		/// Converts media represented by local file and writes result to specified local file using explicit convert settings
		/// </summary>
		/// <param name="inputFile">local path to input media file</param>
		/// <param name="inputFormat">input format (null for automatic format suggestion)</param>
		/// <param name="outputFile">local path to output media file</param>
		/// <param name="outputFormat">output media format</param>
		/// <param name="settings">explicit convert settings</param>
		public void ConvertMedia(string inputFile, string inputFormat, string outputFile, string outputFormat, ConvertSettings settings)
		{
			if (inputFile == null)
			{
				throw new ArgumentNullException("inputFile");
			}
			if (outputFile == null)
			{
				throw new ArgumentNullException("outputFile");
			}
			if (File.Exists(inputFile) && string.IsNullOrEmpty(Path.GetExtension(inputFile)) && inputFormat == null)
			{
				throw new Exception("Input format is required for file without extension");
			}
			if (string.IsNullOrEmpty(Path.GetExtension(outputFile)) && outputFormat == null)
			{
				throw new Exception("Output format is required for file without extension");
			}
			Media medium = new Media()
			{
				Filename = inputFile,
				Format = inputFormat
			};
			Media medium1 = medium;
			Media medium2 = new Media()
			{
				Filename = outputFile,
				Format = outputFormat
			};
			this.ConvertMedia(medium1, medium2, settings ?? new ConvertSettings());
		}

		/// <summary>
		/// Converts media represented by local file and writes result to specified stream
		/// </summary>
		/// <param name="inputFile">local path to input media file</param>
		/// <param name="outputStream">output stream</param>
		/// <param name="outputFormat">output media format</param>
		public void ConvertMedia(string inputFile, Stream outputStream, string outputFormat)
		{
			this.ConvertMedia(inputFile, null, outputStream, outputFormat, null);
		}

		/// <summary>
		/// Converts media represented by local file and writes result to specified stream using explicit convert settings
		/// </summary>
		/// <param name="inputFile">local path to input media file</param>
		/// <param name="inputFormat">input format (null for automatic format suggestion)</param>
		/// <param name="outputStream">output stream</param>
		/// <param name="outputFormat">output media format</param>
		/// <param name="settings">convert settings</param>
		public void ConvertMedia(string inputFile, string inputFormat, Stream outputStream, string outputFormat, ConvertSettings settings)
		{
			if (inputFile == null)
			{
				throw new ArgumentNullException("inputFile");
			}
			if (File.Exists(inputFile) && string.IsNullOrEmpty(Path.GetExtension(inputFile)) && inputFormat == null)
			{
				throw new Exception("Input format is required for file without extension");
			}
			if (outputFormat == null)
			{
				throw new ArgumentNullException("outputFormat");
			}
			Media medium = new Media()
			{
				Filename = inputFile,
				Format = inputFormat
			};
			Media medium1 = medium;
			Media medium2 = new Media()
			{
				DataStream = outputStream,
				Format = outputFormat
			};
			this.ConvertMedia(medium1, medium2, settings ?? new ConvertSettings());
		}

		internal void ConvertMedia(Media input, Media output, ConvertSettings settings)
		{
			this.EnsureFFMpegLibs();
			string filename = input.Filename;
			if (filename == null)
			{
				filename = Path.GetTempFileName();
				using (FileStream fileStream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					this.CopyStream(input.DataStream, fileStream, 262144);
				}
			}
			string str = output.Filename ?? Path.GetTempFileName();
			if ((output.Format == "flv" || Path.GetExtension(str).ToLower() == ".flv") && !settings.AudioSampleRate.HasValue)
			{
				settings.AudioSampleRate = new int?(44100);
			}
			try
			{
				try
				{
					string str1 = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
					if (!File.Exists(str1))
					{
						throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str1));
					}
					string str2 = this.ComposeFFMpegCommandLineArgs(filename, input.Format, str, output.Format, settings);
					ProcessStartInfo processStartInfo = new ProcessStartInfo(str1, string.Concat("-nostdin ", str2))
					{
						WindowStyle = ProcessWindowStyle.Hidden,
						CreateNoWindow = true,
						UseShellExecute = false,
						WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath),
						RedirectStandardInput = false,
						RedirectStandardOutput = true,
						RedirectStandardError = true
					};
					if (this.FFMpegProcess != null)
					{
						throw new InvalidOperationException("FFMpeg process is already started");
					}
					this.FFMpegProcess = Process.Start(processStartInfo);
					if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
					{
						this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
					}
					string empty = string.Empty;
					TimeSpan timeSpan2 = TimeSpan.Zero;
					ConvertProgressEventArgs convertProgressEventArg = null;
					this.FFMpegProcess.ErrorDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
						if (args.Data == null)
						{
							return;
						}
						empty = args.Data;
						if (this.ConvertProgress != null)
						{
							System.Text.RegularExpressions.Match match = this.DurationRegex.Match(args.Data);
							if (match.Success && TimeSpan.TryParse(match.Groups["duration"].Value, out timeSpan2))
							{
								if (settings != null)
								{
									if (settings.Seek.HasValue)
									{
										TimeSpan timeSpan = TimeSpan.FromSeconds((double)settings.Seek.Value);
										timeSpan2 = (timeSpan2 > timeSpan ? timeSpan2.Subtract(timeSpan) : TimeSpan.Zero);
									}
									if (settings.MaxDuration.HasValue)
									{
										TimeSpan timeSpan1 = TimeSpan.FromSeconds((double)settings.MaxDuration.Value);
										if (timeSpan2 > timeSpan1)
										{
											timeSpan2 = timeSpan1;
										}
									}
								}
								convertProgressEventArg = new ConvertProgressEventArgs(TimeSpan.Zero, timeSpan2);
								this.ConvertProgress(this, convertProgressEventArg);
							}
							System.Text.RegularExpressions.Match match1 = this.ProgressRegex.Match(args.Data);
							if (match1.Success && timeSpan2 != TimeSpan.Zero)
							{
								TimeSpan zero = TimeSpan.Zero;
								if (TimeSpan.TryParse(match1.Groups["progress"].Value, out zero))
								{
									convertProgressEventArg = new ConvertProgressEventArgs(zero, timeSpan2);
									this.ConvertProgress(this, convertProgressEventArg);
								}
							}
						}
						if (this.LogReceived != null)
						{
							this.LogReceived(this, new FFMpegLogEventArgs(args.Data));
						}
					});
					this.FFMpegProcess.OutputDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
					});
					this.FFMpegProcess.BeginOutputReadLine();
					this.FFMpegProcess.BeginErrorReadLine();
					this.WaitFFMpegProcessForExit();
					if (this.FFMpegProcess.ExitCode != 0)
					{
						throw new FFMpegException(this.FFMpegProcess.ExitCode, empty);
					}
					this.FFMpegProcess.Close();
					this.FFMpegProcess = null;
					if (this.ConvertProgress != null && convertProgressEventArg != null && convertProgressEventArg.Processed < convertProgressEventArg.TotalDuration)
					{
						this.ConvertProgress(this, new ConvertProgressEventArgs(convertProgressEventArg.TotalDuration, convertProgressEventArg.TotalDuration));
					}
					if (output.Filename == null)
					{
						using (FileStream fileStream1 = new FileStream(str, FileMode.Open, FileAccess.Read, FileShare.None))
						{
							this.CopyStream(fileStream1, output.DataStream, 262144);
						}
					}
				}
				catch (Exception exception)
				{
					this.EnsureFFMpegProcessStopped();
					throw;
				}
			}
			finally
			{
				if (filename != null && input.Filename == null && File.Exists(filename))
				{
					File.Delete(filename);
				}
				if (str != null && output.Filename == null && File.Exists(str))
				{
					File.Delete(str);
				}
			}
		}

		protected void CopyStream(Stream inputStream, Stream outputStream, int bufSize)
		{
			byte[] numArray = new byte[bufSize];
			while (true)
			{
				int num = inputStream.Read(numArray, 0, (int)numArray.Length);
				int num1 = num;
				if (num <= 0)
				{
					break;
				}
				outputStream.Write(numArray, 0, num1);
			}
		}

		protected void EnsureFFMpegLibs()
		{
            Assembly executingAssembly = Assembly.GetAssembly(typeof(NReco.VideoConverter.FFMpegConverter));
            string[] resourceFiles = executingAssembly.GetManifestResourceNames();
            string prefix = "NReco.VideoConverter.FFMpeg.";
            for (int i = 0; i < (int)resourceFiles.Length; i++)
            {
                string current = resourceFiles[i];
                var contains = current.IndexOf(prefix);
                if (contains >= 0)
                {
                    string withoutPrefix = current.Substring(prefix.Length + contains);
                    string fullPath = Path.Combine(this.FFMpegToolPath, Path.GetFileNameWithoutExtension(withoutPrefix));
                    lock (FFMpegConverter.globalObj)
                    {
                        if (!File.Exists(fullPath) || !(File.GetLastWriteTime(fullPath) > File.GetLastWriteTime(executingAssembly.Location)))
                        {
                            using (GZipStream gZipStream = new GZipStream(executingAssembly.GetManifestResourceStream(current), CompressionMode.Decompress, false))
                            {
                                using (FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    byte[] numArray = new byte[65536];
                                    while (true)
                                    {
                                        int num = gZipStream.Read(numArray, 0, (int)numArray.Length);
                                        int num1 = num;
                                        if (num <= 0)
                                        {
                                            break;
                                        }
                                        fileStream.Write(numArray, 0, num1);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

		protected void EnsureFFMpegProcessStopped()
		{
			if (this.FFMpegProcess != null && !this.FFMpegProcess.HasExited)
			{
				try
				{
					this.FFMpegProcess.Kill();
					this.FFMpegProcess = null;
				}
				catch (Exception exception)
				{
				}
			}
		}

		/// <summary>
		/// Extract video thumbnail (first frame) from local video file
		/// </summary>
		/// <param name="inputFile">path to local video file</param>
		/// <param name="outputJpegStream">output stream for thumbnail in jpeg format</param>
		public void GetVideoThumbnail(string inputFile, Stream outputJpegStream)
		{
			this.GetVideoThumbnail(inputFile, outputJpegStream, null);
		}

		/// <summary>
		/// Extract video thumbnail (first frame) from local video file
		/// </summary>
		/// <param name="inputFile">path to local video file</param>
		/// <param name="outputFile">path to thumbnail jpeg file</param>
		public void GetVideoThumbnail(string inputFile, string outputFile)
		{
			this.GetVideoThumbnail(inputFile, outputFile, null);
		}

		/// <summary>
		/// Extract video frame from local video file at specified position
		/// </summary>
		/// <param name="inputFile">path to local video file</param>
		/// <param name="outputJpegStream">output stream for thumbnail in jpeg format</param>
		/// <param name="frameTime">video position (in seconds)</param>
		public void GetVideoThumbnail(string inputFile, Stream outputJpegStream, float? frameTime)
		{
			Media medium = new Media()
			{
				Filename = inputFile
			};
			Media medium1 = new Media()
			{
				DataStream = outputJpegStream,
				Format = "mjpeg"
			};
			Media medium2 = medium1;
			ConvertSettings convertSetting = new ConvertSettings()
			{
				VideoFrameCount = new int?(1),
				Seek = frameTime,
				MaxDuration = new float?(1f)
			};
			this.ConvertMedia(medium, medium2, convertSetting);
		}

		/// <summary>
		/// Extract video frame from local video file at specified position
		/// </summary>
		/// <param name="inputFile">path to local video file</param>
		/// <param name="outputFile">path to thumbnail jpeg file</param>
		/// <param name="frameTime">video position (in seconds)</param>
		public void GetVideoThumbnail(string inputFile, string outputFile, float? frameTime)
		{
			Media medium = new Media()
			{
				Filename = inputFile
			};
			Media medium1 = new Media()
			{
				Filename = outputFile,
				Format = "mjpeg"
			};
			Media medium2 = medium1;
			ConvertSettings convertSetting = new ConvertSettings()
			{
				VideoFrameCount = new int?(1),
				Seek = frameTime,
				MaxDuration = new float?(1f)
			};
			this.ConvertMedia(medium, medium2, convertSetting);
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
				string str = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
				if (!File.Exists(str))
				{
					throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", str));
				}
				ProcessStartInfo processStartInfo = new ProcessStartInfo(str, ffmpegArgs)
				{
					WindowStyle = ProcessWindowStyle.Hidden,
					CreateNoWindow = true,
					UseShellExecute = false,
					WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath),
					RedirectStandardInput = false,
					RedirectStandardOutput = false,
					RedirectStandardError = true
				};
				if (this.FFMpegProcess != null)
				{
					throw new InvalidOperationException("FFMpeg process is already started");
				}
				this.FFMpegProcess = Process.Start(processStartInfo);
				if (this.FFMpegProcessPriority != ProcessPriorityClass.Normal)
				{
					this.FFMpegProcess.PriorityClass = this.FFMpegProcessPriority;
				}
				string empty = string.Empty;
				this.FFMpegProcess.ErrorDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
					if (args.Data == null)
					{
						return;
					}
					empty = args.Data;
					if (this.LogReceived != null)
					{
						this.LogReceived(this, new FFMpegLogEventArgs(args.Data));
					}
				});
				this.FFMpegProcess.BeginErrorReadLine();
				this.WaitFFMpegProcessForExit();
				if (this.FFMpegProcess.ExitCode != 0)
				{
					throw new FFMpegException(this.FFMpegProcess.ExitCode, empty);
				}
				this.FFMpegProcess.Close();
				this.FFMpegProcess = null;
			}
			catch (Exception exception)
			{
				this.EnsureFFMpegProcessStopped();
				throw;
			}
		}

		/// <summary>
		/// Stop FFMpeg process "softly" by sending CTRL+C signal to FFMpeg console. 
		/// This method doesn't stop FFMpeg process immediately and may take some time.
		/// </summary>
		/// <remarks>
		/// Stop method implementation uses native WinAPI calls (AttachConsole, GenerateConsoleCtrlEvent, FreeConsole and SetConsoleCtrlHandler).
		/// If AttachConsole fails (for console apps) special fallback is used that sends Ctrl+C by starting special SendCtrlC.exe process.
		/// </remarks>
		/// <returns>true if CTRL+C signal sent sucessfully and FFPeg process has exited. If this method returns false FFMpeg process should be stopped with Abort method.</returns>
		public bool Stop()
		{
			if (this.FFMpegProcess == null || this.FFMpegProcess.HasExited)
			{
				return true;
			}
			return ConsoleUtils.SendConsoleCtrlC(this.FFMpegProcess, this.FFMpegToolPath);
		}

		protected void WaitFFMpegProcessForExit()
		{
			if (!this.ExecutionTimeout.HasValue)
			{
				this.FFMpegProcess.WaitForExit();
			}
			else if (!this.FFMpegProcess.WaitForExit((int)this.ExecutionTimeout.Value.TotalMilliseconds))
			{
				this.EnsureFFMpegProcessStopped();
				throw new FFMpegException(-2, string.Format("FFMpeg process exceeded execution timeout ({0}) and was aborted", this.ExecutionTimeout));
			}
			if (this.FFMpegProcess == null)
			{
				throw new FFMpegException(-1, "FFMpeg process was aborted");
			}
		}

		/// <summary>
		/// Occurs when FFMpeg outputs media info (total duration, convert progress)
		/// </summary>
		public event EventHandler<ConvertProgressEventArgs> ConvertProgress;

        protected void OnConvertProgress(object sender, ConvertProgressEventArgs evt)
        {
            if (ConvertProgress != null)
                ConvertProgress(sender, evt);
        }

		/// <summary>
		/// Occurs when log line is received from FFMpeg process
		/// </summary>
		public event EventHandler<FFMpegLogEventArgs> LogReceived;

        protected void OnLogRecieved(object sender, FFMpegLogEventArgs evt)
        {
            if (LogReceived != null)
                LogReceived(sender, evt);
        }
	}
}