using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.FFmpeg
{
    internal static class Program
    {
        public static Process StartWatching(Process ffmpeg)
        {
            var me = Process.GetCurrentProcess();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Assembly.GetExecutingAssembly().Location;
            psi.Arguments = $"{me.Id} {ffmpeg.Id}"; // args will be [clowdPID, ffmpegPID]
            psi.UseShellExecute = false;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            return p;
        }

        public static int Main(string[] args)
        {
            // args will be [clowdPID, ffmpegPID]
            try
            {
                var clowdId = Convert.ToInt32(args[0]);
                var ffmpegId = Convert.ToInt32(args[1]);

                var clowd = Process.GetProcessById(clowdId);
                var ffmpeg = Process.GetProcessById(ffmpegId);

                Console.WriteLine($"[CLOWD] FFmpeg watch started successfully. Clowd PID: {clowd.Id}, FFmpeg PID: {ffmpeg.Id}");

                while (true)
                {
                    Thread.Sleep(1000);

                    if (ffmpeg.HasExited)
                    {
                        return 0;
                    }

                    if (clowd.HasExited)
                    {
                        ffmpeg.Kill();
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CLOWD] FFmpeg watch process failed. {Environment.NewLine}{e.ToString()}");
                return 1;
            }
        }
    }
}
