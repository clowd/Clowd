using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Clowd.Drawing.Benchmarks
{
    // The headless application the whole run lives inside. Mirrors
    // Clowd.Drawing.Tests/TestAppBuilder.cs exactly (real Skia — text shaping, geometry, RTB).
    // HeadlessUnitTestSession.StartNew(typeof(BenchApp)) discovers this BuildAvaloniaApp method and
    // uses it to build the AppBuilder; WITHOUT it the session falls back to a bare Application with
    // headless drawing enabled (no Skia RTB / frame capture).
    public class BenchApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<BenchApp>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// <summary>
    /// Owns the single long-lived <see cref="HeadlessUnitTestSession"/> and the timing / allocation
    /// measurement discipline described in benchmark-spec.md §1.4. Every scenario body runs on the
    /// dispatcher thread via <see cref="Run{T}"/> so canvas work happens exactly like an
    /// <c>[AvaloniaFact]</c> test.
    /// </summary>
    public sealed class BenchSession : IDisposable
    {
        private readonly HeadlessUnitTestSession _session;

        public BenchSession()
        {
            _session = HeadlessUnitTestSession.StartNew(typeof(BenchApp));
            // DrawingCanvas reads tool settings through SettingsRoot.Current; the app assigns it at
            // startup, so seed a defaults instance (same as the tests).
            Run(() =>
            {
                Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
                return true;
            });
        }

        /// <summary>Runs <paramref name="f"/> on the dispatcher thread and returns its result.</summary>
        public T Run<T>(Func<T> f) => _session.Dispatch(f, CancellationToken.None).GetAwaiter().GetResult();

        public void Run(Action a) => Run(() => { a(); return true; });

        public void Dispose() => _session.Dispose();

        // ---- dispatcher helpers (call from inside Run) --------------------------------------

        /// <summary>Drain all pending dispatcher work (frame-validator posts, timers, etc.).</summary>
        public static void RunJobs() => Dispatcher.UIThread.RunJobs();

        /// <summary>Force one render timer tick (drives the invalidate→record path).</summary>
        public static void RenderTick() => AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

        // ---- measurement --------------------------------------------------------------------

        public sealed class Sample
        {
            public double WallMs;
            public long AllocBytes;
            public int Gen0;
        }

        public static void GcClean()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        /// <summary>
        /// Runs <paramref name="op"/> for <paramref name="warmup"/> warmup + <paramref name="iters"/>
        /// measured iterations, GC-cleaning between each, capturing wall ms / allocated bytes / Gen0
        /// per measured iteration. Must be invoked on the dispatcher thread (inside <see cref="Run{T}"/>).
        /// </summary>
        public static List<Sample> Measure(int warmup, int iters, Action op)
        {
            for (int i = 0; i < warmup; i++)
            {
                op();
                GcClean();
            }

            var samples = new List<Sample>(iters);
            for (int i = 0; i < iters; i++)
            {
                GcClean();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                int g0 = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                op();
                sw.Stop();
                samples.Add(new Sample
                {
                    WallMs = sw.Elapsed.TotalMilliseconds,
                    AllocBytes = GC.GetAllocatedBytesForCurrentThread() - a0,
                    Gen0 = GC.CollectionCount(0) - g0,
                });
            }
            return samples;
        }

        // ---- statistics ---------------------------------------------------------------------

        public static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            if (v.Length == 0) return 0;
            int mid = v.Length / 2;
            return v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
        }

        public static double P95(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            if (v.Length == 0) return 0;
            int idx = (int)Math.Ceiling(0.95 * v.Length) - 1;
            idx = Math.Clamp(idx, 0, v.Length - 1);
            return v[idx];
        }

        public static double MedianWallMs(IEnumerable<Sample> s) => Median(s.Select(x => x.WallMs));
        public static double P95WallMs(IEnumerable<Sample> s) => P95(s.Select(x => x.WallMs));
        public static double MedianAllocBytes(IEnumerable<Sample> s) => Median(s.Select(x => (double)x.AllocBytes));
        public static double MedianGen0(IEnumerable<Sample> s) => Median(s.Select(x => (double)x.Gen0));
    }
}
