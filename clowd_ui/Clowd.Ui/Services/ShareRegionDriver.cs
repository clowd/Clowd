using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.PlatformUtil;

namespace Clowd.UI
{
    /// <summary>The obscure state the helper last acknowledged. <paramref name="Unsolicited"/> means
    /// the helper sent the ack with no command of ours outstanding, and
    /// <paramref name="BlurAvailable"/> is the latched verdict that follows from it — false means the
    /// helper's GPU effect is permanently dead for this session and the toggle must be retired, not
    /// merely shown as off.</summary>
    public sealed record ShareObscureState(ShareObscureMode Mode, int Strength, bool Unsolicited, bool BlurAvailable);

    /// <summary>How a share session ended: the settled handshake (never
    /// <see cref="ShareHandshake.Pending"/> — a process that died undecided settles to
    /// <see cref="ShareHandshake.Failed"/>) and the helper's exit code, which is worth reporting
    /// even though it cannot distinguish the outcomes on its own.</summary>
    public sealed record ShareSessionEnded(ShareHandshake Handshake, int ExitCode);

    /// <summary>
    /// Hosts one clowd_share_region process and drives it: CLI arguments in, plain-text commands on
    /// stdin, line-delimited JSON on stdout (handed straight to <see cref="ShareRegionProtocol"/>),
    /// free-form libobs chatter on stderr. Lifecycle:
    /// <see cref="InitializeAsync"/> (spawns; resolves when the helper's "share this window" prompt
    /// is on screen) → <see cref="WaitForDecisionAsync"/> (resolves when the user answers that
    /// prompt, however long they take) → mirroring until <see cref="ShutdownAsync"/> or the helper
    /// exits. Public events are raised on the UI thread.
    ///
    /// <para>
    /// STRICTLY SINGLE-USE — THE ONE-HWND INVARIANT. What the meeting app shares is not "the mirror"
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
    /// Two differences from <see cref="ObsCapturer"/>, both of which will bite anyone porting code
    /// between them. (1) There is NO terminal message at the end of a share: once mirroring has
    /// started the helper simply exits 0 and the pipe closes, so obs-express's rule that "an exit
    /// without a terminal message is fatal" is wrong here and must not be copied. (2) The handshake
    /// has no timeout — the user may sit in their meeting app's window picker for as long as they
    /// like — so <see cref="WaitForDecisionAsync"/> waits forever by design, and only the process
    /// dying resolves it early.
    /// </para>
    ///
    /// <para>
    /// stdin EOF is the helper's own orphan-safety kill switch: if Clowd.Ui dies, the pipe closes,
    /// the helper reads EOF and quits, and no stray mirror window is left on the user's desktop. No
    /// watchdog is needed.
    /// </para>
    /// </summary>
    public sealed class ShareRegionDriver : IAsyncDisposable, IDisposable
    {
        /// <summary>The helper spins up libobs and creates a window before it says
        /// <c>initialized</c>; first-run graphics init can be slow, but not this slow. This is the
        /// ONLY timeout in the class — see the class docs for why the handshake has none.</summary>
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>How long a <c>quit</c> gets to take the helper down before it is killed. It only
        /// has to tear down a mirror and close a window; anything longer is a wedged child.</summary>
        private static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(5);

        /// <summary>The pumps unblock as soon as the child's stdio handles close, which the shutdown
        /// above has already forced; this is only a backstop against blocking disposal forever.</summary>
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);

        // libobs is chatty on stderr for the life of the process, and a shared region may be up for
        // an entire meeting — same budget the recorder uses, for the same reason.
        private const int MaxLogLines = 1000;
        private const int MaxLogChars = 256 * 1024;

        /// <summary>Overrides the located binary, for developers running a helper built somewhere
        /// else. Mirrors <c>CLOWD_OBS_EXPRESS_PATH</c> / <c>CLOWD_VID2GIF_PATH</c>.</summary>
        public const string EnvVarName = "CLOWD_SHARE_REGION_PATH";

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "clowd_share_region.exe" : "clowd_share_region";

        private readonly ShareRegionProtocol _protocol = new();
        private readonly HelperProcessLog _log = new(MaxLogLines, MaxLogChars);

        private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ShareHandshake> _decisionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _disposeLock = new();
        private Task _shutdownTask;
        private volatile bool _disposed;

        private Process _proc;
        private Task _stdoutPump;
        private Task _stderrPump;

