using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using Prisc.Config;
using Prisc.Ipc;
using Prisc.PlatformUtil;

namespace Prisc.UI
{
    /// <summary>
    /// Where a share session stands with respect to the ONE decision the user makes in their
    /// meeting app: <see cref="Pending"/> while the helper's "share this window" prompt is up,
    /// then exactly one of the other three, forever.
    /// </summary>
    public enum ShareHandshake
    {
        /// <summary>The prompt window is showing and the user has not answered it yet. There is no
        /// timeout on this state: picking a window in a meeting app can take a while.</summary>
        Pending,

        /// <summary>The user pressed OK: the mirror window is live and the meeting app is
        /// (presumably) sharing it.</summary>
        Started,

        /// <summary>The user closed the prompt, pressed Escape, or we asked the helper to quit
        /// before they decided. A normal, silent outcome: nothing to report.</summary>
        Cancelled,

        /// <summary>The helper died before the user decided. Distinct from <see cref="Cancelled"/>
        /// because it is the one outcome worth surfacing as an error.</summary>
        Failed,
    }

    /// <summary>The obscure state the helper last acknowledged. <paramref name="Unsolicited"/> means
    /// the helper sent the ack with no command of ours outstanding, and
    /// <paramref name="BlurAvailable"/> is the latched verdict that follows from it: false means the
    /// helper's GPU effect is permanently dead for this session and the toggle must be retired, not
    /// merely shown as off.</summary>
    public sealed record ShareObscureState(ShareRegionObscureMode Mode, int Strength, bool Unsolicited, bool BlurAvailable);

    /// <summary>How a share session ended: the settled handshake (never
    /// <see cref="ShareHandshake.Pending"/>), whether the helper ended it cleanly with <c>done</c>,
    /// and a description of the exit for the error report when it did not.</summary>
    public sealed record ShareSessionEnded(ShareHandshake Handshake, bool Clean, string Description);

    /// <summary>
    /// Hosts one prisc_share_region process over the <c>share_region</c> catalog (HELPER_PROTOCOL.md
    /// §4.2). Lifecycle: <see cref="InitializeAsync"/> (spawns; resolves when the helper's "share this
    /// window" prompt is on screen) → <see cref="WaitForDecisionAsync"/> (resolves when the user
    /// answers that prompt, however long they take) → mirroring until <see cref="ShutdownAsync"/> or
    /// the helper ends the share itself. Public events are raised on the UI thread.
    ///
    /// <para>
    /// STRICTLY SINGLE-USE: THE ONE-HWND INVARIANT. What the meeting app shares is not "the mirror"
    /// but one specific window HANDLE that the user picked out of its window list. A respawned
    /// helper is a brand new window that nobody is watching, so a share that silently restarts
    /// itself leaves the user broadcasting a dead thumbnail with no way to tell. There is therefore
    /// no respawn path, no restart, and no reconfigure-by-relaunch anywhere in this class: a second
    /// <see cref="InitializeAsync"/> throws, and every recoverable problem is either fixed in place
    /// over stdin or ends the session outright. Anything a future feature wants to change about a
    /// live share (region, obscure mode) must be a stdin command, never a new process.
    /// </para>
    ///
    /// <para>
    /// The handshake has no timeout: the user may sit in their meeting app's window picker for as
    /// long as they like, so <see cref="WaitForDecisionAsync"/> waits forever by design, and only the
    /// process ending resolves it early.
    /// </para>
    /// </summary>
    public sealed class ShareRegionDriver : IAsyncDisposable, IDisposable
    {
        /// <summary>The helper spins up libobs and creates a window before it says
        /// <c>initialized</c>; first-run graphics init can be slow, but not this slow. This is the
        /// ONLY timeout in the class: see the class docs for why the handshake has none.</summary>
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ShareHandshake> _decisionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();

        private HelperClient<ShareRegionEvent, ShareRegionCommand> _client;
        private bool _started;
        private Task _shutdownTask;
        private TaskCompletionSource _pendingShutdown;
        private bool _startFailed;

