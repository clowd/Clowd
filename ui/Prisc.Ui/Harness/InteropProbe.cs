using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Prisc.Interop;

namespace Prisc.UI.Harness
{
    /// <summary>
    /// The hidden <c>--interop-probe &lt;out.json&gt;</c> harness: exercises the native boundary inside a
    /// real Prisc.Ui process and writes one JSON object per item,
    /// <c>{ "&lt;area&gt;.&lt;item&gt;": { "status": "ok|unsupported|unavailable|denied|error", "value": ...,
    /// "os_code": n, "error": "...", "ms": n } }</c>.
    /// </summary>
    /// <remarks>
    /// <para>It runs ahead of the single-instance mutex (App.IsHarnessLaunch), so it coexists with a
    /// resident Prisc. On the UI thread (the AppKit main thread on macOS) it opens one small real
    /// window and runs every section; then it runs the thread-agnostic items again from a thread-pool
    /// thread (keys suffixed <c>@pool</c>). It only queries: it never requests a permission and never
    /// plays audio.</para>
    /// <para>Exit code = the number of <c>error</c> results: a broken contract (a wrong status for a
    /// documented case, a panic, a missing library, an unexpected exception). <c>unavailable</c> (the
    /// OS said no, with its code) and <c>denied</c> (a permission is missing) never count.</para>
    /// <para>The facade items (InteropProbe.Facades.cs) go through the public C# surfaces that keep
    /// their signatures through the whole migration, so the same probe run before and after a change is
    /// the equality oracle. Each area adds its wrapper-level items to its own partial file.</para>
    /// </remarks>
    internal static partial class InteropProbe
    {
        // App.Startup only: it tests this constant in its harness gate, never the literal
        internal const string ArgName = "--interop-probe";

        /// <summary>Returns true (and takes over startup) when args request a probe.</summary>
        public static bool TryHandleArgs(string[] args)
        {
            var index = Array.FindIndex(args, a => String.Equals(a, ArgName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine($"{ArgName} needs an output path");
                Environment.Exit(2);
            }

            var path = Path.GetFullPath(args[index + 1]);
            try
            {
                Start(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("interop probe failed: " + ex);
                Environment.Exit(1);
            }

            return true;
        }

        private static void Start(string path)
        {
            var window = new Window
            {
                Title = "Prisc interop probe",
                Width = 240,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(40, 40),
            };

            window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                var results = new JsonObject();
                var ui = new ProbeRun(window, results, suffix: "");
                RunAll(ui);

                var pool = new ProbeRun(null, results, suffix: "@pool");
                await Task.Run(() => ui.ReplayAnyThread(pool));

                var errors = ui.Errors + pool.Errors;
                File.WriteAllText(path, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"interop probe: {results.Count} items, {errors} error(s), written to {path}");
                window.Close();
                Environment.Exit(errors);
            });

            window.Show();
        }

        private static void RunAll(ProbeRun run)
        {
            Library(run);
            Facades(run);
            ProbeDesktop(run);
            ProbeWindow(run);
            ProbeIpc(run);
            ProbeMacApp(run);
            ProbeAudio(run);
            ProbeGpu(run);
            ProbeHotkeys(run);
            ProbeKeys(run);
            ProbePure(run);
        }

        /// <summary>The library itself: where it loaded from, its version, its ABI and the boundary's
        /// status machinery.</summary>
        private static void Library(ProbeRun run)
        {
            run.Item("library.load", () =>
            {
                InteropLibrary.Require();
                return new Dictionary<string, object> { ["path"] = InteropLibrary.LoadedPath, ["version"] = InteropLibrary.Version };
            });
            run.Item("library.abi_hash", () =>
            {
                InteropLibrary.Require();
                return "0x" + InteropLibrary.AbiHash.ToString("X16", CultureInfo.InvariantCulture);
            });
            run.Item("library.selftest", () =>
            {
                var ok = InteropDiagnostics.SelfTest(SelftestKind.Ok, out var value, out _);
                if (ok != InteropStatus.Ok || value != 42)
                    throw new InvalidOperationException($"selftest Ok answered {ok} with {value}");
                var panic = InteropDiagnostics.SelfTest(SelftestKind.Panic, out _, out var message);
                if (panic != InteropStatus.Panic || !message.Contains("prisc_interop_selftest", StringComparison.Ordinal))
                    throw new InvalidOperationException($"selftest Panic answered {panic}: {message}");
                return value;
            }, anyThread: true);
        }

