using NReco.VideoConverter;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screeney
{
    internal sealed class FFMpegConverterEx : FFMpegConverter
    {
        public void MergeAudio(string[] inputAudioFiles, string outputFile)
        {
            //ffmpeg -i test2.mp3 -i test3.mp3 -filter_complex amix=inputs=2:duration=longest testASD.mp3
            string cmdArgs = String.Join(" ", inputAudioFiles.Select(i => $"-i \"{Path.GetFullPath(i)}\""))
                + $" -filter_complex amix=inputs={inputAudioFiles.Length}:duration=longest " + Path.GetFullPath(outputFile);
            PerformFFMpegTask(cmdArgs);
        }
        public void CompileVideo(string audioFile, string videoFile, string outputFile, OutputSettings settings)
        {
            StringBuilder sb = new StringBuilder();
            this.ComposeFFMpegOutputArgs(sb, null, settings);
            //ffmpeg - i test.avi - i test.wav - c:v libx264 -s hd720 - c:a libmp3lame -preset veryslow output.mp4
            string cmdArgs = $"-i \"{Path.GetFullPath(audioFile)}\" -i \"{Path.GetFullPath(videoFile)}\"{sb.ToString()}\"{Path.GetFullPath(outputFile)}\"";
            PerformFFMpegTask(cmdArgs);
        }

        private void PerformFFMpegTask(string commandArgs)
        {
            this.EnsureFFMpegLibs();
            string ffmpegPath = Path.Combine(this.FFMpegToolPath, this.FFMpegExeName);
            if (!File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException(string.Concat("Cannot find ffmpeg tool: ", ffmpegPath));
            }

            ProcessStartInfo processStartInfo = new ProcessStartInfo(ffmpegPath, string.Concat("-nostdin ", commandArgs))
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

            ConvertProgressEventArgs convertArgs = null;
            string errorData = string.Empty;

            this.FFMpegProcess.OutputDataReceived += new DataReceivedEventHandler((object o, DataReceivedEventArgs args) => { });
            this.FFMpegProcess.ErrorDataReceived += (s, args) =>
            {
                if (args.Data == null)
                {
                    return;
                }
                errorData = args.Data;
                TimeSpan duration = TimeSpan.Zero;
                System.Text.RegularExpressions.Match match = this.DurationRegex.Match(args.Data);
                if (match.Success && TimeSpan.TryParse(match.Groups["duration"].Value, out duration))
                {
                    convertArgs = new ConvertProgressEventArgs(TimeSpan.Zero, duration);
                    OnConvertProgress(this, convertArgs);
                }
                System.Text.RegularExpressions.Match match1 = this.ProgressRegex.Match(args.Data);
                if (match1.Success && duration != TimeSpan.Zero)
                {
                    TimeSpan progress = TimeSpan.Zero;
                    if (TimeSpan.TryParse(match1.Groups["progress"].Value, out progress))
                    {
                        convertArgs = new ConvertProgressEventArgs(progress, duration);
                        OnConvertProgress(this, convertArgs);
                    }
                }
                this.OnLogRecieved(this, new FFMpegLogEventArgs(args.Data));
            };

            this.FFMpegProcess.BeginOutputReadLine();
            this.FFMpegProcess.BeginErrorReadLine();
            this.WaitFFMpegProcessForExit();
            if (this.FFMpegProcess.ExitCode != 0)
            {
                throw new FFMpegException(this.FFMpegProcess.ExitCode, errorData);
            }
            this.FFMpegProcess.Close();
            this.FFMpegProcess = null;

            if (convertArgs != null && convertArgs.Processed < convertArgs.TotalDuration)
            {
                OnConvertProgress(this, new ConvertProgressEventArgs(convertArgs.TotalDuration, convertArgs.TotalDuration));
            }
        }
    }

}
