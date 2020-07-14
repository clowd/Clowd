// Decompiled with JetBrains decompiler
// Type: NReco.VideoConverter.ConvertLiveMediaTask
// Assembly: NReco.VideoConverter, Version=1.1.2.0, Culture=neutral, PublicKeyToken=395ccb334978a0cd
// MVID: 503557EA-2300-465E-B59C-87E1183E58F6
// Assembly location: C:\Users\Caelan\Downloads\video_converter_free\NReco.VideoConverter.dll

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NReco.VideoConverter
{
  /// <summary>Represents async live media conversion task.</summary>
  public class ConvertLiveMediaTask
  {
    private Stream Input;
    private Stream Output;
    private FFMpegConverter FFMpegConv;
    private string FFMpegToolArgs;
    private Process FFMpegProcess;
    private Thread CopyToStdInThread;
    private Thread CopyFromStdOutThread;
    public EventHandler OutputDataReceived;
    private string lastErrorLine;
    private FFMpegProgress ffmpegProgress;
    private long WriteBytesCount;
    private Exception lastStreamException;

    internal ConvertLiveMediaTask(
      FFMpegConverter ffmpegConv,
      string ffMpegArgs,
      Stream inputStream,
      Stream outputStream,
      FFMpegProgress progress)
    {
      this.Input = inputStream;
      this.Output = outputStream;
      this.FFMpegConv = ffmpegConv;
      this.FFMpegToolArgs = ffMpegArgs;
      this.ffmpegProgress = progress;
    }

    /// <summary>Start live stream conversion</summary>
    public void Start()
    {
      this.lastStreamException = (Exception) null;
      string ffMpegExePath = this.FFMpegConv.GetFFMpegExePath();
      if (!File.Exists(ffMpegExePath))
        throw new FileNotFoundException("Cannot find ffmpeg tool: " + ffMpegExePath);
      ProcessStartInfo startInfo = new ProcessStartInfo(ffMpegExePath, "-stdin " + this.FFMpegToolArgs);
      startInfo.WindowStyle = ProcessWindowStyle.Hidden;
      startInfo.CreateNoWindow = true;
      startInfo.UseShellExecute = false;
      startInfo.WorkingDirectory = Path.GetDirectoryName(ffMpegExePath);
      startInfo.RedirectStandardInput = true;
      startInfo.RedirectStandardOutput = true;
      startInfo.RedirectStandardError = true;
      startInfo.StandardOutputEncoding = Encoding.Default;
      this.FFMpegConv.InitStartInfo(startInfo);
      this.FFMpegProcess = Process.Start(startInfo);
      if (this.FFMpegConv.FFMpegProcessPriority != ProcessPriorityClass.Normal)
        this.FFMpegProcess.PriorityClass = this.FFMpegConv.FFMpegProcessPriority;
      this.lastErrorLine = (string) null;
      this.ffmpegProgress.Reset();
      this.FFMpegProcess.ErrorDataReceived += (DataReceivedEventHandler) ((o, args) =>
      {
        if (args.Data == null)
          return;
        this.lastErrorLine = args.Data;
        this.ffmpegProgress.ParseLine(args.Data);
        this.FFMpegConv.FFMpegLogHandler(args.Data);
      });
      this.FFMpegProcess.BeginErrorReadLine();
      if (this.Input != null)
      {
        this.CopyToStdInThread = new Thread(new ThreadStart(this.CopyToStdIn));
        this.CopyToStdInThread.Start();
      }
      else
        this.CopyToStdInThread = (Thread) null;
      if (this.Output != null)
      {
        this.CopyFromStdOutThread = new Thread(new ThreadStart(this.CopyFromStdOut));
        this.CopyFromStdOutThread.Start();
      }
      else
        this.CopyFromStdOutThread = (Thread) null;
    }

    /// <summary>Write input data into conversion stream</summary>
    public void Write(byte[] buf, int offset, int count)
    {
      if (this.FFMpegProcess.HasExited)
      {
        if (this.FFMpegProcess.ExitCode != 0)
          throw new FFMpegException(this.FFMpegProcess.ExitCode, string.IsNullOrEmpty(this.lastErrorLine) ? "FFMpeg process has exited" : this.lastErrorLine);
        throw new FFMpegException(-1, "FFMpeg process has exited");
      }
      this.FFMpegProcess.StandardInput.BaseStream.Write(buf, offset, count);
      this.FFMpegProcess.StandardInput.BaseStream.Flush();
      this.WriteBytesCount += (long) count;
    }

    /// <summary>Stop live stream conversion process</summary>
    public void Stop()
    {
      this.Stop(false);
    }

    /// <summary>
    /// Stop live stream conversion process and optionally force ffmpeg to quit
    /// </summary>
    /// <param name="forceFFMpegQuit">force FFMpeg to quit by sending 'q' command to stdin.</param>
    public void Stop(bool forceFFMpegQuit)
    {
      if (this.CopyToStdInThread != null)
        this.CopyToStdInThread = (Thread) null;
      if (forceFFMpegQuit)
      {
        if (this.Input == null && this.WriteBytesCount == 0L)
        {
          this.FFMpegProcess.StandardInput.WriteLine("q\n");
          this.FFMpegProcess.StandardInput.Close();
        }
        else
          this.Abort();
      }
      else
        this.FFMpegProcess.StandardInput.BaseStream.Close();
      this.Wait();
    }

    private void OnStreamError(Exception ex, bool isStdinStdout)
    {
      if (ex is IOException && isStdinStdout)
        return;
      this.lastStreamException = ex;
      this.Abort();
    }

    protected void CopyToStdIn()
    {
      byte[] buffer = new byte[65536];
      Thread copyToStdInThread = this.CopyToStdInThread;
      Process ffMpegProcess = this.FFMpegProcess;
      Stream baseStream = this.FFMpegProcess.StandardInput.BaseStream;
      while (true)
      {
        int count;
        try
        {
          count = this.Input.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
          this.OnStreamError(ex, false);
          break;
        }
        if (count > 0)
        {
          if (this.FFMpegProcess != null && object.ReferenceEquals((object) copyToStdInThread, (object) this.CopyToStdInThread))
          {
            if (object.ReferenceEquals((object) ffMpegProcess, (object) this.FFMpegProcess))
            {
              try
              {
                baseStream.Write(buffer, 0, count);
                baseStream.Flush();
              }
              catch (Exception ex)
              {
                this.OnStreamError(ex, true);
                break;
              }
            }
            else
              goto label_10;
          }
          else
            break;
        }
        else
          goto label_8;
      }
      return;
label_10:
      return;
label_8:
      this.FFMpegProcess.StandardInput.Close();
    }

    protected void CopyFromStdOut()
    {
      byte[] buffer = new byte[65536];
      Thread fromStdOutThread = this.CopyFromStdOutThread;
      Stream baseStream = this.FFMpegProcess.StandardOutput.BaseStream;
      while (object.ReferenceEquals((object) fromStdOutThread, (object) this.CopyFromStdOutThread))
      {
        int count;
        try
        {
          count = baseStream.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
          this.OnStreamError(ex, true);
          break;
        }
        if (count > 0)
        {
          if (!object.ReferenceEquals((object) fromStdOutThread, (object) this.CopyFromStdOutThread))
            break;
          try
          {
            this.Output.Write(buffer, 0, count);
            this.Output.Flush();
          }
          catch (Exception ex)
          {
            this.OnStreamError(ex, false);
            break;
          }
          if (this.OutputDataReceived != null)
            this.OutputDataReceived((object) this, EventArgs.Empty);
        }
        else
          Thread.Sleep(30);
      }
    }

    /// <summary>
    /// Wait until live stream conversion is finished (input stream ended)
    /// </summary>
    /// <remarks>
    /// Do not call "Wait" when input stream is not used and input data is provided using Write method
    /// </remarks>
    public void Wait()
    {
      this.FFMpegProcess.WaitForExit(int.MaxValue);
      if (this.CopyToStdInThread != null)
        this.CopyToStdInThread = (Thread) null;
      if (this.CopyFromStdOutThread != null)
        this.CopyFromStdOutThread = (Thread) null;
      if (this.FFMpegProcess.ExitCode != 0)
        throw new FFMpegException(this.FFMpegProcess.ExitCode, this.lastErrorLine ?? "Unknown error");
      if (this.lastStreamException != null)
        throw new IOException(this.lastStreamException.Message, this.lastStreamException);
      this.FFMpegProcess.Close();
      this.ffmpegProgress.Complete();
    }

    /// <summary>Abort live stream conversions process</summary>
    public void Abort()
    {
      if (this.CopyToStdInThread != null)
        this.CopyToStdInThread = (Thread) null;
      if (this.CopyFromStdOutThread != null)
        this.CopyFromStdOutThread = (Thread) null;
      try
      {
        this.FFMpegProcess.Kill();
      }
      catch (InvalidOperationException ex)
      {
      }
    }

    internal class StreamOperationContext
    {
      private bool isInput;
      private bool isRead;

      public Stream TargetStream { get; private set; }

      public bool Read
      {
        get
        {
          return this.isRead;
        }
      }

      public bool Write
      {
        get
        {
          return !this.isRead;
        }
      }

      public bool IsInput
      {
        get
        {
          return this.isInput;
        }
      }

      public bool IsOutput
      {
        get
        {
          return !this.isInput;
        }
      }

      internal StreamOperationContext(Stream stream, bool isInput, bool isRead)
      {
        this.TargetStream = stream;
        this.isInput = isInput;
        this.isRead = isRead;
      }
    }
  }
}
