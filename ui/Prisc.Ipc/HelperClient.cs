using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Prisc.Ipc
{
    /// <summary>
    /// The stdio of one child process as <see cref="HelperClient{TEvent, TCommand}"/> sees it: three
    /// text streams, a task that completes with the exit code when the process exits, and a tree
    /// kill. <see cref="FromProcess"/> wraps a real process; tests build one over in-memory streams.
    /// </summary>
    internal sealed class HelperChild
    {
        public int ProcessId { get; init; }
        public TextReader Stdout { get; init; }
        public TextReader Stderr { get; init; }
        public TextWriter Stdin { get; init; }

        /// <summary>Completes when the process exits, with its exit code (null when it cannot be read).
        /// Never faults.</summary>
        public Task<int?> Exit { get; init; }

        /// <summary>Kills the whole process tree. Must not throw.</summary>
        public Action Kill { get; init; }

        /// <summary>Releases the process object once the pumps are off its streams. May be null.</summary>
        public Action Release { get; init; }

        public static HelperChild FromProcess(Process process) => new()
        {
            ProcessId = process.Id,
            Stdout = process.StandardOutput,
            Stderr = process.StandardError,
            Stdin = process.StandardInput,
            Exit = WaitForExitAsync(process),
            Kill = () =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to kill helper process: " + ex.Message);
                }
            },
            Release = process.Dispose,
        };

        private static async Task<int?> WaitForExitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Waiting for a helper process failed: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// One running helper, spoken to over the line protocol (HELPER_PROTOCOL.md §7.2): starts it, waits
    /// for <c>hello</c> and checks it against the catalog, delivers every event in order on
    /// <see cref="Events"/>, matches command replies without taking them off that channel, and turns
    /// the way the process ended into one <see cref="HelperOutcome{TEvent}"/>.
    /// <para>
    /// One rule for every helper: a run ends with exactly one terminal event or a fatal error, and
    /// an exit without one is a failure (<see cref="HelperFailure.Crashed"/>, or a specific exit-code
    /// failure), whether or not we asked it to stop. A stop that had to kill is
    /// <see cref="HelperFailure.Timeout"/>.
    /// </para>
    /// </summary>
    public sealed class HelperClient<TEvent, TCommand> : IAsyncDisposable
        where TEvent : class where TCommand : class
    {
        /// <summary>How long the stdio pumps get to reach EOF after the process exits. A grandchild
        /// that inherited a pipe (obs-ffmpeg-mux, a browser) can hold it open indefinitely; that must
        /// not hang <see cref="Completion"/>. Settable for tests only.</summary>
        internal TimeSpan PumpDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>How long a killed process gets to actually go away before <see cref="StopAsync"/>
        /// gives up on it. Settable for tests only.</summary>
        internal TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>The wait between <c>quit</c> and kill: the catalog's, unless a test shortens it.</summary>
        internal TimeSpan QuitGrace { get; init; }

        private readonly HelperSpec<TEvent, TCommand> _spec;
        private readonly HelperChild _child;
        private readonly HelperStartOptions _options;
        private readonly Channel<TEvent> _events = Channel.CreateUnbounded<TEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private readonly TaskCompletionSource<IpcHello> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _eventLock = new();
        private readonly object _stdinLock = new();
        private readonly object _stopLock = new();

        private Task _stdoutPump;
        private Task _stderrPump;
        private PendingReply _pending;
        private TEvent _terminal;
        private bool _drainAbandoned;
        private bool _ended;
        private bool _stdinClosed;
        private volatile bool _killedByStop;
        private volatile bool _killRequested;
        private Task<HelperOutcome<TEvent>> _stopTask;
        private Task _disposeTask;

        internal HelperClient(HelperSpec<TEvent, TCommand> spec, HelperChild child, HelperStartOptions options)
        {
            _spec = spec ?? throw new ArgumentNullException(nameof(spec));
            _child = child ?? throw new ArgumentNullException(nameof(child));
            _options = options ?? new HelperStartOptions();
            Log = new HelperLog(_options.StderrLines, _options.StderrChars);
            ProcessId = child.ProcessId;
            QuitGrace = spec.QuitGrace;
        }

        /// <summary>
        /// Starts <paramref name="exePath"/> and completes once it has said a matching <c>hello</c>.
        /// Throws <see cref="HelperStartException"/> (after killing the process) when it cannot be
        /// started, exits first, says nothing within <see cref="HelperStartOptions.HelloTimeout"/>, or
        /// names another protocol, catalog or catalog hash.
        /// </summary>
        public static Task<HelperClient<TEvent, TCommand>> StartAsync(
            HelperSpec<TEvent, TCommand> spec, string exePath, IReadOnlyList<string> args,
            HelperStartOptions options = null, CancellationToken ct = default)
        {
            options ??= new HelperStartOptions();
            var log = new HelperLog(options.StderrLines, options.StderrChars);

            Process process;
            try
            {
                process = HelperProcess.Start(exePath, args, options, log);
            }
            catch (Exception ex)
            {
                throw new HelperStartException(HelperFailure.StartFailed,
                    $"Could not start {Path.GetFileName(exePath)}: {ex.Message}", inner: ex);
            }

            var client = new HelperClient<TEvent, TCommand>(spec, HelperChild.FromProcess(process), options);
            // a job-assignment note written before the client existed belongs in its log
            var early = log.GetLog();
            if (early.Length > 0)
                client.Log.Append(early);
            return StartAsync(client, Path.GetFileName(exePath), ct);
        }

        /// <summary>The handshake half of <see cref="StartAsync(HelperSpec{TEvent, TCommand}, string, IReadOnlyList{string}, HelperStartOptions, CancellationToken)"/>,
        /// shared with the tests' in-memory children.</summary>
        internal static async Task<HelperClient<TEvent, TCommand>> StartAsync(HelperClient<TEvent, TCommand> client,
            string name, CancellationToken ct)
        {
            client.Begin();

            Exception failure;
            try
            {
                var winner = await Task.WhenAny(client._hello.Task, client.Completion)
                    .WaitAsync(client._options.HelloTimeout, ct).ConfigureAwait(false);
                if (winner == client._hello.Task)
                {
                    client.Hello = await client._hello.Task.ConfigureAwait(false);
                    return client;
                }

                // exited (or ended) before hello: a specific exit-code failure if there is one
                var outcome = await client.Completion.ConfigureAwait(false);
                var kind = outcome.Failure is HelperFailure.NoScreenPermission or HelperFailure.CaptureFailed
                    or HelperFailure.InferenceUnavailable
                    ? outcome.Failure
                    : HelperFailure.StartFailed;
                failure = client.Log.Attach(new HelperStartException(kind,
                    $"{name} exited before it was ready ({outcome.Describe()}).", outcome.ExitCode));
            }
            catch (TimeoutException)
            {
                failure = client.Log.Attach(new HelperStartException(HelperFailure.HelloTimeout,
                    $"{name} did not start within {client._options.HelloTimeout.TotalSeconds:0} s."));
            }
            catch (HelperStartException ex)
            {
                failure = client.Log.Attach(ex);
            }
            catch (Exception ex)
            {
                failure = client.Log.Attach(ex);
            }

            client.Kill();
            await client.DisposeAsync().ConfigureAwait(false);
            throw failure;
        }

        /// <summary>The helper's <c>hello</c>.</summary>
        public IpcHello Hello { get; private set; }

        public int ProcessId { get; }

        /// <summary>Every event, in arrival order, replies included. Completes at stdout EOF or when
        /// <see cref="Completion"/> resolves, whichever is first.</summary>
        public ChannelReader<TEvent> Events => _events.Reader;

        /// <summary>How the run ended; resolves once the process has exited and the pumps have drained
        /// (or the drain window has passed). Never faults.</summary>
        public Task<HelperOutcome<TEvent>> Completion { get; private set; }

        /// <summary>The helper's stderr and non-protocol stdout.</summary>
        public HelperLog Log { get; }

        /// <summary>A snapshot of <see cref="Log"/>.</summary>
        public string StderrTail => Log.GetLog();

        /// <summary>Starts the pumps and the completion tracking. Called once, by StartAsync.</summary>
        internal void Begin()
        {
            _stdoutPump = Task.Run(PumpStdoutAsync);
            _stderrPump = Task.Run(PumpStderrAsync);
            Completion = Task.Run(CompleteAsync);
        }

        /// <summary>Writes <paramref name="command"/> as one stdin line. False once stdin is closed (the
        /// child is gone, or <see cref="StopAsync"/> closed it). Use it for commands that never ack.</summary>
        public bool Send(TCommand command) => SendLine(_spec.Encode(command));

        /// <summary>
        /// Sends <paramref name="command"/> and completes with the first event that
        /// <paramref name="isReply"/> accepts. Fails with <see cref="HelperCommandException"/> when a
        /// non-fatal <c>bad_command</c>, <c>rejected</c> or <c>failed</c> error arrives first (commands
        /// are answered in order, so it belongs to this command), with
        /// <see cref="InvalidOperationException"/> when the run ends first, and with
        /// <see cref="TimeoutException"/> after <paramref name="timeout"/>. The reply is still written
        /// to <see cref="Events"/>. At most one may be outstanding; use it only for commands the
        /// catalog guarantees a reply to.
        /// </summary>
        public async Task<TEvent> SendAndWaitAsync(TCommand command, Func<TEvent, bool> isReply, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(isReply);
            var pending = new PendingReply(isReply);
            lock (_eventLock)
            {
                if (_pending != null)
                    throw new InvalidOperationException("A command is already waiting for its reply.");
                if (_ended || _terminal != null)
                    throw new InvalidOperationException("The helper has already ended.");
                // registered before the write, so a reply faster than this thread cannot be missed
                _pending = pending;
            }

            try
            {
                if (!SendLine(_spec.Encode(command)))
                    throw new InvalidOperationException("The helper is no longer accepting commands.");

                try
                {
                    return await pending.Task.WaitAsync(timeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"The helper did not answer within {timeout.TotalSeconds:0} s.");
                }
            }
            finally
            {
                lock (_eventLock)
                {
                    if (_pending == pending)
                        _pending = null;
                }
            }
        }

        /// <summary>
        /// Ends the run (HELPER_PROTOCOL.md §7.3): <c>quit</c>, close stdin, wait the catalog's quit
        /// grace, then kill the tree (the outcome is <see cref="HelperFailure.Timeout"/> from then on),
        /// wait a little more, then give up. Idempotent; resolves with the outcome.
        /// </summary>
        public Task<HelperOutcome<TEvent>> StopAsync()
        {
            lock (_stopLock)
                return _stopTask ??= Task.Run(StopCoreAsync);
        }

        private async Task<HelperOutcome<TEvent>> StopCoreAsync()
        {
            // after Kill() there is nobody left to ask
            if (!_child.Exit.IsCompleted && !_killRequested)
            {
                SendLine(IpcCodec.QuitLine);
                CloseStdin();

                if (await Task.WhenAny(_child.Exit, Task.Delay(QuitGrace)).ConfigureAwait(false) != _child.Exit)
                {
                    Debug.WriteLine($"Helper {_spec.Catalog} did not stop within {QuitGrace.TotalSeconds:0} s; killing it.");
                    _killedByStop = true;
                    _child.Kill();
                }
            }

            var settle = KillWait + PumpDrainTimeout;
            if (await Task.WhenAny(Completion, Task.Delay(settle)).ConfigureAwait(false) == Completion)
                return await Completion.ConfigureAwait(false);

            // even the kill did not take it down; report the timeout without waiting any longer
            return new HelperOutcome<TEvent>(Volatile.Read(ref _terminal), null, HelperFailure.Timeout, StderrTail);
        }

        /// <summary>Kills the process tree now, with no quit. The outcome is whatever the exit maps to.</summary>
        public void Kill()
        {
            _killRequested = true;
            CloseStdin();
            _child.Kill();
        }

        /// <summary><see cref="StopAsync"/>, then joins the pumps (bounded) and releases the process.</summary>
        public ValueTask DisposeAsync()
        {
            lock (_stopLock)
                _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                if (Completion != null)
                    await StopAsync().ConfigureAwait(false);
                await HelperProcess.JoinPumpsAsync(PumpDrainTimeout, _stdoutPump, _stderrPump).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Disposing a helper client failed: " + ex.Message);
            }
            finally
            {
                try { _child.Release?.Invoke(); }
                catch { }
            }
        }

        private bool SendLine(string line)
        {
            lock (_stdinLock)
            {
                if (_stdinClosed)
                    return false;

                try
                {
                    _child.Stdin.WriteLine(line);
                    _child.Stdin.Flush();
                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // the child is gone; commands are best-effort from here
                    Debug.WriteLine($"Failed to write to helper {_spec.Catalog}: {ex.Message}");
                    _stdinClosed = true;
                    return false;
                }
            }
        }

        private void CloseStdin()
        {
            lock (_stdinLock)
            {
                if (_stdinClosed)
                    return;
                _stdinClosed = true;
                try { _child.Stdin.Close(); }
                catch { }
            }
        }

        private async Task PumpStdoutAsync()
        {
            try
            {
                string line;
                while ((line = await _child.Stdout.ReadLineAsync().ConfigureAwait(false)) != null)
                    HandleLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Helper {_spec.Catalog} stdout pump failed: {ex.Message}");
            }

            // stdout EOF: every buffered line has been delivered. A missing hello is settled by the
            // exit (StartAsync maps its code), not here.
            _events.Writer.TryComplete();
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _child.Stderr.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    Log.Append(line);
                    Debug.WriteLine($"[{_spec.Catalog}] {line}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Helper {_spec.Catalog} stderr pump failed: {ex.Message}");
            }
        }

        private void HandleLine(string line)
        {
            if (line.Length == 0)
                return;

            if (!_hello.Task.IsCompleted)
            {
                HandleHello(line);
                return;
            }

            TEvent ev;
            try
            {
                ev = _spec.Decode(line);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // the stdout guard makes a non-protocol line a helper bug; keep it for the report
                ev = null;
            }

            if (ev == null)
            {
                Log.Append("[stdout] " + line);
                return;
            }

            lock (_eventLock)
            {
                if (_drainAbandoned)
                {
                    Log.Append("[stdout, after the drain window] " + line);
                    return;
                }

                // (1) a pending command's reply, or the error that answers it instead
                var pending = _pending;
                if (pending != null)
                {
                    if (Matches(pending, ev))
                    {
                        _pending = null;
                        pending.TrySetResult(ev);
                    }
                    else if (ev is IIpcError { Fatal: false } refusal && IsCommandRefusal(refusal.Code))
                    {
                        _pending = null;
                        pending.TrySetException(new HelperCommandException(refusal));
                    }
                }

                var terminal = IsTerminal(ev);
                if (terminal && _terminal == null)
                {
                    _terminal = ev;
                    if (_pending != null)
                    {
                        _pending.TrySetException(new InvalidOperationException("The helper ended before it answered."));
                        _pending = null;
                    }
                }

                // (2) every event goes to the reader, replies included
                _events.Writer.TryWrite(ev);
            }
        }

        private void HandleHello(string line)
        {
            IpcHello hello;
            try
            {
                hello = IpcCodec.DecodeHello(line);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                hello = null;
            }

            if (hello == null)
            {
                // nothing may precede hello; keep the line and carry on waiting for it
                Log.Append("[stdout, before hello] " + line);
                return;
            }

            if (hello.Protocol != IpcProtocol.Version || hello.Catalog != _spec.Catalog || hello.CatalogHash != _spec.CatalogHash)
            {
                _hello.TrySetException(new HelperStartException(HelperFailure.ProtocolMismatch,
                    $"{hello.Helper} speaks protocol {hello.Protocol}, catalog '{hello.Catalog}' ({hello.CatalogHash}); " +
                    $"Prisc expects protocol {IpcProtocol.Version}, catalog '{_spec.Catalog}' ({_spec.CatalogHash}). " +
                    "The helper does not belong to this build of Prisc."));
                _ = _hello.Task.Exception;
                return;
            }

            _hello.TrySetResult(hello);
        }

        private static bool Matches(PendingReply pending, TEvent ev)
        {
            try { return pending.IsReply(ev); }
            catch { return false; }
        }

        /// <summary>A terminal event, a fatal error, or an error with a code this build does not know
        /// (§2.2: an unknown code is treated as fatal).</summary>
        private bool IsTerminal(TEvent ev) =>
            _spec.IsTerminal(ev) || ev is IIpcError error && !IsKnownCode(error.Code);

        private static bool IsCommandRefusal(string code) =>
            code is IpcErrorCodes.BadCommand or IpcErrorCodes.Rejected or IpcErrorCodes.Failed;

        private static bool IsKnownCode(string code) =>
            code is IpcErrorCodes.BadCommand or IpcErrorCodes.Rejected or IpcErrorCodes.Usage
                or IpcErrorCodes.InitFailed or IpcErrorCodes.IoFailed or IpcErrorCodes.Permission
                or IpcErrorCodes.Failed;

        private async Task<HelperOutcome<TEvent>> CompleteAsync()
        {
            int? exitCode = null;
            try
            {
                exitCode = await _child.Exit.ConfigureAwait(false);

                // bounded drain: the exit has closed our ends unless a grandchild inherited them
                var pumps = Task.WhenAll(_stdoutPump, _stderrPump);
                if (await Task.WhenAny(pumps, Task.Delay(PumpDrainTimeout)).ConfigureAwait(false) != pumps)
                {
                    Log.Append("[prisc] the helper's output stayed open after it exited; no longer reading it");
                    Debug.WriteLine($"Helper {_spec.Catalog} pumps did not drain within {PumpDrainTimeout.TotalSeconds:0} s.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Helper {_spec.Catalog} completion tracking failed: {ex.Message}");
            }

            TEvent terminal;
            lock (_eventLock)
            {
                _drainAbandoned = true;
                _ended = true;
                terminal = _terminal;
                if (_pending != null)
                {
                    _pending.TrySetException(new InvalidOperationException("The helper exited before it answered."));
                    _pending = null;
                }
            }

            _events.Writer.TryComplete();
            lock (_stdinLock)
                _stdinClosed = true;

            return new HelperOutcome<TEvent>(terminal, exitCode, Classify(terminal, exitCode), StderrTail);
        }

        /// <summary>§7.2's mapping, first match wins.</summary>
        private HelperFailure Classify(TEvent terminal, int? exitCode)
        {
            if (_killedByStop)
                return HelperFailure.Timeout;
            if (terminal != null)
                return terminal is IIpcError ? HelperFailure.FatalError : HelperFailure.None;
            return exitCode switch
            {
                HelperExit.NoScreenPermission => HelperFailure.NoScreenPermission,
                HelperExit.CaptureFailed => HelperFailure.CaptureFailed,
                HelperExit.InferenceUnavailable => HelperFailure.InferenceUnavailable,
                _ => HelperFailure.Crashed,
            };
        }

        private sealed class PendingReply : TaskCompletionSource<TEvent>
        {
            public PendingReply(Func<TEvent, bool> isReply)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                IsReply = isReply;
            }

            public Func<TEvent, bool> IsReply { get; }

            public new void TrySetException(Exception ex)
            {
                if (base.TrySetException(ex))
                    _ = Task.Exception;
            }
        }
    }
}