        /// <summary>Raised on the UI thread when the user accepts the prompt and mirroring begins.
        /// The argument is the region actually being mirrored, which is not necessarily the one that
        /// was requested (the helper rounds the size up to at least 64 and to an even number).</summary>
        public event EventHandler<ShareRegionRect> SharingStarted;

        /// <summary>Raised on the UI thread when the helper applies a new region — the ack of
        /// <see cref="MoveRegion"/>, reporting what was APPLIED rather than what was asked for.</summary>
        public event EventHandler<ShareRegionRect> RegionChanged;

        /// <summary>Raised on the UI thread for every 1 Hz <c>status</c> line. The payload is fps
        /// and nothing else; this protocol has no dropped-frame counters.</summary>
        public event EventHandler<double> StatusReceived;

        /// <summary>Raised on the UI thread whenever the obscure state changes, whether we asked for
        /// it or not — see <see cref="ShareObscureState.Unsolicited"/>.</summary>
        public event EventHandler<ShareObscureState> ObscureChanged;

        /// <summary>Raised on the UI thread when the helper refuses or fails a command. Never fatal
        /// on its own: the share keeps running with the state it already had.</summary>
        public event EventHandler<string> CommandError;

        /// <summary>Raised on the UI thread exactly once, when the helper process is gone. This is
        /// the ONLY signal that a running share has stopped — there is no terminal protocol
        /// message — so the session UI must tear itself down from here.</summary>
        public event EventHandler<ShareSessionEnded> Ended;

        /// <summary>False once the helper has told us its obscure effect is permanently dead (see
        /// <see cref="ShareRegionProtocol.BlurAvailable"/>). Read from the UI thread inside an
        /// <see cref="ObscureChanged"/> handler, which is where it can change.</summary>
        public bool BlurAvailable => _protocol.BlurAvailable;

        /// <summary>The region the helper is actually mirroring, or null before it has said. Always
        /// the applied rectangle, never the requested one.</summary>
        public ShareRegionRect AppliedRegion => _protocol.AppliedRegion;

        public ShareRegionDriver()
        {
            // Every protocol event arrives on the stdout pump thread. Lifecycle bookkeeping (the two
            // TCSs) is done inline so it is ordered exactly as the wire was; everything the outside
            // world sees is posted to the UI thread, because every consumer of this class touches
            // windows and view models from its handlers.
            _protocol.Initialized += () => _initTcs.TrySetResult(true);

            _protocol.SharingStarted += region =>
            {
                _decisionTcs.TrySetResult(ShareHandshake.Started);
                Dispatcher.UIThread.Post(() => SharingStarted?.Invoke(this, region));
            };

            _protocol.Cancelled += () => _decisionTcs.TrySetResult(ShareHandshake.Cancelled);

            _protocol.RegionChanged += region =>
                Dispatcher.UIThread.Post(() => RegionChanged?.Invoke(this, region));

            _protocol.ObscureChanged += (mode, strength, unsolicited) =>
            {
                // BlurAvailable is read here, on the pump thread, so the snapshot the UI receives is
                // the one that belongs to this ack rather than whatever it happens to be when the
                // posted callback finally runs.
                var state = new ShareObscureState(mode, strength, unsolicited, _protocol.BlurAvailable);
                Dispatcher.UIThread.Post(() => ObscureChanged?.Invoke(this, state));
            };

            _protocol.StatusReceived += fps =>
                Dispatcher.UIThread.Post(() => StatusReceived?.Invoke(this, fps));

            _protocol.CommandError += message =>
            {
                // kept in the log as well as raised: a rejected command is exactly the context an
                // error report a minute later is missing.
                _log.Append("command_error: " + message);
                Dispatcher.UIThread.Post(() => CommandError?.Invoke(this, message));
            };

            _protocol.Chatter += line => _log.Append(line);

            _protocol.Ended += (handshake, exitCode) =>
            {
                // an init or a decision nobody ever got: fail the first, settle the second with
                // whatever the protocol decided (Failed when the user never answered).
                if (!_initTcs.Task.IsCompleted)
                {
                    FailObserved(_initTcs, _log.Attach(new InvalidOperationException(
                        "The screen sharing process exited before it was ready (exit code " + exitCode + ").")));
                }

                _decisionTcs.TrySetResult(handshake);

                var ended = new ShareSessionEnded(handshake, exitCode);
                Dispatcher.UIThread.Post(() => Ended?.Invoke(this, ended));
            };
        }

