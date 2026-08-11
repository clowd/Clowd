using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Render;

namespace Clowd.VideoRender
{
    /// <summary>
    /// Renders an edited recording to an mp4, replacing the external Rust <c>vid-render</c> tool
    /// with the in-repo <see cref="RenderJob"/>. Deliberately thin: locate the FFmpeg natives, turn
    /// the args file into a <see cref="Project"/>, run the render, speak the protocol.
    ///
    /// <para>Usage: <c>Clowd.VideoRender &lt;path-to-args.json&gt; [output.mp4]</c> — the args file
    /// is the whole job description, exactly as vid-render took it (the caller,
    /// <c>VidRenderRunner</c>, passes nothing else). Two file shapes are accepted:</para>
    /// <list type="bullet">
    /// <item><c>"version": 1</c> — the legacy render-args file (keep segments, webcam rect + mask
    /// PNG, crf), mapped onto the v2 model by <see cref="RenderArgsCompat"/>.</item>
    /// <item><c>"version": 2</c> — a <see cref="Project"/> straight from the editor. The output
    /// path is not part of the project model, so it comes from a sibling <c>"output"</c> property
    /// in the same file (and an optional <c>"crf"</c>), or from the optional second argument, which
    /// wins when both are present.</item>
    /// </list>
    ///
    /// <para>Stdout protocol, byte-compatible with vid-render (and vid2gif before it), one message
    /// per line and <b>nothing else on stdout ever</b>:</para>
    /// <code>
    /// progress &lt;0-100&gt;       monotonically increasing integer percent
    /// done &lt;path&gt; &lt;bytes&gt;    render finished successfully (exit code 0)
    /// error &lt;message&gt;        render failed, single line (exit code 1)
    /// cancelled              stdin cancellation honored (exit code 0)
    /// </code>
    ///
    /// <para>Stdin: a <c>quit</c> line cancels — the render stops within one frame, the partial
    /// output is removed and the terminal message is <c>cancelled</c>. Errors also remove the
    /// partial output. Diagnostics (backend selection, mask notes) go to stderr, which is outside
    /// the protocol.</para>
    /// </summary>
    internal static class Program
    {
        private const int ExitSuccess = 0;
        private const int ExitError = 1;

        private static int Main(string[] argv)
        {
            // the protocol is line-oriented ASCII; make sure nothing buffers past a terminal line.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);

            using var cancel = new CancellationTokenSource();
            StartStdinWatcher(cancel);
            var emitter = new ProgressEmitter();

            // remembered as soon as the args parse, so the error path can remove a partial output
            // that RenderJob did not get to clean up itself.
            string output = null;

            try
            {
                var argsPath = ParseCommandLine(argv, out var outputOverride);

                if (!FFmpegLoader.TryInitialize(FindFFmpegDirectory))
                    throw new InvalidOperationException(FFmpegLoader.FailureReason);

                var job = LoadJob(argsPath, outputOverride);
                output = job.OutputPath;

                emitter.Emit(0);
                var result = RenderJob.Run(job.Project, job.OutputPath,
                    new RenderJobOptions
                    {
                        Crf = job.Crf,
                        // v1 args are vid-render's contract: mux with its container timing, and
                        // follow its VFR frame-passthrough schedule when the compat mapping built
                        // one. v2 projects render on the CFR grid with full sample durations.
                        FrameTimestampsTicks = job.FrameTimestampsTicks,
                        LegacyContainerTiming = job.LegacyContainerTiming,
                        // CLOWD_RENDER_BACKEND=cpu forces the raster path — used by the GPU/CPU
                        // equivalence checks; the surface factory falls back to CPU on its own
                        // when no usable GPU context exists, so this is a test/diagnostic knob,
                        // not something the app needs to set.
                        PreferGpu = !String.Equals(
                            Environment.GetEnvironmentVariable("CLOWD_RENDER_BACKEND"), "cpu",
                            StringComparison.OrdinalIgnoreCase),
                        DiagnosticLog = message => Console.Error.WriteLine("Clowd.VideoRender: " + message),
                    },
                    new InlineProgress(percent => emitter.Emit(percent)),
                    cancel.Token);

                if (result.Outcome == RenderOutcome.Cancelled)
                {
                    // RenderJob already deleted the partial file.
                    Console.WriteLine("cancelled");
                    return ExitSuccess;
                }

                emitter.Emit(100);
                Console.WriteLine(FormattableString.Invariant(
                    $"done {result.OutputPath} {result.OutputBytes}"));
                return ExitSuccess;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                TryDelete(output);
                Console.WriteLine("cancelled");
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                TryDelete(output);
                Console.WriteLine("error " + SingleLine(Describe(ex)));
                Console.Error.WriteLine(ex.ToString());
                return ExitError;
            }
        }

