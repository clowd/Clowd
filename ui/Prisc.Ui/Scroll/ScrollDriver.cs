using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using Prisc.Ipc;
using Prisc.PlatformUtil;

namespace Prisc.UI
{
    /// <summary>Progress from the driver, one per phase change of every scroll step.
    /// <paramref name="ResumeInS"/> is meaningful only while <paramref name="State"/> is
    /// <see cref="ScrollState.Resuming"/>, where it counts whole seconds down to the run restarting.</summary>
    internal sealed record ScrollProgress(int Frames, int HeightPx, ScrollState State, int ResumeInS);

    /// <summary>
    /// How a driver run ended. <see cref="Result"/> is the <c>done</c> result, null when the driver
    /// ended without one (a fatal error, whose text is <see cref="Message"/>, or a crash).
    /// <see cref="Clean"/> is true only for a run that ended with <c>done</c>.
    /// </summary>
    internal sealed record ScrollDriverOutcome(ScrollResult? Result, string Message, int Frames, int HeightPx, int? ExitCode, bool Clean);

    /// <summary>
    /// Hosts one run of the <c>prisc_scroll_driver</c> process over the <c>scroll</c> catalog
    /// (HELPER_PROTOCOL.md §4.4). Exactly one run per instance: <see cref="RunAsync"/> resolves when
    /// the process has ended. <see cref="Stop"/> finishes the run and keeps what was captured;
    /// <see cref="ShutdownAsync"/> is the quit that throws it away. Events are raised on the UI
    /// thread.
    /// </summary>
    internal sealed class ScrollDriver
    {
        // the driver logs a line or two per scroll step, and a capped run is 120 steps: this only
        // needs to survive long enough to explain a failure.
        private static readonly HelperStartOptions Options = new()
        {
            // the driver foregrounds the window it is about to scroll, which a freshly spawned
            // process is refused without our rights (Win10+ routes the wheel by cursor position
            // regardless, so this is best-effort).
            GrantForeground = true,
            StderrLines = 500,
            StderrChars = 128 * 1024,
        };

        private readonly object _sync = new();
        private HelperClient<ScrollEvent, ScrollCommand> _client;
        private bool _started;
        private Task _shutdownTask;

        /// <summary>Raised on the UI thread for every <c>status</c> event.</summary>
        public event EventHandler<ScrollProgress> StatusReceived;

        /// <summary>
        /// Spawns the driver and resolves when it has ended. Never throws for an outcome the caller
        /// can act on: the returned outcome, not an exception, distinguishes success from failure.
        /// Only the start itself throws (<see cref="HelperStartException"/>).
        /// </summary>
        public async Task<ScrollDriverOutcome> RunAsync(string binary, string sessionDir, ScreenRect region,
                                                        ScreenPoint point, long targetHwnd, bool rewindToTop)
        {
            lock (_sync)
            {
                if (_started)
                    throw new InvalidOperationException("A ScrollDriver drives exactly one capture.");
                _started = true;
            }

            var client = await HelperClient<ScrollEvent, ScrollCommand>.StartAsync(HelperSpecs.Scroll, binary,
                BuildArguments(sessionDir, region, point, targetHwnd, rewindToTop), Options);

            lock (_sync)
            {
                _client = client;
                if (_shutdownTask != null)
                    _ = client.StopAsync();
            }

            int frames = 0, heightPx = 0;
            await foreach (var ev in client.Events.ReadAllAsync())
            {
                switch (ev)
                {
                    case ScrollEvent.Ready:
                        // nothing to do: the border is already up, and the driver waits out its own
                        // start delay before the first frame. Logged so a run that never got this far
                        // is distinguishable from one that did.
                        Debug.WriteLine("Scrolling capture driver is ready.");
                        break;

                    case ScrollEvent.Status status:
                        frames = (int)status.Frames;
                        heightPx = (int)status.HeightPx;
                        var progress = new ScrollProgress(frames, heightPx, status.State, (int)(status.ResumeInS ?? 0));
                        Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, progress));
                        break;
                }
            }

            var outcome = await client.Completion;
            await client.DisposeAsync();

            return outcome.Terminal switch
            {
                ScrollEvent.Done done => new ScrollDriverOutcome(done.Result, null, (int)done.Frames, (int)done.HeightPx,
                    outcome.ExitCode, outcome.Failure == HelperFailure.None),
                _ => new ScrollDriverOutcome(null, outcome.Error?.Message, frames, heightPx, outcome.ExitCode, false),
            };
        }

        /// <summary>
        /// The exact command line the driver is spawned with. Factored out so the argument
        /// contract is readable in one place: the driver rejects a missing or malformed
        /// <c>--region</c>/<c>--point</c> outright, after <c>hello</c>, as a fatal
        /// <c>error{code:"usage"}</c> and exit 2. Only clap's own rejection exits 2 with no protocol
        /// line beyond <c>hello</c>.
        /// <para>Every numeric option is emitted as a single <c>--name=value</c> token. A monitor
        /// left of or above the primary has a negative virtual-desktop origin, and clap reads a
        /// separate value token that begins with '-' as an unknown flag, and the run would die with
        /// a usage error and no protocol line beyond <c>hello</c>. <c>--session-dir</c> stays a
        /// two-token pair: a path never starts with '-'.</para>
        /// </summary>
        internal static IReadOnlyList<string> BuildArguments(string sessionDir, ScreenRect region, ScreenPoint point,
                                                             long targetHwnd, bool rewindToTop)
        {
            static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

            var args = new List<string>
            {
                "--session-dir", sessionDir,
                // physical virtual-desktop px, the same space the action.txt marker used.
                "--region=" + String.Join(",", Num(region.X), Num(region.Y), Num(region.Width), Num(region.Height)),
                "--point=" + String.Join(",", Num(point.X), Num(point.Y)),
                // 0 means "the overlay could not resolve one"; the driver falls back to
                // WindowFromPoint. It re-validates a non-zero handle either way.
                "--hwnd=" + Num(targetHwnd),
            };

            // The driver rewinds by default, so only the opt-out is ever passed: the flag is
            // negative on both sides so the default lives in one place.
            if (!rewindToTop)
                args.Add("--no-rewind");

            return args;
        }

        /// <summary>Finishes the run now and keeps what has been captured (<c>stop</c>). Best-effort: a
        /// driver that has already ended is the state the command was trying to reach.</summary>
        public void Stop()
        {
            HelperClient<ScrollEvent, ScrollCommand> client;
            lock (_sync)
                client = _client;
            client?.Send(new ScrollCommand.Stop());
        }

        /// <summary>
        /// Ends the run and throws it away: <c>quit</c>, which the driver answers with
        /// <c>done{cancelled}</c> and writes nothing, then the catalog's grace, then a kill. Quit
        /// rather than stop: a shutdown means nobody is left to route a finished session anywhere.
        /// Idempotent; every caller awaits the same task.
        /// </summary>
        public Task ShutdownAsync()
        {
            lock (_sync)
            {
                if (_shutdownTask == null)
                {
                    var client = _client;
                    _shutdownTask = client == null ? Task.CompletedTask : client.StopAsync();
                }

                return _shutdownTask;
            }
        }

        /// <summary>The tail of the driver's output, for an error dialog and report: whatever went
        /// wrong is always at the end, after the routine startup lines.</summary>
        public string GetLogTail(int maxLines = 15)
        {
            lock (_sync)
                return _client?.Log.GetLogTail(maxLines);
        }
    }
}
