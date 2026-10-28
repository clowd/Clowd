using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Prisc.Ipc;

namespace Prisc.UI
{
    /// <summary>What the recorder reported about the file it is writing: the tracks (stream index and
    /// size of each video track, what each audio track carries) and the sidecar paths it echoed back.
    /// The video editor needs the tracks to place the webcam overlay and label the audio rows; only
    /// the recorder knows which audio track is the microphone. Read together because the three
    /// describe the same take.</summary>
    internal sealed record RecorderReport(RecorderTracks Tracks, string InputCapturePath, string WindowCapturePath)
    {
        /// <summary>
        /// The report after <paramref name="ev"/>: <c>started_recording</c> and <c>done</c> both carry
        /// one. A message without a usable <c>tracks</c> object (no screen track) leaves
        /// <paramref name="previous"/> alone rather than clearing it: <c>done</c> is the second report,
        /// and losing the first because the second was thinner would cost the editor its labels. An
        /// empty sidecar path reads as none: the recorder omits the key when it was not asked for one.
        /// </summary>
        internal static RecorderReport Merge(RecorderReport previous, RecorderEvent ev)
        {
            var (tracks, input, window) = ev switch
            {
                RecorderEvent.StartedRecording s => (s.Tracks, s.InputCapture, s.WindowCapture),
                RecorderEvent.Done d => (d.Tracks, d.InputCapture, d.WindowCapture),
                _ => (null, null, null),
            };

            if (tracks?.Screen == null)
                return previous;

            return new RecorderReport(tracks, NullIfEmpty(input), NullIfEmpty(window));
        }

        /// <summary>The audio tracks as the session stores them, or null when the recorder named
        /// none. There is deliberately no probe fallback: the file says how many audio streams it has,
        /// but not which one is the microphone, and that is all this is for.</summary>
        internal SessionAudioTrack[] ToSessionAudioTracks()
        {
            var audio = Tracks?.Audio;
            if (audio == null || audio.Count == 0)
                return null;

            var tracks = new SessionAudioTrack[audio.Count];
            for (var i = 0; i < tracks.Length; i++)
            {
                tracks[i] = new SessionAudioTrack
                {
                    Index = (int)audio[i].Index,
                    // the wire spelling (speaker, microphone, mixed); unnamed when the recorder did not say
                    Kind = audio[i].Kind == RecorderAudioKind.Unknown ? null : audio[i].Kind.ToString().ToLowerInvariant(),
                };
            }

            return tracks;
        }

        private static string NullIfEmpty(string value) => String.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>The recorder's answer to a <c>configure</c> command: either <c>configure_applied</c>,
    /// whose <see cref="IgnoredKeys"/> names the settings the recorder refused to change in its current
    /// phase (empty before recording starts), or <c>configure_error</c>, whose
    /// <see cref="RestartRequired"/> means the pipeline may match neither the old nor the new
    /// settings.</summary>
    internal sealed record RecorderConfigureResult(bool Applied, IReadOnlyList<string> IgnoredKeys, string Message, bool RestartRequired);

    /// <summary>
    /// Hosts the prisc_recorder recording process over the <c>recorder</c> catalog
    /// (HELPER_PROTOCOL.md §4.1). Lifecycle: <see cref="InitializeAsync"/> (spawns with
    /// <c>--pause</c>, resolves on <c>initialized</c>) → <see cref="StartAsync"/> (resolves on
    /// <c>started_recording</c>) → <see cref="StopAsync"/> (<c>quit</c>, resolves once the process has
    /// ended). A <c>done{failed}</c> may arrive spontaneously at any time after recording starts (disk
    /// full, encoder error), and any exit without a clean <c>done</c> is fatal, including one
    /// <see cref="StopAsync"/> had to kill: all of these surface through <see cref="CriticalError"/>.
    /// Events are raised on the UI thread.
    /// <para>
    /// The recorder is the one helper not in the kill-on-close job: if Prisc.Ui dies, stdin EOF makes
    /// the recorder stop and flush the mp4, so the recording up to that point is kept instead of
    /// truncated.
    /// </para>
    /// </summary>
    internal sealed class RecorderDriver : IDisposable
    {
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        // a configure only re-reads a small file and touches the (not yet running) pipeline; if it
        // has not been acked by now the child is wedged and the page respawns it.
        private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromSeconds(10);
        // a pause/resume only flips the output's pause flag; an unacked one means a wedged child.
        private static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(10);

