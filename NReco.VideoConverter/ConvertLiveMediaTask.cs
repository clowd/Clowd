using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Represents async live media task conversion
	/// </summary>
	public class ConvertLiveMediaTask
	{
		private Stream Input;

		private Stream Output;

		private string FFMpegToolPath;

		private string FFMpegToolArgs;

		private Process FFMpegProcess;

		private Thread CopyToStdInThread;

		private Thread CopyFromStdOutThread;

		public EventHandler OutputDataReceived;

		private string lastErrorLine;

		internal ConvertLiveMediaTask(string ffMpegToolPath, string ffMpegArgs, Stream inputStream, Stream outputStream)
		{
			this.Input = inputStream;
			this.Output = outputStream;
			this.FFMpegToolPath = ffMpegToolPath;
			this.FFMpegToolArgs = ffMpegArgs;
		}

		/// <summary>
		/// Abort live stream conversions process
		/// </summary>
		public void Abort()
		{
			if (this.CopyToStdInThread != null)
			{
				this.CopyToStdInThread.Abort();
			}
			if (this.CopyFromStdOutThread != null)
			{
				this.CopyFromStdOutThread.Abort();
			}
			this.FFMpegProcess.Kill();
		}

		protected void CopyFromStdOut()
		{
			byte[] numArray = new byte[32768];
			while (!this.FFMpegProcess.HasExited)
			{
				int num = this.FFMpegProcess.StandardOutput.BaseStream.Read(numArray, 0, (int)numArray.Length);
				if (num <= 0)
				{
					Thread.Sleep(30);
				}
				else
				{
					this.Output.Write(numArray, 0, num);
					this.Output.Flush();
					if (this.OutputDataReceived == null)
					{
						continue;
					}
					this.OutputDataReceived(this, EventArgs.Empty);
				}
			}
		}

		protected void CopyToStdIn()
		{
			byte[] numArray = new byte[8192];
			while (true)
			{
				int num = this.Input.Read(numArray, 0, (int)numArray.Length);
				if (num <= 0)
				{
					break;
				}
				this.FFMpegProcess.StandardInput.BaseStream.Write(numArray, 0, num);
				this.FFMpegProcess.StandardInput.BaseStream.Flush();
			}
			this.FFMpegProcess.StandardInput.Close();
		}

		/// <summary>
		/// Start live stream conversion
		/// </summary>
		public void Start()
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo(this.FFMpegToolPath, string.Concat("-stdin ", this.FFMpegToolArgs))
			{
				WindowStyle = ProcessWindowStyle.Hidden,
				CreateNoWindow = true,
				UseShellExecute = false,
				WorkingDirectory = Path.GetDirectoryName(this.FFMpegToolPath),
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.Default
			};
			this.FFMpegProcess = Process.Start(processStartInfo);
			this.lastErrorLine = null;
			this.FFMpegProcess.ErrorDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => {
				if (args.Data == null)
				{
					return;
				}
				this.lastErrorLine = args.Data;
			});
			this.FFMpegProcess.BeginErrorReadLine();
			if (this.Input == null)
			{
				this.CopyToStdInThread = null;
			}
			else
			{
				this.CopyToStdInThread = new Thread(new ThreadStart(this.CopyToStdIn));
				this.CopyToStdInThread.Start();
			}
			if (this.Output == null)
			{
				this.CopyFromStdOutThread = null;
				return;
			}
			this.CopyFromStdOutThread = new Thread(new ThreadStart(this.CopyFromStdOut));
			this.CopyFromStdOutThread.Start();
		}

		/// <summary>
		/// Stop live stream conversion process
		/// </summary>
		public void Stop()
		{
			this.Stop(false);
		}

		/// <summary>
		/// Stop live stream conversion process and optionally force ffmpeg to quit
		/// </summary>
		/// <param name="forceFFMpegQuit">force FFMpeg to quit by sending Ctrl+C signal</param>
		public void Stop(bool forceFFMpegQuit)
		{
			if (this.CopyToStdInThread != null)
			{
				this.CopyToStdInThread.Abort();
			}
			this.FFMpegProcess.StandardInput.BaseStream.Close();
			if (forceFFMpegQuit && !ConsoleUtils.SendConsoleCtrlC(this.FFMpegProcess, this.FFMpegToolPath))
			{
				this.Abort();
			}
			this.Wait();
		}

		/// <summary>
		/// Wait until live stream conversion is finished (input stream ended)
		/// </summary>
		/// <remarks>
		/// Do not call "Wait" when input stream is not used and input data is provided using Write method
		/// </remarks>
		public void Wait()
		{
			this.FFMpegProcess.WaitForExit();
			if (this.CopyFromStdOutThread != null)
			{
				this.CopyFromStdOutThread.Abort();
			}
			if (this.FFMpegProcess.ExitCode != 0)
			{
				throw new FFMpegException(this.FFMpegProcess.ExitCode, this.lastErrorLine ?? "Unknown error");
			}
			this.FFMpegProcess.Close();
		}

		/// <summary>
		/// Write input data into conversion stream
		/// </summary>
		public void Write(byte[] buf, int offset, int count)
		{
			this.FFMpegProcess.StandardInput.BaseStream.Write(buf, offset, count);
			this.FFMpegProcess.StandardInput.BaseStream.Flush();
		}
	}
}