using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clowd.Aot
{
    static class Watch
    {
        static Process GetProcessByIdSafe(int id)
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

        static void Write(string txt)
        {
            Console.WriteLine($"[Clowd.Watch] [{DateTime.Now.ToShortTimeString()}] {txt}");
        }

        public static void Run(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("Must provide at least two arguments.");

            // parse all arguments to int (safely: don't want to throw system exception due to compiler being used)
            List<int> pids = new List<int>();
            foreach (var t in args)
            {
                if (Int32.TryParse(t, out var i))
                {
                    pids.Add(i);
                }
                else
                {
                    throw new ArgumentException("Each argument must be a valid integer.");
                }
            }

            Process parent = GetProcessByIdSafe(pids[0]);
            if (parent == null || parent.HasExited)
                throw new InvalidOperationException("parentPid must point to a running process.");

            var children = pids
                .Skip(1)
                .Select(GetProcessByIdSafe)
                .Where(p => p != null)
                .Where(p => !p.HasExited)
                .ToArray();

            if (!children.Any())
            {
                Write("Exiting; There are no valid/running children processes to watch");
                return;
            }

            Write($"Starting; Owner process: {parent.Id}, children: {String.Join(", ", children.Select(s => s.Id))}");

            while (true)
            {
                Thread.Sleep(1000);

                if (children.All(w => w.HasExited))
                {
                    Write("Exiting; All watched processes have exited.");
                    return;
                }

                if (parent.HasExited)
                {
                    Write("Parent has exited watched processes still exist, killing...");
                    foreach (var p in children)
                        if (!p.HasExited)
                            p.Kill();
                }
            }
        }
    }
}