        // protocol state; touched only by the one event pump (and the obscure counter by the writer)
        private ShareHandshake _handshake = ShareHandshake.Pending;
        private int _obscurePending;
        private volatile bool _blurAvailable = true;
        private volatile ShareRegionRect _appliedRegion;

        /// <summary>Raised on the UI thread when the user accepts the prompt and mirroring begins.
        /// The argument is the region actually being mirrored, which is not necessarily the one that
        /// was requested (the helper rounds the size up to at least 64 and to an even number).</summary>
        public event EventHandler<ShareRegionRect> SharingStarted;

        /// <summary>Raised on the UI thread when the helper applies a new region: the ack of
        /// <see cref="MoveRegion"/>, reporting what was APPLIED rather than what was asked for.</summary>
        public event EventHandler<ShareRegionRect> RegionChanged;

        /// <summary>Raised on the UI thread for every 1 Hz <c>status</c> event. The payload is fps
        /// and nothing else; this catalog has no dropped-frame counters.</summary>
        public event EventHandler<double> StatusReceived;

        /// <summary>Raised on the UI thread whenever the obscure state changes, whether we asked for
        /// it or not; see <see cref="ShareObscureState.Unsolicited"/>.</summary>
        public event EventHandler<ShareObscureState> ObscureChanged;

        /// <summary>Raised on the UI thread when the helper refuses or fails a command (a non-fatal
        /// <c>error</c>). Never fatal on its own: the share keeps running with the state it had.</summary>
        public event EventHandler<string> CommandError;

        /// <summary>Raised on the UI thread exactly once, when the helper process is gone. The share
        /// session UI tears itself down from here.</summary>
        public event EventHandler<ShareSessionEnded> Ended;

        /// <summary>
        /// False once the helper has retracted to <c>none</c> with no command of ours outstanding.
        /// That unsolicited ack means its GPU effect failed to build, and that failure is permanent
        /// for the life of the process: the helper never tries to build the effect again. So this
        /// latches false and the UI must retire the blur tile outright rather than merely show it as
        /// off; every later attempt would silently do nothing.
        /// </summary>
        public bool BlurAvailable => _blurAvailable;

        /// <summary>The region the helper is actually mirroring, or null before it has said. Always
        /// the applied rectangle, never the requested one.</summary>
        public ShareRegionRect AppliedRegion => _appliedRegion;

        /// <summary>Where the handshake stands; for tests (the page awaits
        /// <see cref="WaitForDecisionAsync"/>).</summary>
        internal ShareHandshake Handshake => _handshake;