        // One per area, implemented in InteropProbe.<Area>.cs.
        static partial void ProbeDesktop(ProbeRun run);
        static partial void ProbeWindow(ProbeRun run);
        static partial void ProbeIpc(ProbeRun run);
        static partial void ProbeMacApp(ProbeRun run);
        static partial void ProbeAudio(ProbeRun run);
        static partial void ProbeGpu(ProbeRun run);
        static partial void ProbeHotkeys(ProbeRun run);
        static partial void ProbeKeys(ProbeRun run);
        static partial void ProbePure(ProbeRun run);

        /// <summary>An explicit outcome an item may return instead of a plain value.</summary>
        internal sealed record ProbeOutcome(string Status, object Value = null, long OsCode = 0, string Error = null)
        {
            public static ProbeOutcome Unsupported(string why) => new("unsupported", Error: why);

            public static ProbeOutcome Unavailable(string why, long osCode = 0) => new("unavailable", OsCode: osCode, Error: why);

            public static ProbeOutcome Denied(string why) => new("denied", Error: why);
        }

        /// <summary>One pass over the items: the UI-thread pass (with the probe window) or the pool pass.</summary>
        internal sealed class ProbeRun
        {
            private readonly JsonObject _results;
            private readonly string _suffix;
            private readonly List<(string Key, Func<object> Body)> _anyThread = new();

            public ProbeRun(Window window, JsonObject results, string suffix)
            {
                Window = window;
                _results = results;
                _suffix = suffix;
            }

            /// <summary>The probe window on the UI pass; null on the pool pass.</summary>
            public Window Window { get; }

            public int Errors { get; private set; }

            /// <summary>Runs one item now and records it. <paramref name="anyThread"/> items run again
            /// on the pool pass.</summary>
            public void Item(string key, Func<object> body, bool anyThread = false)
            {
                if (anyThread)
                    _anyThread.Add((key, body));

                var watch = Stopwatch.StartNew();
                ProbeOutcome outcome;
                try
                {
                    var value = body();
                    outcome = value as ProbeOutcome ?? new ProbeOutcome("ok", value);
                }
                catch (InteropException e)
                {
                    outcome = e.Status switch
                    {
                        InteropStatus.Unsupported => new ProbeOutcome("unsupported", Error: e.Message),
                        InteropStatus.Permission => new ProbeOutcome("denied", Error: e.Message),
                        InteropStatus.Os or InteropStatus.Capability => new ProbeOutcome("unavailable", OsCode: e.NativeCode, Error: e.Message),
                        _ => new ProbeOutcome("error", OsCode: e.NativeCode, Error: e.Message),
                    };
                }
                catch (PlatformNotSupportedException e)
                {
                    outcome = new ProbeOutcome("unsupported", Error: e.Message);
                }
                catch (Exception e)
                {
                    outcome = new ProbeOutcome("error", Error: e.GetType().Name + ": " + e.Message);
                }

                if (outcome.Status == "error")
                    Errors++;

                var entry = new JsonObject
                {
                    ["status"] = outcome.Status,
                    ["value"] = ToJson(outcome.Value),
                    ["ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
                };
                if (outcome.OsCode != 0)
                    entry["os_code"] = outcome.OsCode;
                if (outcome.Error != null)
                    entry["error"] = outcome.Error;
                lock (_results)
                    _results[key + _suffix] = entry;
            }

            /// <summary>Runs this pass's thread-agnostic items again on <paramref name="pool"/>.</summary>
            public void ReplayAnyThread(ProbeRun pool)
            {
                foreach (var (key, body) in _anyThread)
                    pool.Item(key, body);
            }

            private static JsonNode ToJson(object value) => value switch
            {
                null => null,
                JsonNode node => node,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                uint u => JsonValue.Create(u),
                long l => JsonValue.Create(l),
                ulong ul => JsonValue.Create(ul),
                double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
                float f => JsonValue.Create((double)f),
                IDictionary<string, object> map => ToObject(map),
                IEnumerable list => ToArray(list),
                IFormattable f => JsonValue.Create(f.ToString(null, CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(value.ToString()),
            };

            private static JsonObject ToObject(IDictionary<string, object> map)
            {
                var o = new JsonObject();
                foreach (var (k, v) in map)
                    o[k] = ToJson(v);
                return o;
            }

            private static JsonArray ToArray(IEnumerable list)
            {
                var a = new JsonArray();
                foreach (var item in list)
                    a.Add(ToJson(item));
                return a;
            }
        }
    }
}
