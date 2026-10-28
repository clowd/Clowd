using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Prisc.Ipc;
using Prisc.VideoSDK;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Media;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Render;

namespace Prisc.VideoRender
{
    /// <summary>
    /// Renders an edited recording to an mp4 with the in-repo <see cref="RenderJob"/>. Deliberately
    /// thin: locate the FFmpeg natives, turn the args file into a <see cref="Project"/>, run the
    /// render, speak the protocol.
    ///
    /// <para>Usage: <c>Prisc.VideoRender &lt;path-to-args.json&gt; [output.mp4]</c>. The args file is
    /// the whole job description (the caller, <c>JobRunner.ForVideoRender</c>, passes nothing else).
    /// Two file shapes are accepted:</para>
    /// <list type="bullet">
    /// <item><c>"version": 1</c>: the legacy render-args file (keep segments, webcam rect + mask
    /// PNG, crf), mapped onto the v2 model by <see cref="RenderArgsCompat"/>.</item>
    /// <item><c>"version": 2</c>: a <see cref="Project"/> straight from the editor. The output
    /// path is not part of the project model, so it comes from a sibling <c>"output"</c> property
    /// in the same file (and optional <c>"crf"</c>, <c>"encoder"</c> and <c>"maxHeight"</c>
    /// siblings: the encoder one of <c>auto|software|nvenc|amf|videotoolbox</c>, <c>auto</c> when
    /// absent; <c>maxHeight</c> a non-negative whole number of pixels capping the encoded height,
    /// no cap when absent or 0), or from the optional second argument, which wins when both are
    /// present.</item>
    /// </list>
    ///
    /// <para>The stdio protocol is the <c>job</c> catalog (crates/prisc_capture/HELPER_PROTOCOL.md
    /// §4.3), shared with prisc_vid2gif: <c>hello</c>, then <c>progress</c> (monotonic,
    /// deduplicated), then exactly one of <c>done{ok, path, bytes}</c> (exit 0),
    /// <c>done{cancelled}</c> (exit 0) or a fatal <c>error</c> (exit 1, or 2 for a bad command line).
    /// <c>quit</c> or stdin EOF cancels: the render stops within one frame and the partial output is
    /// removed, as it is on an error. stdout carries protocol lines and nothing else (the
    /// <see cref="IpcChild"/> guard); diagnostics go to stderr.</para>
    /// </summary>
    internal static class Program
    {
        /// <summary>Beyond the catalog's quit grace: the parent kills us first if it is alive, so this
        /// only ends an orphan.</summary>
        private static readonly TimeSpan SelfExitMargin = TimeSpan.FromSeconds(5);

        /// <summary>Tests only: with this set to <c>1</c> the render waits after its first encoded
        /// frame until it is told to quit (bounded by <see cref="HoldForQuitBound"/>), so a cancel
        /// test always lands its quit mid-render. A short fixture renders in a second or two, which
        /// a loaded test host can take just to turn <c>hello</c> around into the quit.</summary>
        internal const string HoldForQuitVariable = "PRISC_RENDER_TEST_HOLD_FOR_QUIT";

        /// <summary>Long past the catalog's quit grace: a quit that never arrives shows up as a
        /// parent-side kill, not as a hold that quietly gave up first.</summary>
        private static readonly TimeSpan HoldForQuitBound = TimeSpan.FromSeconds(30);