        /// <summary>
        /// Spawns the helper and resolves when it reports <c>initialized</c>, that is, when its
        /// "share this window" prompt is actually on screen and the user can point their meeting app
        /// at it. Does NOT wait for the user to answer; that is <see cref="WaitForDecisionAsync"/>.
        /// <para>
        /// Call this while Prisc.Ui is the foreground application. The helper's prompt window has to
        /// take the foreground to be findable, and a process spawned by a background app is refused
        /// it: the window then only blinks in the taskbar and the user never sees what they are
        /// supposed to pick.
        /// </para>
        /// <para>
        /// May be called ONCE per instance (see the class docs on the one-HWND invariant). Throws
        /// <see cref="InvalidOperationException"/> on a second call,
        /// <see cref="HelperStartException"/> when the process cannot be started, and
        /// <see cref="TimeoutException"/> if the helper never reports ready.
        /// </para>
        /// </summary>
        /// <param name="region">The area to mirror, in capture space (physical px on the Windows
        /// virtual desktop; x/y may be negative). The helper rounds the size up to at least 64 and
        /// to an even number, so what it mirrors may differ: read <see cref="AppliedRegion"/>.</param>
        /// <param name="title">The mirror window's title, i.e. what the user looks for in their
        /// meeting app's picker. Null or empty leaves the helper's own default.</param>
        /// <param name="captureCursor">Whether the mouse cursor is drawn into the mirror.</param>
        /// <param name="fps">Mirror frame rate; clamped to at least 1.</param>
        /// <param name="captureMethod">Which OS API backs the capture. Windows only: the helper
        /// ignores it on macOS.</param>
        /// <param name="accent">Fill of the prompt's OK button, or null for the helper's own blue.
        /// Windows only: on macOS the button is a native one in the system accent.</param>
        /// <param name="exePath">The helper binary, from <see cref="HelperLocator.Find"/>.</param>
        public async Task InitializeAsync(ScreenRect region, string title, bool captureCursor, int fps,
            ScreenCaptureMethod captureMethod, Color? accent, string exePath)
        {
            if (String.IsNullOrEmpty(exePath))
                throw new ArgumentException("The screen sharing helper could not be located.", nameof(exePath));

            lock (_sync)
            {
                if (_started)
                    throw new InvalidOperationException(
                        "A ShareRegionDriver drives exactly one share session; the mirror window's handle cannot be re-created.");
                // from here a shutdown waits for the client this call is about to start
                _started = true;
            }

            // the prompt window is the whole point of the spawn; the client hands it our foreground
            // rights before it tries to show itself
            HelperClient<ShareRegionEvent, ShareRegionCommand> client;
            try
            {
                client = await HelperClient<ShareRegionEvent, ShareRegionCommand>.StartAsync(HelperSpecs.ShareRegion, exePath,
                    BuildArguments(region, title, captureCursor, fps, captureMethod, accent),
                    new HelperStartOptions { GrantForeground = true });
            }
            catch
            {
                // StartAsync has already disposed whatever it spawned, so a shutdown has nothing to wait for
                lock (_sync)
                {
                    _startFailed = true;
                    _pendingShutdown?.TrySetResult();
                }
                throw;
            }

            TaskCompletionSource pendingShutdown;
            bool stopNow;
            lock (_sync)
            {
                _client = client;
                pendingShutdown = _pendingShutdown;
                stopNow = _shutdownTask != null;
            }

            // a shutdown that arrived during the start resolves only once this client is gone, so
            // the next session never overlaps a mirror that is still up
            if (stopNow)
                _ = Task.Run(async () =>
                {
                    await client.DisposeAsync();
                    pendingShutdown?.TrySetResult();
                });

            _ = Task.Run(() => PumpAsync(client));

            try
            {
                await _initTcs.Task.WaitAsync(InitializeTimeout);
            }
            catch (TimeoutException)
            {
                throw client.Log.Attach(new TimeoutException(
                    $"The screen sharing process did not become ready within {InitializeTimeout.TotalSeconds:0} s."));
            }
            catch (Exception ex)
            {
                client.Log.Attach(ex);
                throw;
            }
        }

        /// <summary>
        /// The command line the helper accepts, and nothing else: it has no other flags we use, and
        /// <c>--help</c>/<c>--version</c> would make it print and exit instead of sharing.
        /// </summary>
        public static IReadOnlyList<string> BuildArguments(ScreenRect region, string title, bool captureCursor, int fps,
            ScreenCaptureMethod captureMethod, Color? accent = null)
        {
            var args = new List<string>
            {
                // capture space, verbatim: the same string prisc_recorder takes for --region. Invariant
                // formatting because a locale digit separator would fail the Rust parser.
                "--region", FormattableString.Invariant($"{region.X},{region.Y},{region.Width},{region.Height}"),
                "--fps", FormattableString.Invariant($"{Math.Max(1, fps)}"),
                // always passed, so the helper's own default never silently overrides what the
                // settings page shows. Ignored on macOS, where there is only one capture path.
                "--capture-method", captureMethod.ToCliValue(),
            };

            // omitted entirely when empty so the helper's own default title stands, rather than
            // handing a meeting app's picker a blank row to show.
            if (!String.IsNullOrEmpty(title))
            {
                args.Add("--title");
                args.Add(title);
            }

            // the flag is the opt-OUT; the helper draws the cursor by default.
            if (!captureCursor)
                args.Add("--no-cursor");

            // the effective (contrast-corrected) accent, unlike the capturer's pick: the helper makes
            // no correction of its own, it only flips the label to black on a light fill.
            if (accent is { } a)
            {
                args.Add("--accent-color");
                args.Add($"#{a.R:X2}{a.G:X2}{a.B:X2}");
            }

            return args;
        }

