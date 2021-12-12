using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Clowd
{
    public class WatchLogEventArgs
    {
        internal WatchLogEventArgs(Process process, string name, string data, bool error)
        {
            Process = process;
            ProcessName = name;
            Data = data;
            IsError = error;
        }

        public Process Process { get; }
        public string ProcessName { get; }
        public string Data { get; }
        public bool IsError { get; }
    }

    public class WatchProcess
    {
        private readonly Process _watcher;
        private readonly Process[] _other;
        private Dictionary<int, string> _namecache = new Dictionary<int, string>();

        public event EventHandler<WatchLogEventArgs> OutputReceived;

        public bool IsRunning => !_watcher.HasExited;

        public void ForceExit()
        {
            if (!_watcher.HasExited)
            {
                foreach (var p in _other)
                    KillPid(p);
                KillPid(_watcher);
            }
        }

        public void WaitTimeoutThenForceExit(int timeoutMs)
        {
            if (!_watcher.HasExited)
            {
                _watcher.WaitForExit(timeoutMs);
                ForceExit();
            }
        }

        public WatchProcess(Process watcher, Process[] other)
        {
            this._watcher = watcher;
            this._other = other;

            void startlog(Process p)
            {
                _namecache[p.Id] = p.ProcessName;

                p.OutputDataReceived += (s, e) =>
                {
                    var useName = _namecache.TryGetValue(p.Id, out var pname) ? pname : ("pid." + p.Id.ToString());
                    RaiseOutputRecieved(new WatchLogEventArgs(p, useName, e.Data, false));
                };

                p.ErrorDataReceived += (s, e) =>
                {
                    var useName = _namecache.TryGetValue(p.Id, out var pname) ? pname : ("pid." + p.Id.ToString());
                    RaiseOutputRecieved(new WatchLogEventArgs(p, useName, e.Data, true));
                };

                p.Exited += (s, e) =>
                {
                    var useName = _namecache.TryGetValue(p.Id, out var pname) ? pname : ("pid." + p.Id.ToString());
                    RaiseOutputRecieved(new WatchLogEventArgs(p, useName, $"Process exited - code: {p.ExitCode}", true));
                };

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }

            startlog(watcher);
            foreach (var p in other)
                startlog(p);
        }

        private void RaiseOutputRecieved(WatchLogEventArgs args)
        {
            OutputReceived?.Invoke(this, args);
        }

        public static WatchProcess StartAndWatch(params ProcessStartInfo[] psis)
        {
            List<Process> started = new List<Process>();
            Exception error = null;
            foreach (var i in psis)
            {
                try
                {
                    started.Add(Process.Start(i));
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }
            }

            if (error != null)
            {
                foreach (var p in started)
                    KillPid(p);
                throw error;
            }

            var watcher = Watch(started.Select(p => p.Id).ToArray());
            return new WatchProcess(watcher, started.ToArray());
        }


        private static void KillPid(Process pid)
        {
            if (pid.HasExited)
                return;
            KillPid(pid.Id);
        }

        private static void KillPid(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill";
                psi.Arguments = $"/F /PID {pid}";
                psi.UseShellExecute = false;
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                p.WaitForExit();
            }
            catch { }
        }

        public static Process Watch(params int[] watchIds)
        {
            var watchExePath = Path.Combine(AppContext.BaseDirectory, "clowd.exe");
            if (!File.Exists(watchExePath))
                throw new FileNotFoundException("Could not find 'clowd.exe', ensure it's in the application directory.");

            var me = Process.GetCurrentProcess();

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = watchExePath;
            psi.Arguments = $"watch {me.Id} " + String.Join(" ", watchIds.Select(i => i.ToString())); // args will be [watch, clowdPID, ffmpegPID, etcPID]
            psi.UseShellExecute = false;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            return Process.Start(psi);
        }
    }
}
