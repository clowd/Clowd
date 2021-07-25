using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.IO;

class Program
{
    static string MyExeName { get; } = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

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
        Console.WriteLine($"[{MyExeName}] [{DateTime.Now.ToShortTimeString()}] {txt}");
    }

    static int Main(string[] args)
    {
        try
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
                return 0;
            }

            Write($"Starting; Owner process: {parent.Id}, children: {String.Join(", ", children.Select(s => s.Id))}");

            while (true)
            {
                Thread.Sleep(1000);

                if (children.All(w => w.HasExited))
                {
                    Write("Exiting; All watched processes have exited.");
                    return 0;
                }

                if (parent.HasExited)
                {
                    Write("Parent has exited watched processes still exist, killing...");
                    foreach (var p in children)
                        if (!p.HasExited)
                            p.Kill();
                }
            }
            return 0;
        }
        catch (Exception e)
        {
            var clr = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Write($"Exiting with error: {e.Message}");
            Console.WriteLine();
            Console.ForegroundColor = clr;
            Console.WriteLine($"Usage instructions:  '{MyExeName} parentPid childPid [otherChildPid ...]'");
            return 1;
        }
    }
}