        /// <summary>
        /// Locates clowd_share_region: the <see cref="EnvVarName"/> override first, otherwise the
        /// directory obs-express was found in — the two ship side by side in every layout (the
        /// release payload and the cargo target dir alike), so there is deliberately no second
        /// search of its own to drift out of sync with <see cref="ObsBinaryLocator"/>. Returns null
        /// when it cannot be found; the caller reports that, because only the caller knows what the
        /// user was trying to do.
        /// </summary>
        public static string ResolveBinary()
        {
            var env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!String.IsNullOrWhiteSpace(env) && File.Exists(env))
                return HelperBinary.EnsureExecutable(Path.GetFullPath(env));

            var obs = ObsBinaryLocator.Resolve();
            if (String.IsNullOrEmpty(obs))
                return null;

            var candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(obs)), BinaryFileName);
            return File.Exists(candidate) ? HelperBinary.EnsureExecutable(candidate) : null;
        }

        /// <summary>
        /// Spawns the helper and resolves when it reports <c>initialized</c> — that is, when its
        /// "share this window" prompt is actually on screen and the user can point their meeting
        /// app at it. Does NOT wait for the user to answer; that is
        /// <see cref="WaitForDecisionAsync"/>.
        /// <para>
        /// Call this while Clowd.Ui is the foreground application. The helper's prompt window has to
        /// take the foreground to be findable, and a process spawned by a background app is refused
        /// it — the window then only blinks in the taskbar and the user never sees what they are
        /// supposed to pick.
        /// </para>
        /// <para>
        /// May be called ONCE per instance (see the class docs on the one-HWND invariant). Throws
        /// <see cref="InvalidOperationException"/> on a second call or when the process cannot be
        /// started, and <see cref="TimeoutException"/> if the helper never reports ready.
        /// </para>
        /// </summary>
        /// <param name="region">The area to mirror, in capture space (physical px on the Windows
        /// virtual desktop; x/y may be negative). The helper rounds the size up to at least 64 and
        /// to an even number, so what it mirrors may differ — read <see cref="AppliedRegion"/>.</param>
        /// <param name="title">The mirror window's title, i.e. what the user looks for in their
        /// meeting app's picker. Null or empty leaves the helper's own default.</param>
        /// <param name="captureCursor">Whether the mouse cursor is drawn into the mirror.</param>
        /// <param name="fps">Mirror frame rate; clamped to at least 1.</param>
        /// <param name="exePath">The helper binary, from <see cref="ResolveBinary"/>.</param>
        public async Task InitializeAsync(ScreenRect region, string title, bool captureCursor, int fps, string exePath)
        {
            if (_proc != null)
                throw new InvalidOperationException(
                    "A ShareRegionDriver drives exactly one share session; the mirror window's handle cannot be re-created.");

            if (String.IsNullOrEmpty(exePath))
                throw new ArgumentException("The screen sharing helper could not be located.", nameof(exePath));

            _proc = HelperProcess.Start(exePath, BuildArguments(region, title, captureCursor, fps));

            // the prompt window is the whole point of the spawn; hand it our foreground rights
            // before it tries to show itself.
            HelperProcess.GrantForeground(_proc);

            _stdoutPump = Task.Run(PumpStdoutAsync);
            _stderrPump = Task.Run(PumpStderrAsync);

            try
            {
                await _initTcs.Task.WaitAsync(InitializeTimeout);
            }
            catch (TimeoutException)
            {
                throw _log.Attach(new TimeoutException(
                    $"The screen sharing process did not become ready within {InitializeTimeout.TotalSeconds:0} s."));
            }
            catch (Exception ex)
            {
                _log.Attach(ex);
                throw;
            }
        }

        /// <summary>
        /// The command line the helper accepts, and nothing else — it has no other flags, and
        /// <c>--help</c>/<c>--version</c> would make it print and exit instead of sharing.
        /// </summary>
        public static IReadOnlyList<string> BuildArguments(ScreenRect region, string title, bool captureCursor, int fps)
        {
            var args = new List<string>
            {
                // capture space, verbatim: the same string obs-express takes for --region. Invariant
                // formatting because a locale digit separator would fail the Rust parser.
                "--region", FormattableString.Invariant($"{region.X},{region.Y},{region.Width},{region.Height}"),
                "--fps", FormattableString.Invariant($"{Math.Max(1, fps)}"),
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

            return args;
        }

        /// <summary>
        /// Resolves with the user's answer to the helper's prompt:
        /// <see cref="ShareHandshake.Started"/>, <see cref="ShareHandshake.Cancelled"/>, or
        /// <see cref="ShareHandshake.Failed"/> if the helper died before they answered.
        /// <para>
        /// NEVER TIMES OUT. Finding Clowd's mirror window in a meeting app's share picker can take a
        /// user minutes — walking through a Teams or Zoom dialog, hunting the right thumbnail — and
        /// a timeout here would kill a session the user was in the middle of setting up. The only
        /// thing that resolves this early is the process itself going away.
        /// </para>
        /// </summary>
        public Task<ShareHandshake> WaitForDecisionAsync()
        {
            if (_proc == null)
                throw new InvalidOperationException("InitializeAsync must be awaited before waiting for the share decision.");

            return _decisionTcs.Task;
        }

        /// <summary>
        /// Sets how the shared region is obscured for the people watching it.
        /// <see cref="ShareObscureMode.None"/> writes <c>unobscure</c>;
        /// <see cref="ShareObscureMode.Blur"/> and <see cref="ShareObscureMode.Pixelate"/> write
        /// <c>obscure &lt;mode&gt; &lt;strength&gt;</c>; <see cref="ShareObscureMode.Hide"/> writes a
        /// bare <c>obscure hide</c>. Fire-and-forget: the helper answers with an <c>obscure</c> line,
        /// which arrives as <see cref="ObscureChanged"/> and is the only confirmation there is — so
        /// the UI must follow the ack rather than assume the command took.
        /// </summary>
        /// <param name="strength">1..100, clamped. Only <c>blur</c> and <c>pixelate</c> carry one:
        /// the helper REFUSES a strength on the other two modes (its <c>parse_obscure</c> routes
        /// <c>hide</c> through a reject-strength arm), answering <c>command_error</c> and leaving the
        /// region exactly as it was — so appending one unconditionally would make Hide a no-op that
        /// also strands the pending-ack count.</param>
        public void SetObscure(ShareObscureMode mode, int strength = 50)
        {
            var command = ShareRegionProtocol.BuildObscureCommand(mode, strength);

            // BEFORE the write, never after: the helper can answer faster than this thread reaches
            // its next statement, and an ack that overtook its own bookkeeping would be classified
            // as unsolicited — which permanently retires the blur feature for the session.
            _protocol.NoteObscureSent();
            WriteCommand(command);
        }

        /// <summary>
        /// Moves/resizes the shared area without disturbing the mirror window, which is what keeps
        /// the meeting app's share alive (the alternative — a new process — would hand it a window
        /// handle nobody is watching). The caller is <c>ShareRegionPage</c>'s resize mode, which
        /// writes exactly one <c>move</c> when the user leaves the mode.
        /// <para>The helper answers with <c>region_changed</c> carrying the region it ACTUALLY
        /// applied after its own clamping, which arrives as <see cref="RegionChanged"/>. That clamp
        /// floors each side at 64 px and then rounds it DOWN to an even number (<c>mirror.rs:59-65</c>),
        /// so the applied rect is routinely a pixel or two smaller than the requested one. Callers
        /// must reflow the border, the toolbar and their own idea of the region from the ack, never
        /// from the rect they asked for.</para>
        /// <para>A REFUSED move answers <c>command_error</c> and emits no <c>region_changed</c> at
        /// all (<c>mirror.rs:270</c>, <c>win32.rs:1104</c>), so a caller that waits only on
        /// <see cref="RegionChanged"/> waits forever. This is reachable in ordinary operation and not
        /// merely on a malformed command: the helper plans the move against the monitor snapshot it
        /// took at bootstrap and never re-enumerates it, so a rect that lies on a display attached
        /// after the share started is refused even though it is on screen right now.</para>
        /// <para>There is NO per-move pending counter here — deliberately unlike
        /// <see cref="ShareRegionProtocol.NoteObscureSent"/> for the obscure commands — and the acks
        /// carry no request id, so one move's answer cannot be told from another's. Callers must
        /// therefore keep exactly ONE move in flight and pair it with their own timeout: a refusal
        /// that is missed or mis-attributed is indistinguishable from an ack that has not landed
        /// yet.</para>
        /// </summary>
        public void MoveRegion(ScreenRect region)
        {
            WriteCommand(FormattableString.Invariant($"move {region.X},{region.Y},{region.Width},{region.Height}"));
        }

        /// <summary>The most recent helper output (stderr plus any non-protocol stdout), for an
        /// error report. Bounded; see <see cref="HelperProcessLog"/>.</summary>
        public string GetLog() => _log.GetLog();

        /// <summary>
        /// Ends the session: a graceful <c>quit</c>, five seconds to act on it, then a kill.
        /// Idempotent — every caller gets the same task, and <see cref="Dispose"/> /
        /// <see cref="DisposeAsync"/> are the same operation under different names. The helper has
        /// no console-control handler, so <c>quit</c> (or closing stdin, which it also treats as
        /// quit) is the only graceful shutdown that exists.
        /// </summary>
        public Task ShutdownAsync()
        {
            lock (_disposeLock)
            {
                if (_shutdownTask == null)
                {
                    // read by WriteCommand: after this point a closed stdin writer is by design and
                    // must not be reported.
                    _disposed = true;
                    var proc = _proc;
                    _shutdownTask = proc == null ? Task.CompletedTask : Task.Run(() => ShutdownCoreAsync(proc));
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

        private async Task ShutdownCoreAsync(Process proc)
        {
            try
            {
                if (!proc.HasExited)
                {
                    WriteCommand("quit");
                    proc.WaitForExit((int)QuitGrace.TotalMilliseconds);
                }

                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error shutting down the screen sharing process: " + ex.Message);
                SentryConfig.CaptureHandled(_log.Attach(ex), "share.shutdown");
            }
            finally
            {
                // both pumps must be off the streams before the Process is disposed, or a parked
                // ReadLineAsync faults instead of ending at EOF.
                await HelperProcess.JoinPumpsAsync(PumpDrainTimeout, _stdoutPump, _stderrPump);
                try { proc.Dispose(); }
                catch { }
            }
        }

        private async Task PumpStdoutAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
                {
                    // handled inline, on this one thread: protocol state stays single-threaded and
                    // strictly in the order the helper wrote it, which is what the "exactly one
                    // response per command, in arrival order" contract is built on.
                    _protocol.HandleLine(line);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Share stdout pump failed: " + ex);
                SentryConfig.CaptureHandled(_log.Attach(ex), "share.stdout-pump");
            }

            // stdout EOF: the process is gone (or going). EOF is only reached after every buffered
            // line has been delivered, so the handshake can never be settled by the exit when the
            // helper had already answered it.
            await OnProcessEndedAsync();
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardError.ReadLineAsync()) != null)
                    _log.Append(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Share stderr pump failed: " + ex);
                SentryConfig.CaptureHandled(_log.Attach(ex), "share.stderr-pump");
            }
        }

        private async Task OnProcessEndedAsync()
        {
            try { await _proc.WaitForExitAsync(); }
            // a concurrent shutdown may have disposed the Process already; stdout EOF means it is
            // gone either way.
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("Share WaitForExitAsync failed: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "share.wait-for-exit");
            }

            // reported for diagnosis only — it CANNOT classify the outcome. The helper exits 0 both
            // when the user cancelled the prompt and when a share ran to completion; 1 is a runtime
            // failure and 2 an argument one. The protocol's settled handshake is the truth.
            int exitCode;
            try { exitCode = _proc.ExitCode; }
            catch { exitCode = -1; }

            _protocol.HandleProcessEnded(exitCode);
        }

        private void WriteCommand(string command)
        {
            var proc = _proc;
            if (proc == null)
                return;

            try
            {
                proc.StandardInput.WriteLine(command);
            }
            catch (Exception ex)
            {
                // the helper may already be dead, and after disposal its stdin writer is closed by
                // design — commands are best-effort and neither case is a defect worth reporting.
                Debug.WriteLine($"Failed to write '{command}' to the screen sharing process: {ex.Message}");
                if (!_disposed)
                    SentryConfig.CaptureHandled(_log.Attach(ex), "share.write-stdin");
            }
        }

        /// <summary>Faults a lifecycle TCS whose task may never be awaited — a caller whose own
        /// <c>WaitAsync</c> already timed out has walked away from the original task. Reading
        /// <see cref="Task.Exception"/> marks it observed so it cannot resurface as an
        /// <c>UnobservedTaskException</c> at GC time.</summary>
        private static void FailObserved<T>(TaskCompletionSource<T> tcs, Exception ex)
        {
            if (tcs != null && tcs.TrySetException(ex))
                _ = tcs.Task.Exception;
        }
    }
}