        private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();

        private HelperClient<RecorderEvent, RecorderCommand> _client;
        private RecorderReport _report;
        private Task<bool> _ended;
        private int _criticalRaised;
        private volatile bool _disposed;
        private Task _disposeTask;
        private TaskCompletionSource _pendingDispose;

        /// <summary>Raised on the UI thread for every <c>status</c> event while recording.</summary>
        public event EventHandler<RecorderEvent.Status> StatusReceived;

        /// <summary>Raised on the UI thread for every <c>levels</c> event (10 Hz from
        /// <c>initialized</c> onward, only when audio sources exist): dBFS per source in CLI order,
        /// always finite, floored at -100.</summary>
        public event EventHandler<RecorderEvent.Levels> LevelsReceived;

        /// <summary>Raised on the UI thread, once, when the recording fails: a <c>done{failed}</c>
        /// (possibly spontaneous), a fatal error, or an exit without a clean <c>done</c>.</summary>
        public event EventHandler<string> CriticalError;

        /// <summary>The recorder's last report (<c>started_recording</c>, then <c>done</c>), or null
        /// before it sent one. Read from the UI thread after a lifecycle call has resolved, which is
        /// what orders it after the write.</summary>
        public RecorderReport LastReport => Volatile.Read(ref _report);

        /// <summary>Starts the recorder and resolves when its pipeline is fully built
        /// (<c>initialized</c>). Throws <see cref="TimeoutException"/> after 30 s (first-run OBS init
        /// can be slow, but not that slow) and <see cref="HelperStartException"/> when the process
        /// cannot be started or speaks the wrong protocol.</summary>
        public async Task InitializeAsync(IReadOnlyList<string> args, string exePath)
        {
            lock (_sync)
            {
                if (_client != null || _ended != null)
                    throw new InvalidOperationException("InitializeAsync may only be called once per RecorderDriver.");
                _ended = Task.FromResult(false); // claimed; replaced below once the client exists
            }

            HelperClient<RecorderEvent, RecorderCommand> client;
            try
            {
                client = await HelperClient<RecorderEvent, RecorderCommand>.StartAsync(HelperSpecs.Recorder, exePath, args,
                    new HelperStartOptions { KillWithParent = false });
            }
            catch
            {
                // StartAsync has already disposed whatever it spawned
                Volatile.Read(ref _pendingDispose)?.TrySetResult();
                throw;
            }

            TaskCompletionSource pendingDispose;
            bool stopNow;
            lock (_sync)
            {
                _client = client;
                _ended = Task.Run(() => PumpAsync(client));
                pendingDispose = _pendingDispose;
                stopNow = _disposed;
            }

            // a dispose that arrived during the start resolves only once this client is gone, so
            // its awaiters never overlap a recorder that is still flushing
            if (stopNow)
                _ = Task.Run(async () =>
                {
                    await client.DisposeAsync();
                    pendingDispose?.TrySetResult();
                });

            try
            {
                await _initTcs.Task.WaitAsync(InitializeTimeout);
            }
            catch (TimeoutException)
            {
                throw client.Log.Attach(new TimeoutException(
                    $"The recording process did not send 'initialized' within {InitializeTimeout.TotalSeconds:0} s."));
            }
            catch (Exception ex)
            {
                client.Log.Attach(ex);
                throw;
            }
        }

        /// <summary>Starts the recording; resolves on <c>started_recording</c>, i.e. when frames are
        /// actually flowing. Throws <see cref="TimeoutException"/> after 10 s.</summary>
        public async Task StartAsync()
        {
            var reply = await WaitForReplyAsync(new RecorderCommand.Start(),
                e => e is RecorderEvent.StartedRecording or RecorderEvent.RecordingResumed, StartTimeout, "started_recording");

            // applied here as well as by the event pump, so a caller can read LastReport straight
            // after the await (the reply resolves before the pump sees the same event)
            ApplyReport(reply);
        }

        /// <summary>Pauses the recording; resolves on <c>recording_paused</c>. The recorder keeps
        /// running (levels keep flowing) but no frames are written and <c>status</c> events stop.
        /// Throws <see cref="TimeoutException"/> after 10 s.</summary>
        public Task PauseAsync() => WaitForReplyAsync(new RecorderCommand.Pause(),
            e => e is RecorderEvent.RecordingPaused, PauseTimeout, "recording_paused");