        /// <summary>
        /// Resolves with the user's answer to the helper's prompt:
        /// <see cref="ShareHandshake.Started"/>, <see cref="ShareHandshake.Cancelled"/>, or
        /// <see cref="ShareHandshake.Failed"/> if the helper died before they answered.
        /// <para>
        /// NEVER TIMES OUT. Finding Prisc's mirror window in a meeting app's share picker can take a
        /// user minutes, and a timeout here would kill a session the user was in the middle of
        /// setting up. The only thing that resolves this early is the process itself going away.
        /// </para>
        /// </summary>
        public Task<ShareHandshake> WaitForDecisionAsync()
        {
            lock (_sync)
            {
                if (_client == null)
                    throw new InvalidOperationException("InitializeAsync must be awaited before waiting for the share decision.");
            }

            return _decisionTcs.Task;
        }

        /// <summary>
        /// Sets how the shared region is obscured for the people watching it. Fire-and-forget: the
        /// helper answers with an <c>obscure</c> event, which arrives as <see cref="ObscureChanged"/>
        /// and is the only confirmation there is, so the UI must follow the ack rather than assume
        /// the command took.
        /// </summary>
        /// <param name="strength">1..100, clamped. Only <c>blur</c> and <c>pixelate</c> carry one.</param>
        public void SetObscure(ShareRegionObscureMode mode, int strength = 50)
        {
            var command = BuildObscureCommand(mode, strength);

            // BEFORE the write, never after: the helper can answer faster than this thread reaches
            // its next statement, and an ack that overtook its own bookkeeping would be classified
            // as unsolicited, which permanently retires the blur feature for the session.
            Interlocked.Increment(ref _obscurePending);
            Send(command);
        }

        /// <summary>The <c>obscure</c> command for <paramref name="mode"/>. The strength rides only on
        /// <c>blur</c> and <c>pixelate</c>, clamped to the helper's 1..100; <c>hide</c> and <c>none</c>
        /// (the old <c>unobscure</c>) carry none.</summary>
        internal static ShareRegionCommand.Obscure BuildObscureCommand(ShareRegionObscureMode mode, int strength) =>
            mode is ShareRegionObscureMode.Blur or ShareRegionObscureMode.Pixelate
                ? new ShareRegionCommand.Obscure(mode, (uint)Math.Clamp(strength, 1, 100))
                : new ShareRegionCommand.Obscure(mode == ShareRegionObscureMode.Hide ? mode : ShareRegionObscureMode.None, null);

        /// <summary>
        /// Moves/resizes the shared area without disturbing the mirror window, which is what keeps
        /// the meeting app's share alive (a new process would hand it a window handle nobody is
        /// watching). The caller is <c>ShareRegionPage</c>'s resize mode, which writes exactly one
        /// <c>move</c> when the user leaves the mode.
        /// <para>The helper answers with <c>region_changed</c> carrying the region it ACTUALLY
        /// applied after its own clamping, which arrives as <see cref="RegionChanged"/>. That clamp
        /// floors each side at 64 px and then rounds it DOWN to an even number, so the applied rect
        /// is routinely a pixel or two smaller than the requested one. Callers must reflow the
        /// border, the toolbar and their own idea of the region from the ack, never from the rect
        /// they asked for.</para>
        /// <para>A REFUSED move answers with an <c>error</c> (<see cref="CommandError"/>) and no
        /// <c>region_changed</c> at all, so a caller that waits only on <see cref="RegionChanged"/>
        /// waits forever. This is reachable in ordinary operation: the helper plans the move against
        /// the monitor snapshot it took at bootstrap, so a rect on a display attached after the share
        /// started is refused even though it is on screen right now. The acks carry no request id,
        /// so callers must keep exactly ONE move in flight and pair it with their own timeout.</para>
        /// </summary>
        public void MoveRegion(ScreenRect region)
        {
            Send(new ShareRegionCommand.Move(new ShareRegionRect(region.X, region.Y,
                (uint)Math.Max(0, region.Width), (uint)Math.Max(0, region.Height))));
        }

