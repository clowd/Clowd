using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Benchmarks
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // --compare old.json new.json
            int ci = Array.IndexOf(args, "--compare");
            if (ci >= 0)
            {
                if (ci + 2 >= args.Length)
                {
                    Console.Error.WriteLine("usage: --compare <old.json> <new.json>");
                    return 2;
                }
                return ResultsIo.Compare(args[ci + 1], args[ci + 2]);
            }

            string outPath = ArgValue(args, "--out") ?? "results.json";
            var filter = ArgValue(args, "--filter")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                   .Select(x => x.ToUpperInvariant()).ToHashSet();

            // The harness source is identical across builds; detect which product it linked against.
            bool isNew = typeof(GraphicBase).Assembly.GetType("Clowd.Drawing.Rendering.SceneRenderer") != null;
            string build = isNew ? "new" : "old";
            bool saveGoldens = !isNew; // goldens are captured from the baseline (old) build in WP0

            string benchDir = FindBenchDir();
            string goldensDir = Path.Combine(benchDir, "baselines", "goldens");
            if (!Path.IsPathRooted(outPath)) outPath = Path.Combine(benchDir, outPath);

            Console.WriteLine($"Clowd.Drawing.Benchmarks  build={build}  out={outPath}");
            Console.WriteLine($"goldens={goldensDir}  saveGoldens={saveGoldens}");

            var all = new (string Id, Func<BenchSession, List<ScenarioResult>> Run)[]
            {
                ("S1", Scenarios.S1),
                ("S2", Scenarios.S2),
                ("S3", Scenarios.S3),
                ("S4", Scenarios.S4),
                ("S5", Scenarios.S5),
                ("S6", Scenarios.S6),
                ("S7", Scenarios.S7),
                ("S8", Scenarios.S8),
                ("S9", sess => Scenarios.S9(sess, goldensDir, saveGoldens)),
                ("S10", Scenarios.S10),
                ("S11", Scenarios.S11),
            };

            var results = new List<ScenarioResult>();
            using (var session = new BenchSession())
            {
                foreach (var scen in all)
                {
                    if (filter != null && !filter.Contains(scen.Id)) continue;
                    var sw = Stopwatch.StartNew();
                    Console.Write($"  {scen.Id} ... ");
                    try
                    {
                        var rows = scen.Run(session);
                        results.AddRange(rows);
                        sw.Stop();
                        Console.WriteLine($"done ({sw.Elapsed.TotalSeconds:F1}s, {rows.Count} row(s))");
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        Console.WriteLine($"ERROR after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
                        Console.WriteLine(ex);
                        var err = new ScenarioResult { Id = scen.Id, Name = "error", N = 0 };
                        err.Notes = "EXCEPTION: " + ex.Message;
                        results.Add(err);
                    }
                }
            }

            ResultsIo.Write(outPath, build, GitCommit(), results);
            Console.WriteLine($"wrote {results.Count} rows to {outPath}");
            return 0;
        }

        private static string ArgValue(string[] args, string key)
        {
            int i = Array.IndexOf(args, key);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }

        private static string FindBenchDir()
        {
            // walk up from the working dir / assembly location to the Benchmarks project folder
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var d = new DirectoryInfo(start);
                while (d != null)
                {
                    if (File.Exists(Path.Combine(d.FullName, "Clowd.Drawing.Benchmarks.csproj")))
                        return d.FullName;
                    d = d.Parent;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static string GitCommit()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return string.IsNullOrEmpty(outp) ? "unknown" : outp;
            }
            catch { return "unknown"; }
        }
    }
}
