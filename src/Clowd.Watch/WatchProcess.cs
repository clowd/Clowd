using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

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

        public EventHandler<WatchLogEventArgs> OutputReceived;

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
                    OutputReceived?.Invoke(this, new WatchLogEventArgs(p, useName, e.Data, false));
                };

                p.ErrorDataReceived += (s, e) =>
                {
                    var useName = _namecache.TryGetValue(p.Id, out var pname) ? pname : ("pid." + p.Id.ToString());
                    OutputReceived?.Invoke(this, new WatchLogEventArgs(p, useName, e.Data, true));
                };

                p.Exited += (s, e) =>
                {
                    var useName = _namecache.TryGetValue(p.Id, out var pname) ? pname : ("pid." + p.Id.ToString());
                    OutputReceived?.Invoke(this, new WatchLogEventArgs(p, useName, $"Process exited - code: {p.ExitCode}", true));
                };

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }

            startlog(watcher);
            foreach (var p in other)
                startlog(p);
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
            var me = Process.GetCurrentProcess();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Assembly.GetExecutingAssembly().Location;
            psi.Arguments = $"{me.Id} " + String.Join(" ", watchIds.Select(i => i.ToString())); // args will be [clowdPID, ffmpegPID, etcPID]
            psi.UseShellExecute = false;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            return p;
        }

        private static Process GetProcessByIdSafe(int id)
        {
            try
            {
                return Process.GetProcessById(id);
            }
            catch
            {
                return null;
            }
        }

        internal static int Main(string[] args)
        {
            // args will be [clowdPID, ffmpegPID, ...]
            try
            {
                var clowdId = Convert.ToInt32(args[0]);
                var watchIds = args
                    .Skip(1)
                    .Select(i => Convert.ToInt32(i))
                    .Select(GetProcessByIdSafe)
                    .Where(p => p != null)
                    .ToArray();

                if (!watchIds.Any())
                    return 0;

                var clowd = Process.GetProcessById(clowdId);

                Console.WriteLine($"[Clowd.Watch] watch started successfully. Clowd PID: {clowd.Id}, Watching PID's: {String.Join(", ", watchIds.Select(s => s.Id))}");

                while (true)
                {
                    Thread.Sleep(1000);

                    if (watchIds.All(w => w.HasExited))
                    {
                        Console.WriteLine("[Clowd.Watch] All watched processes have exited.");
                        return 0;
                    }

                    if (clowd.HasExited)
                    {
                        Console.WriteLine("[Clowd.Watch] Clowd has exited, watched processes still exist, killing...");
                        foreach (var p in watchIds)
                            if (!p.HasExited)
                                p.Kill();
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Clowd.Watch] watch process failed. {Environment.NewLine}{e.ToString()}");
                return 1;
            }
        }
    }
}