        // ------------------------------------------------------------------------ job loading

        /// <summary>The args file path, plus the optional output override. Mirrors vid-render's
        /// argument handling (one positional argument, everything else is a usage error).</summary>
        private static string ParseCommandLine(string[] argv, out string outputOverride)
        {
            outputOverride = null;
            if (argv == null || argv.Length == 0 || argv.Length > 2)
                throw new InvalidOperationException("usage: Clowd.VideoRender <path-to-args.json> [output.mp4]");

            if (argv.Length == 2)
                outputOverride = argv[1];

            return argv[0];
        }

        /// <summary>Reads the job file, dispatching on its <c>version</c>.</summary>
        private static LegacyRenderPlan LoadJob(string argsPath, string outputOverride)
        {
            int version = ReadVersion(argsPath);

            if (version == RenderArgsCompat.LegacyVersion)
            {
                var plan = RenderArgsCompat.Load(argsPath);
                return outputOverride == null
                    ? plan
                    : new LegacyRenderPlan
                    {
                        Project = plan.Project,
                        InputPath = plan.InputPath,
                        OutputPath = outputOverride,
                        Crf = plan.Crf,
                        MaskPngPath = plan.MaskPngPath,
                        FrameTimestampsTicks = plan.FrameTimestampsTicks,
                        LegacyContainerTiming = plan.LegacyContainerTiming,
                    };
            }

            if (version == Project.CurrentVersion)
                return LoadProjectFile(argsPath, outputOverride);

            throw new InvalidOperationException(
                $"unsupported args version {version} (expected {RenderArgsCompat.LegacyVersion} or {Project.CurrentVersion})");
        }

        private static int ReadVersion(string argsPath)
        {
            string text;
            try
            {
                text = File.ReadAllText(argsPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"could not read args file: {argsPath}: {ex.Message}", ex);
            }

            try
            {
                using var document = JsonDocument.Parse(text,
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("version", out var version) ||
                    !version.TryGetInt32(out var value))
                    throw new InvalidOperationException("args file has no numeric \"version\" property");

                return value;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("invalid args JSON: " + ex.Message, ex);
            }
        }