        /// <summary>Resumes a paused recording; resolves on <c>recording_resumed</c> (the command is
        /// <c>start</c>, which the recorder reuses for resume).</summary>
        public Task ResumeAsync() => WaitForReplyAsync(new RecorderCommand.Start(),
            e => e is RecorderEvent.RecordingResumed or RecorderEvent.StartedRecording, PauseTimeout, "recording_resumed");

        /// <summary>
        /// Points the recorder at a rewritten settings file and waits for it to apply the change,
        /// which replaces a tear-down-and-respawn cycle for settings edited during WAIT. Throws
        /// <see cref="TimeoutException"/> after 10 s and <see cref="InvalidOperationException"/> if the
        /// process has already exited; callers treat any failure (thrown or a non-applied result) as
        /// "respawn the recorder".
        /// </summary>
        public async Task<RecorderConfigureResult> ConfigureAsync(string settingsPath)
        {
            var reply = await WaitForReplyAsync(new RecorderCommand.Configure(settingsPath),
                e => e is RecorderEvent.ConfigureApplied or RecorderEvent.ConfigureError, ConfigureTimeout, "configure_applied");

            return reply switch
            {
                RecorderEvent.ConfigureApplied applied => new RecorderConfigureResult(true, applied.IgnoredKeys ?? Array.Empty<string>(), null, false),
                RecorderEvent.ConfigureError error => new RecorderConfigureResult(false, Array.Empty<string>(),
                    String.IsNullOrEmpty(error.Message) ? "The recorder rejected the new settings." : error.Message, error.RestartRequired),
                _ => throw new InvalidOperationException("Unexpected configure reply."),
            };
        }

        /// <summary>Mutes/unmutes the first speaker source (index 0, one device each). Safe to call
        /// while recording; the recorder never acks it.</summary>
        public void SetSpeakerMute(bool mute) =>
            Send(mute ? new RecorderCommand.MuteSpeaker(0) : new RecorderCommand.UnmuteSpeaker(0));

        /// <summary>Mutes/unmutes the first microphone source (index 0).</summary>
        public void SetMicrophoneMute(bool mute) =>
            Send(mute ? new RecorderCommand.MuteMic(0) : new RecorderCommand.UnmuteMic(0));

        /// <summary>Stops the recording and flushes the mp4 (<c>quit</c>, then the catalog's grace for
        /// the stop and the flush, then a kill). Returns true on a clean stop (<c>done</c> stopped, or
        /// cancelled before start); false when the recording failed, in which case
        /// <see cref="CriticalError"/> has been raised.</summary>
        public async Task<bool> StopAsync()
        {
            HelperClient<RecorderEvent, RecorderCommand> client;
            Task<bool> ended;
            lock (_sync)
            {
                client = _client;
                ended = _ended;
            }

            if (client == null)
                return false;

            await client.StopAsync();
            return await ended;
        }

        /// <summary>The recorder's recent stderr (bounded), for the error-log file written on
        /// <see cref="CriticalError"/>.</summary>
        public string GetLog()
        {
            lock (_sync)
                return _client?.StderrTail ?? "";
        }

        /// <summary>Non-blocking best-effort shutdown for safety-net callers; UI-thread code should
        /// <c>await</c> <see cref="DisposeAsync"/> instead.</summary>
        public void Dispose()
        {
            _ = DisposeAsync();
        }

        /// <summary>Shuts the recorder down (a quit, so a running recording is flushed) on the thread
        /// pool. Nothing is reported once this has been called. Idempotent.</summary>
        public Task DisposeAsync()
        {
            lock (_sync)
            {
                _disposed = true;
                if (_disposeTask != null)
                    return _disposeTask;
                if (_client is { } client)
                    return _disposeTask = Task.Run(() => client.DisposeAsync().AsTask());
                if (_ended == null)
                    return _disposeTask = Task.CompletedTask;

                // InitializeAsync is still waiting for hello; it completes this once it has the client
                _pendingDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _disposeTask = _pendingDispose.Task;
            }
        }