        private static int Main(string[] argv)
        {
            // first, before FFmpeg, Skia or a GPU driver can print to fd 1
            var events = IpcChild.Open("Prisc.VideoRender", HelperSpecs.Job);

            using var cancel = new CancellationTokenSource();
            IpcChild.OnQuit += () =>
            {
                try { cancel.Cancel(); }
                catch (ObjectDisposedException) { }
            };
            IpcChild.ArmHardExit(HelperSpecs.Job.QuitGrace + SelfExitMargin);
            // the job catalog has no commands besides quit
            IpcChild.ReadCommands(JobCodec.DecodeCommand, _ => { },
                reason => events.Emit(new JobEvent.Error(IpcErrorCodes.BadCommand, reason, false)));

            var emitter = new ProgressEmitter(events);
            bool holdForQuit = Environment.GetEnvironmentVariable(HoldForQuitVariable) == "1";

            // remembered as soon as the args parse, so the error path can remove a partial output
            // that RenderJob did not get to clean up itself.
            string output = null;

            try
            {
                var argsPath = ParseCommandLine(argv, out var outputOverride);

                if (!FFmpegLoader.TryInitialize(FFmpegLoader.FindBesideRecorder))
                    throw new JobFailure(IpcErrorCodes.InitFailed, FFmpegLoader.FailureReason);

                var job = LoadJob(argsPath, outputOverride);
                output = job.OutputPath;

                emitter.Emit(0);
                var result = RenderJob.Run(job.Project, job.OutputPath,
                    new RenderJobOptions
                    {
                        Crf = job.Crf,
                        Encoder = job.Encoder,
                        // the job file's optional size cap: the project composes at its canvas
                        // size and the encoder opens at the capped one (0 = no cap).
                        MaxHeight = job.MaxHeight,
                        // v1 args are vid-render's contract: mux with its container timing, and
                        // follow its VFR frame-passthrough schedule when the compat mapping built
                        // one. v2 projects render on the CFR grid with full sample durations.
                        FrameTimestampsTicks = job.FrameTimestampsTicks,
                        LegacyContainerTiming = job.LegacyContainerTiming,
                        // PRISC_RENDER_BACKEND=cpu forces the raster path, used by the GPU/CPU
                        // equivalence checks; the surface factory falls back to CPU on its own
                        // when no usable GPU context exists, so this is a test/diagnostic knob,
                        // not something the app needs to set.
                        PreferGpu = !String.Equals(
                            Environment.GetEnvironmentVariable("PRISC_RENDER_BACKEND"), "cpu",
                            StringComparison.OrdinalIgnoreCase),
                        // PRISC_RENDER_ZEROCOPY=0 forces the readback path for NVENC/AMF renders
                        // (the zero-copy Direct3D 11 hand-off falls back to it on its own when
                        // anything is missing): the A/B knob for benchmarking the two, in the
                        // same spirit as PRISC_RENDER_BACKEND.
                        ZeroCopyEncode = !String.Equals(
                            Environment.GetEnvironmentVariable("PRISC_RENDER_ZEROCOPY"), "0",
                            StringComparison.Ordinal),
                        // the job file sits beside the project's videoedit.json, which is where
                        // the AI sidecars live (AiSidecars): matte/denoise consumption and any
                        // pre-render generation both key off this directory.
                        SidecarCacheDir = Path.GetDirectoryName(Path.GetFullPath(argsPath)),
                        DiagnosticLog = message => Console.Error.WriteLine("Prisc.VideoRender: " + message),
                    },
                    new InlineProgress(percent =>
                    {
                        emitter.Emit(percent);
                        // the first report past the start is the first encoded frame: the output
                        // exists, and the encode loop checks the token right after this returns
                        if (holdForQuit && percent > 0)
                        {
                            holdForQuit = false;
                            cancel.Token.WaitHandle.WaitOne(HoldForQuitBound);
                        }
                    }),
                    cancel.Token);

                if (result.Outcome == RenderOutcome.Canceled)
                {
                    // RenderJob already deleted the partial file.
                    events.Emit(new JobEvent.Done(JobResult.Cancelled, null, null));
                    return HelperExit.Ok;
                }

                emitter.Emit(100);
                events.Emit(new JobEvent.Done(JobResult.Ok, result.OutputPath, (ulong)Math.Max(0, result.OutputBytes)));
                return HelperExit.Ok;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                TryDelete(output);
                events.Emit(new JobEvent.Done(JobResult.Cancelled, null, null));
                return HelperExit.Ok;
            }
            catch (Exception ex)
            {
                TryDelete(output);
                var code = (ex as JobFailure)?.Code ?? IpcErrorCodes.Failed;
                events.Emit(new JobEvent.Error(code, SingleLine(Describe(ex)), true));
                Console.Error.WriteLine(ex.ToString());
                return code == IpcErrorCodes.Usage ? HelperExit.Usage : HelperExit.Failed;
            }
        }

