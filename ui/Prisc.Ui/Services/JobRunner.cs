using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Prisc.Ipc;
using Prisc.VideoSDK;

namespace Prisc.UI.Services
{
    /// <summary>How a job run ended.</summary>
    internal enum JobOutcome
    {
        Success,
        Canceled,
        Error,
    }

    /// <summary>The terminal state of a job run. <see cref="Message"/> is user-facing;
    /// <see cref="Diagnostics"/> is the helper's stderr tail, for the crash report only.</summary>
    internal sealed record JobRunResult(JobOutcome Outcome, string OutputPath, long Bytes, string Message, string Diagnostics)
    {
        public static JobRunResult Success(string outputPath, long bytes) =>
            new(JobOutcome.Success, outputPath, bytes, null, null);

        public static JobRunResult Canceled() =>
            new(JobOutcome.Canceled, null, 0, null, null);

        public static JobRunResult Error(string message, string diagnostics = null) =>
            new(JobOutcome.Error, null, 0, message, diagnostics);
    }

    /// <summary>
    /// Hosts one run of a <c>job</c>-catalog helper (HELPER_PROTOCOL.md §4.3): prisc_vid2gif for a GIF
    /// export, Prisc.VideoRender for an edited render. There is no handshake beyond <c>hello</c>: the
    /// helper starts working the moment it is spawned, reports <c>progress</c>, and ends with
    /// <c>done</c> (ok or cancelled) or a fatal <c>error</c>. <c>quit</c> cancels: the helper stops
    /// within a frame, deletes its partial output and says <c>done{cancelled}</c>. The whole lifecycle
    /// is one <see cref="RunAsync"/> call. <see cref="ProgressChanged"/> is raised on the UI thread.
    /// One instance per run.
    /// </summary>
    internal sealed class JobRunner : IDisposable
    {
        private readonly string _stem;
        private readonly string _what;
        private readonly IReadOnlyList<string> _args;
        private readonly HelperStartOptions _options;
        private readonly object _sync = new();

        private HelperClient<JobEvent, JobCommand> _client;
        private bool _started;
        private volatile bool _cancelRequested;

        private JobRunner(string stem, string what, IReadOnlyList<string> args, HelperStartOptions options)
        {
            _stem = stem;
            _what = what;
            _args = args;
            _options = options;
        }

        /// <summary>Raised on the UI thread for every <c>progress</c> event, 0-100.</summary>
        public event EventHandler<int> ProgressChanged;

        /// <summary>A GIF export. <paramref name="maxWidth"/>/<paramref name="maxHeight"/> of 0 mean "no
        /// cap" and omit the flag entirely: prisc_vid2gif's parser rejects 0 as a value.</summary>
        public static JobRunner ForVid2Gif(string inputPath, string outputPath, string quality, int maxWidth, int maxHeight)
        {
            var args = new List<string> { inputPath, outputPath, "--quality", quality };
            if (maxWidth > 0)
            {
                args.Add("--max-width");
                args.Add(maxWidth.ToString(CultureInfo.InvariantCulture));
            }

            if (maxHeight > 0)
            {
                args.Add("--max-height");
                args.Add(maxHeight.ToString(CultureInfo.InvariantCulture));
            }

            return new JobRunner(HelperNames.Vid2gif, "GIF conversion", args, new HelperStartOptions());
        }

        /// <summary>A render of an already-written render-args file (a serialized project plus its
        /// output/crf siblings, see <see cref="Prisc.VideoSDK.Editing.ProjectFileWriter"/>), which is the
        /// tool's single argument.</summary>
        public static JobRunner ForVideoRender(string renderArgsPath) => ForVideoRender(renderArgsPath, null);

        /// <summary><see cref="ForVideoRender(string)"/> with extra variables for the tool's
        /// environment: the tests' seam for the tool's test-only knobs.</summary>
        internal static JobRunner ForVideoRender(string renderArgsPath, IReadOnlyDictionary<string, string> extraEnvironment)
        {
            // The render tool finds FFmpeg beside the recorder itself, but hand it the directory we
            // already resolved so the two can never disagree. Harmless when there is none: the
            // tool's own lookup runs.
            var environment = new Dictionary<string, string>();
            var ffmpegDir = FFmpegLoader.FindBesideRecorder();
            if (!String.IsNullOrEmpty(ffmpegDir))
                environment[Prisc.Interop.EnvVars.FfmpegPath] = ffmpegDir;
            if (extraEnvironment != null)
            {
                foreach (var (name, value) in extraEnvironment)
                    environment[name] = value;
            }

            return new JobRunner(HelperNames.VideoRender, "video render", new[] { renderArgsPath },
                new HelperStartOptions { Environment = environment });
        }