        /// <summary>The most recent helper output (stderr plus any non-protocol stdout), for an error
        /// report. Bounded.</summary>
        public string GetLog()
        {
            lock (_sync)
                return _client?.StderrTail ?? "";
        }

        /// <summary>
        /// Ends the session: a graceful <c>quit</c>, the catalog's grace to act on it, then a kill.
        /// Idempotent: every caller gets the same task, and <see cref="Dispose"/> /
        /// <see cref="DisposeAsync"/> are the same operation under different names.
        /// </summary>
        public Task ShutdownAsync()
        {
            lock (_sync)
            {
                if (_shutdownTask == null)
                {
                    if (_client is { } client)
                    {
                        _shutdownTask = Task.Run(() => client.DisposeAsync().AsTask());
                    }
                    else if (_started && !_startFailed)
                    {
                        // InitializeAsync is still waiting for hello; it completes this once it has the client
                        _pendingShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _shutdownTask = _pendingShutdown.Task;
                    }
                    else
                    {
                        _shutdownTask = Task.CompletedTask;
                    }
                }

                return _shutdownTask;
            }
        }

        /// <summary>Non-blocking best-effort shutdown for safety-net callers (a window closing, a
        /// finally block); UI-thread code should <c>await</c> <see cref="DisposeAsync"/> instead so
        /// the mirror is really gone before the next session starts.</summary>
        public void Dispose()
        {
            _ = ShutdownAsync();
        }

        /// <inheritdoc cref="ShutdownAsync"/>
        public ValueTask DisposeAsync() => new ValueTask(ShutdownAsync());

        private void Send(ShareRegionCommand command)
        {
            HelperClient<ShareRegionEvent, ShareRegionCommand> client;
            lock (_sync)
                client = _client;
            if (client != null && !client.Send(command))
                Debug.WriteLine($"The screen sharing process is not accepting commands; dropped {command.GetType().Name}.");
        }

        private async Task PumpAsync(HelperClient<ShareRegionEvent, ShareRegionCommand> client)
        {
            try
            {
                // one thread, in wire order: the "exactly one response per command, in arrival
                // order" contract the obscure accounting is built on depends on it
                await foreach (var ev in client.Events.ReadAllAsync())
                    Handle(ev, client.Log);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Share event pump failed: " + ex);
                SentryConfig.CaptureHandled(client.Log.Attach(ex), "share.event-pump");
            }

            HandleEnded(await client.Completion, client.Log);
        }

