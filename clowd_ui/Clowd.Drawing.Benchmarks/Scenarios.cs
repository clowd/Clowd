using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Benchmarks
{
    /// <summary>
    /// S1..S11 implementations (benchmark-spec.md §3). Each method runs entirely on the dispatcher
    /// thread (it is invoked via <see cref="BenchSession.Run{T}"/>) and returns the result row(s).
    /// Nothing here references API that does not exist on both the old and new builds (§5); the two
    /// build-specific probes (S7 shadow bake, S8 FlushPendingState) go through reflection.
    /// </summary>
    public static class Scenarios
    {
        private const int Warmup = 2;
        private const int Iters = 7;

        private static readonly Color[] Pal =
        {
            Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold, Colors.Yellow, Colors.Lime,
            Colors.Green, Colors.Teal, Colors.Cyan, Colors.DodgerBlue, Colors.Blue, Colors.Indigo,
            Colors.Purple, Colors.Magenta, Colors.HotPink, Colors.Black,
        };

        private static GraphicRectangle FirstRect(DrawingCanvas c)
        {
            for (int i = 0; i < c.GraphicsList.Count; i++)
                if (c.GraphicsList[i] is GraphicRectangle r && c.GraphicsList[i] is not GraphicPolyLine)
                    return r;
            throw new InvalidOperationException("no rectangle in fixture");
        }

        // =====================================================================================
        // S1 commit_scrub
        // =====================================================================================
        public static List<ScenarioResult> S1(BenchSession s)
        {
            var rows = new List<ScenarioResult>();
            double per200 = double.NaN, per1000 = double.NaN;
            foreach (int n in new[] { 10, 50, 200, 1000 })
            {
                var row = s.Run(() =>
                {
                    var canvas = FixtureBuilder.BuildDoc(n);
                    var rect = FirstRect(canvas);
                    rect.IsSelected = true;
                    BenchSession.RunJobs();

                    int i = 0;
                    var samples = BenchSession.Measure(Warmup, Iters, () =>
                    {
                        for (int k = 0; k < 500; k++)
                        {
                            rect.ObjectColor = Pal[i++ % Pal.Length];
                            canvas.AddCommandToHistory(true);
                        }
                    });

                    double totalMs = BenchSession.MedianWallMs(samples);
                    var r = new ScenarioResult { Id = "S1", Name = "commit_scrub", N = n, Warmup = Warmup, Iterations = Iters };
                    r.Metrics["wallMsTotalMedian"] = totalMs;
                    r.Metrics["wallMsTotalP95"] = BenchSession.P95WallMs(samples);
                    r.Metrics["wallUsPerCommitMedian"] = totalMs / 500.0 * 1000.0;
                    r.Metrics["allocBytesPerCommitMedian"] = BenchSession.MedianAllocBytes(samples) / 500.0;
                    r.Metrics["gen0PerIteration"] = BenchSession.MedianGen0(samples);
                    return r;
                });
                if (n == 200) per200 = row.Metrics["wallUsPerCommitMedian"];
                if (n == 1000) per1000 = row.Metrics["wallUsPerCommitMedian"];
                rows.Add(row);
            }
            var r200 = rows.First(r => r.N == 200);
            r200.Counters["scaleRatio1000v200"] = per1000 / per200;
            return rows;
        }

        // =====================================================================================
        // S2 nudge_scrub
        // =====================================================================================
        public static List<ScenarioResult> S2(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildDoc(200);
                int selected = 0;
                for (int i = 0; i < canvas.GraphicsList.Count && selected < 10; i++)
                    if (canvas.GraphicsList[i] is GraphicRectangle && canvas.GraphicsList[i] is not GraphicPolyLine)
                    {
                        canvas.GraphicsList[i].IsSelected = true;
                        selected++;
                    }
                BenchSession.RunJobs();

                var samples = BenchSession.Measure(Warmup, Iters, () =>
                {
                    for (int k = 0; k < 200; k++)
                        canvas.Nudge(1, 0);
                });

                double totalMs = BenchSession.MedianWallMs(samples);
                var r = new ScenarioResult { Id = "S2", Name = "nudge_scrub", N = 200, Warmup = Warmup, Iterations = Iters };
                r.Metrics["wallMsTotalMedian"] = totalMs;
                r.Metrics["wallMsTotalP95"] = BenchSession.P95WallMs(samples);
                r.Metrics["wallUsPerOpMedian"] = totalMs / 200.0 * 1000.0;
                r.Metrics["allocBytesPerOpMedian"] = BenchSession.MedianAllocBytes(samples) / 200.0;
                r.Metrics["gen0PerIteration"] = BenchSession.MedianGen0(samples);
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S3 undo_redo
        // =====================================================================================
        public static List<ScenarioResult> S3(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildImageDoc(200);
                var rect = FirstRect(canvas);
                rect.IsSelected = true;

                // 90 field-edit commits (alternating move / color) + 10 z-order commits = 100 steps.
                for (int k = 0; k < 90; k++)
                {
                    if (k % 2 == 0) rect.Move(1, 0);
                    else rect.ObjectColor = Pal[k % Pal.Length];
                    canvas.AddCommandToHistory(false);
                }
                rect.IsSelected = false;
                var zTarget = canvas.GraphicsList[canvas.GraphicsList.Count / 2];
                for (int k = 0; k < 10; k++)
                {
                    zTarget.IsSelected = true;
                    if (k % 2 == 0) canvas.MoveToBack(); else canvas.MoveToFront();
                    zTarget.IsSelected = false;
                }
                BenchSession.RunJobs();

                var listBefore = canvas.GraphicsList;
                var untouched = canvas.GraphicsList[canvas.GraphicsList.Count - 1]; // last, never edited

                var samples = BenchSession.Measure(Warmup, Iters, () =>
                {
                    for (int k = 0; k < 100; k++)
                    {
                        canvas.Undo();
                        if (k % 10 == 9) BenchSession.RunJobs();
                    }
                    for (int k = 0; k < 100; k++)
                    {
                        canvas.Redo();
                        if (k % 10 == 9) BenchSession.RunJobs();
                    }
                });

                bool listIdentity = ReferenceEquals(canvas.GraphicsList, listBefore);
                bool instanceIdentity = canvas.GraphicsList.Contains(untouched);

                double totalMs = BenchSession.MedianWallMs(samples);
                var r = new ScenarioResult { Id = "S3", Name = "undo_redo", N = 200, Warmup = Warmup, Iterations = Iters };
                r.Metrics["wallMsPerOpMedian"] = totalMs / 200.0;
                r.Metrics["wallMsTotalMedian"] = totalMs;
                r.Metrics["allocBytesPerOpMedian"] = BenchSession.MedianAllocBytes(samples) / 200.0;
                r.Counters["graphicsListIdentityPreserved"] = listIdentity;
                r.Counters["instanceIdentityPreserved"] = instanceIdentity;
                r.Notes = "identity counters are informational on the old build (recorded FALSE), hard pass on new";
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S4 drag_funnel
        // =====================================================================================
        public static List<ScenarioResult> S4(BenchSession s)
        {
            var rows = new List<ScenarioResult>();
            double per200 = double.NaN, per1000 = double.NaN;
            foreach (int n in new[] { 10, 50, 200, 1000 })
            {
                var row = s.Run(() =>
                {
                    var canvas = FixtureBuilder.BuildDoc(n);
                    var rect = FirstRect(canvas);
                    rect.IsSelected = true;
                    BenchSession.RunJobs();

                    var samples = BenchSession.Measure(Warmup, Iters, () =>
                    {
                        for (int k = 0; k < 1000; k++)
                        {
                            rect.Move(1, 1);
                            if (k % 16 == 15)
                            {
                                BenchSession.RunJobs();
                                BenchSession.RenderTick();
                            }
                        }
                    });

                    double totalMs = BenchSession.MedianWallMs(samples);
                    var r = new ScenarioResult { Id = "S4", Name = "drag_funnel", N = n, Warmup = Warmup, Iterations = Iters };
                    r.Metrics["wallMsTotalMedian"] = totalMs;
                    r.Metrics["wallUsPerMoveMedian"] = totalMs / 1000.0 * 1000.0;
                    r.Metrics["allocBytesPerMoveMedian"] = BenchSession.MedianAllocBytes(samples) / 1000.0;
                    r.Metrics["gen0PerIteration"] = BenchSession.MedianGen0(samples);
                    return r;
                });
                if (n == 200) per200 = row.Metrics["wallUsPerMoveMedian"];
                if (n == 1000) per1000 = row.Metrics["wallUsPerMoveMedian"];
                rows.Add(row);
            }
            rows.First(r => r.N == 200).Counters["scaleRatio1000v200"] = per1000 / per200;
            return rows;
        }

        // =====================================================================================
        // S5 hover_hittest
        // =====================================================================================
        public static List<ScenarioResult> S5(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildDoc(200);
                BenchSession.RunJobs();
                var scale = canvas.CanvasUiElementScale;

                var pts = new Point[500];
                for (int i = 0; i < 500; i++)
                    pts[i] = new Point(i / 500.0 * 3200, i / 500.0 * 1800);

                var samples = BenchSession.Measure(Warmup, Iters, () =>
                {
                    for (int k = 0; k < 500; k++)
                        canvas.ToolPointer.MakeHitTest(canvas, pts[k], out _);
                });

                double totalMs = BenchSession.MedianWallMs(samples);
                var r = new ScenarioResult { Id = "S5", Name = "hover_hittest", N = 200, Warmup = Warmup, Iterations = Iters };
                r.Metrics["wallUsPerTestMedian"] = totalMs / 500.0 * 1000.0;
                r.Metrics["allocBytesPerTestMedian"] = BenchSession.MedianAllocBytes(samples) / 500.0;
                r.Metrics["gen0PerIteration"] = BenchSession.MedianGen0(samples);
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S6 render_record
        // =====================================================================================
        public static List<ScenarioResult> S6(BenchSession s)
        {
            var rows = new List<ScenarioResult>();
            foreach (int n in new[] { 10, 50, 200 })
            {
                var row = s.Run(() =>
                {
                    var canvas = FixtureBuilder.BuildDoc(n);
                    var window = new Window { Width = 1920, Height = 1080, Content = canvas };
                    window.Show();
                    BenchSession.RunJobs();
                    BenchSession.RenderTick();
                    canvas.ZoomPanAuto();
                    BenchSession.RunJobs();
                    BenchSession.RenderTick();

                    var rect = FirstRect(canvas);
                    int i = 0;
                    var samples = BenchSession.Measure(Warmup, Iters, () =>
                    {
                        for (int k = 0; k < 100; k++)
                        {
                            rect.ObjectColor = Pal[i++ % Pal.Length];
                            BenchSession.RunJobs();
                            BenchSession.RenderTick();
                        }
                    });

                    window.Close();
                    double totalMs = BenchSession.MedianWallMs(samples);
                    var r = new ScenarioResult { Id = "S6", Name = "render_record", N = n, Warmup = Warmup, Iterations = Iters };
                    r.Metrics["wallMsPerFrameMedian"] = totalMs / 100.0;
                    r.Metrics["wallMsTotalMedian"] = totalMs;
                    r.Notes = "baseline includes per-visual record but NOT compositor effect cost";
                    return r;
                });
                rows.Add(row);
            }
            return rows;
        }

        // =====================================================================================
        // S7 shadow_bake — reflective probe (works on both builds via ShadowRenderer.Render)
        // =====================================================================================
        public static List<ScenarioResult> S7(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var r = new ScenarioResult { Id = "S7", Name = "shadow_bake", N = 0, Warmup = Warmup, Iterations = Iters };
                var baker = ShadowBaker.Resolve();
                r.Notes = baker?.Description ?? "n/a: no shadow bake API found";

                foreach (var (label, w, h) in new[] { ("100x100", 100.0, 100.0), ("500x500", 500.0, 500.0), ("2000x1000", 2000.0, 1000.0) })
                {
                    if (baker == null) { r.Metrics["wallMsPerBake_" + label] = double.NaN; continue; }
                    var rect = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, w, h));
                    var samples = BenchSession.Measure(Warmup, Iters, () => baker.Bake(rect));
                    r.Metrics["wallMsPerBake_" + label] = BenchSession.MedianWallMs(samples);
                }
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S8 autosave_count
        // =====================================================================================
        public static List<ScenarioResult> S8(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildDoc(200);
                var rect = FirstRect(canvas);
                rect.IsSelected = true;

                int raises = 0;
                bool lastNonNull = false;
                EventHandler<StateChangedEventArgs> handler = (_, e) => { raises++; lastNonNull = e.State != null; };
                canvas.StateUpdated += handler;

                for (int i = 0; i < 300; i++)
                {
                    rect.ObjectColor = Pal[i % Pal.Length];
                    canvas.AddCommandToHistory(true);
                    if (i % 10 == 9) BenchSession.RunJobs();
                }

                int before = raises;
                // FlushPendingState is new-build only; invoke via reflection if present.
                var flush = typeof(DrawingCanvas).GetMethod("FlushPendingState", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                flush?.Invoke(canvas, null);

                // Spin the dispatcher until the debounce timer delivers the final state (or 500 ms wall).
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 500)
                {
                    BenchSession.RunJobs();
                    BenchSession.RenderTick();
                    if (raises > before) break;
                }

                canvas.StateUpdated -= handler;

                var r = new ScenarioResult { Id = "S8", Name = "autosave_count", N = 200, Warmup = 0, Iterations = 1 };
                r.Counters["stateUpdatedRaises"] = (double)raises;
                r.Counters["lastPayloadNonNull"] = lastNonNull;
                r.Counters["flushMethodPresent"] = flush != null;
                r.Notes = flush != null ? "new build: debounced autosave" : "old build: StateUpdated fires per commit";
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S9 export — latency + goldens + pixel diff
        // =====================================================================================
        public static List<ScenarioResult> S9(BenchSession s, string goldensDir, bool saveGoldens)
        {
            var row = s.Run(() =>
            {
                var r = new ScenarioResult { Id = "S9", Name = "export", N = 50, Warmup = 0, Iterations = 6 };
                Directory.CreateDirectory(goldensDir);

                var shadowless = FixtureBuilder.BuildDoc(50);
                for (int i = 0; i < shadowless.GraphicsList.Count; i++) shadowless.GraphicsList[i].DropShadowEffect = false;
                SafeMeasure9(shadowless, "shadowless", r, goldensDir, saveGoldens);

                SafeMeasure9(FixtureBuilder.BuildDoc(50), "shadowed", r, goldensDir, saveGoldens);

                SafeMeasure9(FixtureBuilder.BuildImageDoc(50, withObscure: true), "image", r, goldensDir, saveGoldens);

                return r;
            });
            return new List<ScenarioResult> { row };
        }

        private static void SafeMeasure9(DrawingCanvas canvas, string tag, ScenarioResult r, string goldensDir, bool saveGoldens)
        {
            try { Measure9(canvas, tag, r, goldensDir, saveGoldens); }
            catch (Exception ex) { r.Notes += $" [{tag} export unavailable headless: {ex.GetType().Name}: {ex.Message}]"; }
        }

        private static void Measure9(DrawingCanvas canvas, string tag, ScenarioResult r, string goldensDir, bool saveGoldens)
        {
            BenchSession.RunJobs();
            BenchSession.GcClean();

            var swCold = Stopwatch.StartNew();
            var bmp = canvas.DrawGraphicsToBitmap();
            swCold.Stop();

            var warm = new List<double>();
            Bitmap last = bmp;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                last = canvas.DrawGraphicsToBitmap();
                sw.Stop();
                warm.Add(sw.Elapsed.TotalMilliseconds);
            }

            r.Metrics["wallMsCold_" + tag] = swCold.Elapsed.TotalMilliseconds;
            r.Metrics["wallMsWarm_" + tag] = BenchSession.Median(warm);
            if (tag == "shadowed")
            {
                r.Metrics["wallMsColdShadowed"] = swCold.Elapsed.TotalMilliseconds;
                r.Metrics["wallMsWarmShadowed"] = BenchSession.Median(warm);
            }

            string goldenPath = Path.Combine(goldensDir, "export_" + tag + ".png");
            if (saveGoldens && last != null)
                last.Save(goldenPath);

            if (last != null && File.Exists(goldenPath))
            {
                try
                {
                    using var golden = new Bitmap(goldenPath);
                    var (maxD, meanD, pctOver16) = PixelDiff(golden, last);
                    r.Metrics["maxChannelDelta_" + tag] = maxD;
                    r.Metrics["meanAbsDelta_" + tag] = meanD;
                    r.Metrics["pctOver16_" + tag] = pctOver16;
                }
                catch (Exception ex)
                {
                    r.Notes += $" [diff {tag} failed: {ex.Message}]";
                }
            }
        }

        // =====================================================================================
        // S10 history_memory
        // =====================================================================================
        public static List<ScenarioResult> S10(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildDoc(200);
                var rect = FirstRect(canvas);
                rect.IsSelected = true;
                BenchSession.RunJobs();

                BenchSession.GcClean();
                long before = GC.GetTotalMemory(true);

                for (int i = 0; i < 500; i++)
                {
                    rect.Move(1, 0); // a real one-field change each step
                    canvas.AddCommandToHistory(false);
                    if (i % 25 == 24) BenchSession.RunJobs();
                }

                BenchSession.GcClean();
                long after = GC.GetTotalMemory(true);

                var r = new ScenarioResult { Id = "S10", Name = "history_memory", N = 200, Warmup = 0, Iterations = 1 };
                r.Metrics["totalMemoryDeltaMB"] = (after - before) / (1024.0 * 1024.0);
                r.Metrics["totalMemoryDeltaBytes"] = after - before;
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // =====================================================================================
        // S11 structural — zero-Effect visual tree + zero serializes on the drag path
        // =====================================================================================
        public static List<ScenarioResult> S11(BenchSession s)
        {
            var row = s.Run(() =>
            {
                var canvas = FixtureBuilder.BuildDoc(200);
                var window = new Window { Width = 1920, Height = 1080, Content = canvas };
                window.Show();
                BenchSession.RunJobs();
                BenchSession.RenderTick();
                canvas.ZoomPanAuto();
                BenchSession.RunJobs();
                BenchSession.RenderTick();

                int effectCount = canvas.GetVisualDescendants().Count(v => v.Effect != null);

                // 0 full-document serializations on the drag path: Move must not raise StateUpdated.
                int serializeRaises = 0;
                EventHandler<StateChangedEventArgs> handler = (_, __) => serializeRaises++;
                canvas.StateUpdated += handler;
                var rect = FirstRect(canvas);
                rect.IsSelected = true;
                for (int k = 0; k < 200; k++)
                {
                    rect.Move(1, 1);
                    if (k % 16 == 15) { BenchSession.RunJobs(); BenchSession.RenderTick(); }
                }
                canvas.StateUpdated -= handler;
                window.Close();

                var r = new ScenarioResult { Id = "S11", Name = "structural", N = 0, Warmup = 0, Iterations = 1 };
                r.Counters["effectVisualCount"] = (double)effectCount;
                r.Counters["dragSerializeRaises"] = (double)serializeRaises;
                r.Notes = "old build records effectVisualCount ≈ shadowed-graphic count (informational)";
                return r;
            });
            return new List<ScenarioResult> { row };
        }

        // ---- helpers ------------------------------------------------------------------------

        /// <summary>
        /// Resolves the shadow bake routine by reflection so the harness compiles and runs on both
        /// builds. Prefers a new-build sprite cache if a single-graphic bake method is discoverable;
        /// otherwise binds to <c>ShadowRenderer.Render</c> (present in both builds; its exact
        /// parameter list may change, so args are built from ParameterInfo).
        /// </summary>
        private sealed class ShadowBaker
        {
            public string Description;
            public Action<GraphicBase> Bake;

            public static ShadowBaker Resolve()
            {
                var asm = typeof(GraphicBase).Assembly;
                var renderer = asm.GetType("Clowd.Drawing.ShadowRenderer");
                var render = renderer?.GetMethod("Render", BindingFlags.Public | BindingFlags.Static);
                if (render != null)
                {
                    var ps = render.GetParameters();
                    return new ShadowBaker
                    {
                        Description = "ShadowRenderer.Render(" + string.Join(",", ps.Select(p => p.ParameterType.Name)) + ")",
                        Bake = g =>
                        {
                            var args = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                var t = ps[i].ParameterType;
                                if (typeof(GraphicBase).IsAssignableFrom(t)) args[i] = g;
                                else if (t == typeof(double)) args[i] = 1.0; // bakeScale
                                else args[i] = ps[i].IsOut ? null : (t.IsValueType ? Activator.CreateInstance(t) : null);
                            }
                            render.Invoke(null, args);
                        },
                    };
                }
                return null;
            }
        }

        private static (double maxDelta, double meanDelta, double pctOver16) PixelDiff(Bitmap a, Bitmap b)
        {
            if (a.PixelSize != b.PixelSize)
                return (255, 255, 100);

            var pa = Extract(a);
            var pb = Extract(b);
            int len = Math.Min(pa.Length, pb.Length);
            long sum = 0;
            int max = 0;
            long over = 0;
            long pixels = len / 4;
            for (int i = 0; i < len; i += 4)
            {
                bool pixOver = false;
                for (int c = 0; c < 4; c++)
                {
                    int d = Math.Abs(pa[i + c] - pb[i + c]);
                    sum += d;
                    if (d > max) max = d;
                    if (d > 16) pixOver = true;
                }
                if (pixOver) over++;
            }
            double mean = len == 0 ? 0 : (double)sum / len;
            double pct = pixels == 0 ? 0 : 100.0 * over / pixels;
            return (max, mean, pct);
        }

        private static byte[] Extract(Bitmap bmp)
        {
            var size = bmp.PixelSize;
            using var wb = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = wb.Lock();
            bmp.CopyPixels(fb, AlphaFormat.Unpremul);
            int w = size.Width, h = size.Height;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(new IntPtr(fb.Address.ToInt64() + (long)y * fb.RowBytes), px, y * w * 4, w * 4);
            return px;
        }
    }
}