        /// <summary>
        /// Spawns the helper and resolves once it has exited, with how the run ended. May only be
        /// called once. Throws <see cref="FileNotFoundException"/> when the helper is missing and
        /// <see cref="HelperStartException"/> when it cannot be started; every other failure comes back
        /// as a <see cref="JobOutcome.Error"/> result.
        /// </summary>
        public async Task<JobRunResult> RunAsync()
        {
            lock (_sync)
            {
                if (_started)
                    throw new InvalidOperationException("RunAsync may only be called once per JobRunner.");
                _started = true;
            }

            var exePath = HelperLocator.Find(_stem);
            if (exePath == null)
            {
                var name = Prisc.Interop.NativeProbe.ExeName(_stem);
                throw new FileNotFoundException(
                    $"Could not find {name}. It ships with Prisc; set {Prisc.Interop.EnvVars.HelpersDir} to the folder holding it to override.",
                    name);
            }

            var client = await HelperClient<JobEvent, JobCommand>.StartAsync(HelperSpecs.Job, exePath, _args, _options);
            bool cancelledWhileStarting;
            lock (_sync)
            {
                // published and checked under the lock CancelAsync and DisposeAsync decide under, so a
                // cancel either sees the client or is seen here, never neither
                _client = client;
                cancelledWhileStarting = _cancelRequested;
            }

            try
            {
                // a cancel that arrived while the helper was still starting had nobody to tell yet
                if (cancelledWhileStarting)
                    _ = client.StopAsync();

                await foreach (var ev in client.Events.ReadAllAsync())
                {
                    if (ev is JobEvent.Progress progress)
                    {
                        var percent = (int)Math.Min(progress.Percent, 100u);
                        Dispatcher.UIThread.Post(() => ProgressChanged?.Invoke(this, percent));
                    }
                }

                return Map(await client.Completion);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        private JobRunResult Map(HelperOutcome<JobEvent> outcome)
        {
            switch (outcome.Terminal)
            {
                case JobEvent.Done { Result: JobResult.Ok } done when outcome.Failure == HelperFailure.None:
                    return JobRunResult.Success(done.Path, (long)(done.Bytes ?? 0));
                case JobEvent.Done { Result: JobResult.Cancelled } when outcome.Failure == HelperFailure.None:
                    return JobRunResult.Canceled();
            }

            // the user asked for this one: a helper that had to be killed on its way out still ended
            // the way they wanted
            if (_cancelRequested && outcome.Failure == HelperFailure.Timeout)
            {
                Debug.WriteLine($"The canceled {_what} had to be killed.");
                return JobRunResult.Canceled();
            }

            var message = outcome.Failure == HelperFailure.FatalError
                ? outcome.Error?.Message
                : $"The {_what} process exited unexpectedly ({outcome.Describe()}).";
            return JobRunResult.Error(message, outcome.StderrTail);
        }

        /// <summary>Asks the job to stop (<c>quit</c>) and waits for the helper to go away, killing it
        /// after the catalog's grace. The run itself still resolves through <see cref="RunAsync"/>,
        /// normally with <see cref="JobOutcome.Canceled"/>.</summary>
        public async Task CancelAsync()
        {
            HelperClient<JobEvent, JobCommand> client;
            lock (_sync)
            {
                _cancelRequested = true;
                client = _client;
            }

            // RunAsync stops it itself once it exists
            if (client != null)
                await client.StopAsync();
        }

        /// <summary>The helper's recent stderr (bounded).</summary>
        public string GetLog()
        {
            lock (_sync)
                return _client?.StderrTail ?? "";
        }

        /// <summary>Non-blocking best-effort shutdown; callers that can wait should <c>await</c>
        /// <see cref="DisposeAsync"/>.</summary>
        public void Dispose()
        {
            _ = DisposeAsync();
        }

        /// <summary>Stops the helper if it is still running (a quit, so it removes its own partial
        /// output); <see cref="RunAsync"/> releases it on its way out. Idempotent.</summary>
        public Task DisposeAsync()
        {
            lock (_sync)
            {
                _cancelRequested = true;
                return _client == null ? Task.CompletedTask : _client.StopAsync();
            }
        }
    }
}