        private async Task<RecorderEvent> WaitForReplyAsync(RecorderCommand command, Func<RecorderEvent, bool> isReply,
            TimeSpan timeout, string expected)
        {
            HelperClient<RecorderEvent, RecorderCommand> client;
            lock (_sync)
                client = _client;
            if (client == null)
                throw new InvalidOperationException("The recording process is not running.");

            try
            {
                return await client.SendAndWaitAsync(command, isReply, timeout);
            }
            catch (TimeoutException)
            {
                throw client.Log.Attach(new TimeoutException(
                    $"The recording process did not send '{expected}' within {timeout.TotalSeconds:0} s."));
            }
            catch (Exception ex)
            {
                client.Log.Attach(ex);
                throw;
            }
        }

        private void Send(RecorderCommand command)
        {
            HelperClient<RecorderEvent, RecorderCommand> client;
            lock (_sync)
                client = _client;
            if (client != null && !client.Send(command))
                Debug.WriteLine($"Recording process is not accepting commands; dropped {command.GetType().Name}.");
        }

        private void ApplyReport(RecorderEvent ev)
        {
            RecorderReport current, next;
            do
            {
                current = Volatile.Read(ref _report);
                next = RecorderReport.Merge(current, ev);
            } while (!ReferenceEquals(current, next) && Interlocked.CompareExchange(ref _report, next, current) != current);
        }

        /// <summary>Reads every event, then turns how the run ended into the stop result. Resolves
        /// true only for a clean <c>done</c>.</summary>
        private async Task<bool> PumpAsync(HelperClient<RecorderEvent, RecorderCommand> client)
        {
            try
            {
                await foreach (var ev in client.Events.ReadAllAsync())
                    Handle(ev);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recording event pump failed: " + ex);
                SentryConfig.CaptureHandled(client.Log.Attach(ex), "recorder.event-pump");
            }

            var outcome = await client.Completion;
            if (outcome.Terminal is RecorderEvent done)
                ApplyReport(done);

            var clean = outcome.Failure == HelperFailure.None
                        && outcome.Terminal is RecorderEvent.Done { Result: not RecorderResult.Failed };

            var died = client.Log.Attach(new InvalidOperationException(
                $"The recording process has exited unexpectedly ({outcome.Describe()})."));
            if (_initTcs.TrySetException(died))
                _ = _initTcs.Task.Exception;

            if (!clean)
            {
                Debug.WriteLine($"Recording ended badly: {outcome.Describe()}");
                RaiseCriticalError(outcome.Failure == HelperFailure.FatalError && outcome.Error != null
                    ? outcome.Error.Message
                    : outcome.Failure == HelperFailure.Timeout
                        ? "The recording process did not stop in time and was killed."
                        : outcome.Terminal is RecorderEvent.Done failed
                            ? Describe(failed)
                            : "The recording process has exited unexpectedly.");
            }

            return clean;
        }

        private void Handle(RecorderEvent ev)
        {
            switch (ev)
            {
                case RecorderEvent.Initialized:
                    _initTcs.TrySetResult(true);
                    break;

                case RecorderEvent.StartedRecording:
                    ApplyReport(ev);
                    break;

                case RecorderEvent.Status status:
                    Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, status));
                    break;

                case RecorderEvent.Levels levels:
                    Dispatcher.UIThread.Post(() => LevelsReceived?.Invoke(this, levels));
                    break;

                case RecorderEvent.Done done:
                    // the final word on what was written; the page reads it once the stop resolves
                    ApplyReport(done);
                    Debug.WriteLine($"Recording stopped: result={done.Result} code={done.Code} message={done.Message} detail={done.Detail}");
                    // may arrive spontaneously mid-recording (disk full, encoder error): report it
                    // now rather than when the process gets round to exiting
                    if (done.Result == RecorderResult.Failed)
                        RaiseCriticalError(Describe(done));
                    break;

                case RecorderEvent.Error { Fatal: false } error:
                    // a refusal no one waited for; the mute commands never produce one (§4.1)
                    Debug.WriteLine($"Recording process refused a command: {error.Code}: {error.Message}");
                    break;
            }
        }

        private static string Describe(RecorderEvent.Done done) =>
            String.IsNullOrEmpty(done.Detail) ? done.Message : done.Message + "\n" + done.Detail;

        private void RaiseCriticalError(string message)
        {
            if (_disposed || Interlocked.Exchange(ref _criticalRaised, 1) != 0)
                return;
            Dispatcher.UIThread.Post(() => CriticalError?.Invoke(this, message));
        }
    }
}