        /// <summary>A failure with a specific protocol error code (the rest are <c>failed</c>).</summary>
        private sealed class JobFailure : Exception
        {
            public JobFailure(string code, string message)
                : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        // ------------------------------------------------------------------------ job loading

        /// <summary>The args file path, plus the optional output override. Mirrors vid-render's
        /// argument handling (one positional argument, everything else is a usage error).</summary>
        private static string ParseCommandLine(string[] argv, out string outputOverride)
        {
            outputOverride = null;
            if (argv == null || argv.Length == 0 || argv.Length > 2)
                throw new JobFailure(IpcErrorCodes.Usage, "usage: Prisc.VideoRender <path-to-args.json> [output.mp4]");

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
                        Encoder = plan.Encoder,
                        MaxHeight = plan.MaxHeight,
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
                // JSON lookups are ordinal, and the two file shapes spell it differently: the v1
                // args file is a snake_case wire contract, a v2 file is the project itself, whose
                // properties are PascalCase.
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    (!document.RootElement.TryGetProperty("version", out var version) &&
                     !document.RootElement.TryGetProperty("Version", out version)) ||
                    !version.TryGetInt32(out var value))
                    throw new InvalidOperationException("args file has no numeric \"version\" property");

                return value;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("invalid args JSON: " + ex.Message, ex);
            }
        }

        /// <summary>A v2 file is the project itself; the output path, encoder quality and encoder
        /// choice ride alongside it as siblings (the project model has no notion of where or how
        /// it is written).</summary>
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
            var encoder = VideoEncoder.Auto;
            int maxHeight = 0;

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

                // absent means auto; present means exactly one of the known names — a typo must
                // not silently render on the software path (or the hardware one).
                if (document.RootElement.TryGetProperty("encoder", out var encoderProperty) &&
                    (encoderProperty.ValueKind != JsonValueKind.String ||
                     !VideoEncoderNames.TryParse(encoderProperty.GetString(), out encoder)))
                    throw new InvalidOperationException(
                        $"unknown encoder {encoderProperty.GetRawText()} (expected one of {VideoEncoderNames.All})");

                // The size cap is optional and absent means none; anything present must be a
                // whole number of pixels — a "720p" string or a 719.5 would otherwise silently
                // render at full size, which is the opposite of what the user asked for.
                if (document.RootElement.TryGetProperty(ProjectFileWriter.MaxHeightProperty, out var maxHeightProperty) &&
                    (maxHeightProperty.ValueKind != JsonValueKind.Number || !maxHeightProperty.TryGetInt32(out maxHeight)))
                    throw new InvalidOperationException(
                        $"maxHeight {maxHeightProperty.GetRawText()} is not a whole number of pixels");
            }

            if (String.IsNullOrEmpty(output))
                throw new InvalidOperationException(
                    "the project file has no \"output\" path and none was passed on the command line");

            if (crf is < 0 or > 51)
                throw new InvalidOperationException($"crf {crf} out of range (0-51)");

            if (maxHeight < 0)
                throw new InvalidOperationException($"maxHeight {maxHeight} is negative");

            var parent = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!String.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            return new LegacyRenderPlan
            {
                Project = project,
                OutputPath = output,
                Crf = crf,
                Encoder = encoder,
                MaxHeight = maxHeight,
            };
        }

        // ---------------------------------------------------------------------------- protocol

        /// <summary>Emits <c>progress</c>: deduplicated, monotonic, clamped to 100, because the
        /// consumers treat the stream as a monotonic percentage.</summary>
        private sealed class ProgressEmitter
        {
            private readonly IpcWriter<JobEvent> _events;
            private readonly object _sync = new object();
            private int _last = -1;

            public ProgressEmitter(IpcWriter<JobEvent> events) => _events = events;

            public void Emit(double percent)
            {
                int value = (int)Math.Clamp(Math.Floor(percent), 0, 100);
                lock (_sync)
                {
                    if (value <= _last)
                        return;

                    _last = value;
                }

                _events.Emit(new JobEvent.Progress((uint)value));
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