        /// <summary>A v2 file is the project itself; the output path and encoder quality ride
        /// alongside it as siblings (the project model has no notion of where it is written).</summary>
        private static LegacyRenderPlan LoadProjectFile(string argsPath, string outputOverride)
        {
            var text = File.ReadAllText(argsPath);

            Project project;
            try
            {
                project = Project.FromJson(text);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("invalid project JSON: " + ex.Message, ex);
            }

            if (project == null)
                throw new InvalidOperationException("invalid project JSON: the file is empty");

            string output = outputOverride;
            int crf = RenderArgsCompat.DefaultCrf;

            using (var document = JsonDocument.Parse(text,
                       new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (output == null &&
                    document.RootElement.TryGetProperty("output", out var outputProperty) &&
                    outputProperty.ValueKind == JsonValueKind.String)
                    output = outputProperty.GetString();

                if (document.RootElement.TryGetProperty("crf", out var crfProperty) &&
                    crfProperty.TryGetInt32(out var crfValue))
                    crf = crfValue;
            }

            if (String.IsNullOrEmpty(output))
                throw new InvalidOperationException(
                    "the project file has no \"output\" path and none was passed on the command line");

            if (crf is < 0 or > 51)
                throw new InvalidOperationException($"crf {crf} out of range (0-51)");

            var parent = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!String.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            return new LegacyRenderPlan { Project = project, OutputPath = output, Crf = crf };
        }

        // ------------------------------------------------------------------------ FFmpeg natives

        /// <summary>
        /// Where the FFmpeg natives live when <c>CLOWD_FFMPEG_PATH</c> is unset. Release layout
        /// first: the DLLs ship beside this binary (the runner starts us in our own directory for
        /// exactly that reason, and vid-render loaded them the same way). Then the dev layout: an
        /// <c>obs-express-rs</c> sibling checkout's cargo target directory, mirroring
        /// <c>ObsBinaryLocator</c> and the test suite's resolver.
        /// </summary>
        private static string FindFFmpegDirectory()
        {
            var baseDirectory = AppContext.BaseDirectory;
            if (HasFFmpeg(baseDirectory))
                return baseDirectory;

            // Release layout: this exe publishes beside Clowd.Ui, and the FFmpeg DLLs ship in
            // the obs-express/ subdirectory (ci.yml). The runner also passes CLOWD_FFMPEG_PATH
            // explicitly; this probe keeps a manually-invoked exe working in that layout too.
            var obsSubdir = Path.Combine(baseDirectory, "obs-express");
            if (HasFFmpeg(obsSubdir))
                return obsSubdir;

            var directory = new DirectoryInfo(baseDirectory);
            while (directory != null)
            {
                foreach (var configuration in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(directory.FullName, "obs-express-rs", "target", configuration);
                    if (HasFFmpeg(candidate))
                        return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static bool HasFFmpeg(string directory)
        {
            if (String.IsNullOrEmpty(directory))
                return false;

            string[] probes = OperatingSystem.IsWindows() ? new[] { "avcodec-61.dll" }
                : OperatingSystem.IsMacOS() ? new[] { "libavcodec.61.dylib", "libavcodec.dylib" }
                : new[] { "libavcodec.so.61" };

            foreach (var probe in probes)
            {
                if (File.Exists(Path.Combine(directory, probe)))
                    return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------------- protocol

        /// <summary>Watches stdin for a <c>quit</c> line and trips the token. The thread blocks in
        /// read for the process lifetime; it is a background thread, so it dies with the process.</summary>
        private static void StartStdinWatcher(CancellationTokenSource cancel)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        if (String.Equals(line.Trim(), "quit", StringComparison.OrdinalIgnoreCase))
                        {
                            cancel.Cancel();
                            return;
                        }
                    }
                }
                catch
                {
                    // stdin closed or already disposed — nothing left to cancel from.
                }
            })
            {
                IsBackground = true,
                Name = "stdin-watcher",
            };

            thread.Start();
        }

        /// <summary>Prints <c>progress &lt;n&gt;</c>: deduplicated, monotonic, clamped to 100 —
        /// the same rule vid-render's Emitter applied, because the consumers parse them
        /// identically.</summary>
        private sealed class ProgressEmitter
        {
            private readonly object _sync = new object();
            private int _last = -1;

            public void Emit(double percent)
            {
                int value = (int)Math.Clamp(Math.Floor(percent), 0, 100);
                lock (_sync)
                {
                    if (value <= _last)
                        return;

                    _last = value;
                }

                Console.WriteLine("progress " + value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Reports on the calling thread. <see cref="Progress{T}"/> would post to the
        /// thread pool, which can deliver a percent <i>after</i> the terminal line.</summary>
        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _report;

            public InlineProgress(Action<double> report) => _report = report;

            public void Report(double value) => _report(value);
        }

        /// <summary>The message chain of an exception, innermost causes appended — the C# shape of
        /// anyhow's <c>{e:#}</c>.</summary>
        private static string Describe(Exception ex)
        {
            var builder = new StringBuilder();
            for (var current = ex; current != null; current = current.InnerException)
            {
                var message = current.Message;
                if (String.IsNullOrWhiteSpace(message) || builder.ToString().Contains(message, StringComparison.Ordinal))
                    continue;

                if (builder.Length > 0)
                    builder.Append(": ");
                builder.Append(message);
            }

            return builder.Length > 0 ? builder.ToString() : ex.GetType().Name;
        }

        /// <summary>Collapses a possibly multi-line message onto one bounded line so it cannot
        /// break the line-oriented protocol.</summary>
        internal static string SingleLine(string message)
        {
            const int max = 500;
            var collapsed = String.Join(' ', (message ?? String.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

            return collapsed.Length > max ? collapsed.Substring(0, max) + "…" : collapsed;
        }

        private static void TryDelete(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best effort — matching vid-render's `let _ = remove_file(...)`.
            }
        }
    }
}