        /// <summary>One event, on the pump. Internal so the protocol rules are testable without a
        /// process.</summary>
        internal void Handle(ShareRegionEvent ev, HelperLog log = null)
        {
            switch (ev)
            {
                case ShareRegionEvent.Initialized:
                    _initTcs.TrySetResult(true);
                    break;

                case ShareRegionEvent.SharingStarted started:
                    if (_handshake != ShareHandshake.Pending)
                    {
                        // exactly one per session; a second is a helper bug, and re-settling would
                        // be far worse than a log line
                        log?.Append("[prisc] ignored a second sharing_started");
                        break;
                    }

                    _handshake = ShareHandshake.Started;
                    if (started.Region != null)
                        _appliedRegion = started.Region;
                    _decisionTcs.TrySetResult(ShareHandshake.Started);
                    var region = _appliedRegion;
                    Dispatcher.UIThread.Post(() => SharingStarted?.Invoke(this, region));
                    break;

                case ShareRegionEvent.RegionChanged changed when changed.Region != null:
                    // deliberately does NOT touch the handshake: a move is legal during the prompt
                    // phase, and answering one says nothing about the user's decision.
                    _appliedRegion = changed.Region;
                    Dispatcher.UIThread.Post(() => RegionChanged?.Invoke(this, changed.Region));
                    break;

                case ShareRegionEvent.Obscure obscure when obscure.Mode != ShareRegionObscureMode.Unknown:
                    HandleObscure(obscure);
                    break;

                case ShareRegionEvent.Status status:
                    Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, status.Fps));
                    break;

                case ShareRegionEvent.Done { Result: ShareRegionResult.Cancelled }:
                    if (_handshake == ShareHandshake.Pending)
                    {
                        _handshake = ShareHandshake.Cancelled;
                        _decisionTcs.TrySetResult(ShareHandshake.Cancelled);
                    }

                    break;

                case ShareRegionEvent.Error { Fatal: false } error:
                    var message = String.IsNullOrEmpty(error.Message) ? "The share helper rejected a command." : error.Message;
                    // kept in the log as well as raised: a rejected command is exactly the context
                    // an error report a minute later is missing.
                    log?.Append("[prisc] command refused: " + error.Code + ": " + message);
                    Dispatcher.UIThread.Post(() => CommandError?.Invoke(this, message));
                    break;
            }
        }

        private void HandleObscure(ShareRegionEvent.Obscure obscure)
        {
            // every command gets exactly one response in arrival order, so an ack with nothing
            // outstanding is one the helper sent on its own initiative.
            var unsolicited = Interlocked.Decrement(ref _obscurePending) < 0;
            if (unsolicited)
                Interlocked.Increment(ref _obscurePending);

            // The one permanent failure on this catalog. The helper only ever retracts to none
            // unbidden when its effect could not be built, and that is terminal for the life of the
            // process. Latch it so the UI retires the feature instead of offering a button that can
            // no longer do anything.
            if (unsolicited && obscure.Mode == ShareRegionObscureMode.None)
                _blurAvailable = false;

            // read here, on the pump, so the snapshot the UI receives belongs to this ack rather
            // than to whatever it is when the posted callback runs
            var state = new ShareObscureState(obscure.Mode, (int)Math.Min(obscure.Strength, 100u), unsolicited, _blurAvailable);
            Dispatcher.UIThread.Post(() => ObscureChanged?.Invoke(this, state));
        }

        /// <summary>
        /// The process is gone. If the handshake never settled it settles now: to
        /// <see cref="ShareHandshake.Cancelled"/> when the helper ended cleanly (we asked it to quit
        /// before the user decided), and to <see cref="ShareHandshake.Failed"/> otherwise, whatever the
        /// exit code: a helper that died before the user decided is exactly the case worth reporting.
        /// </summary>
        internal void HandleEnded(HelperOutcome<ShareRegionEvent> outcome, HelperLog log = null)
        {
            var clean = outcome.Failure == HelperFailure.None;
            if (_handshake == ShareHandshake.Pending)
                _handshake = clean ? ShareHandshake.Cancelled : ShareHandshake.Failed;

            if (!_initTcs.Task.IsCompleted)
            {
                var ex = new InvalidOperationException($"The screen sharing process exited before it was ready ({outcome.Describe()}).");
                if (_initTcs.TrySetException(log?.Attach(ex) ?? ex))
                    _ = _initTcs.Task.Exception;
            }

            _decisionTcs.TrySetResult(_handshake);

            var ended = new ShareSessionEnded(_handshake, clean, outcome.Describe());
            Dispatcher.UIThread.Post(() => Ended?.Invoke(this, ended));
        }
    }
}
