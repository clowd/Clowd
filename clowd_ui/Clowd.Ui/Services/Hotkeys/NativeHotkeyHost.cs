using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using static Clowd.UI.HotkeysAbi;

namespace Clowd.UI
{
    /// <summary>
    /// <see cref="IGlobalTriggerHost"/> over the native clowd_hotkeys library (WH_KEYBOARD_LL on
    /// Windows, a CGEventTap on macOS). Owned by <see cref="HotkeyManager"/>. The hook starts
    /// lazily on the first registration, observer or recording, and runs until the host is
    /// disposed on app exit — on macOS restarting by itself if it cannot start or dies.
    /// </summary>
    /// <remarks>
    /// The hook itself decides what to swallow and only queues what happened; nothing here runs on
    /// it. A dedicated event thread (<c>clowd-hotkeys-events</c>) blocks on the queue, hands key
    /// edges to the observers right there, and posts everything else to the UI thread — hotkey
    /// actions, recording progress and status changes.
    /// <para>Native-call discipline: registrations and recording sessions can outlive the host
    /// (an editor window closing during shutdown), so they never hold the native handle. Every call
    /// goes through a host method that takes <see cref="_gate"/> and does nothing once the host is
    /// disposed; the event thread is the only other user of the handle, and the handle is freed
    /// only after that thread has ended.</para>
    /// <para>If the hook cannot start (no Accessibility permission on macOS, Linux, the library
    /// missing), every registration and recording reports why, and the app keeps running. A
    /// missing permission asks for it (<see cref="AccessibilityPrompt"/>, once per run). If it
    /// dies later (it reports itself stopped, or its event queue fails), the hook is stopped for
    /// good — so the gestures it swallowed reach apps again — and every live registration and
    /// recording fails with the reason. Nothing restarts it short of restarting the app, except
    /// on macOS below.</para>
    /// <para>On macOS every way the tap fails to start or reports itself stopped — a missing
    /// permission, a NULL tap, a grant revoked under a running one — comes down to that grant,
    /// which <c>AXIsProcessTrusted</c> reports live, so there the host polls it while the hook is
    /// down (<see cref="RetryStart"/>) and, once it reads as granted, starts a new hook and moves
    /// the registrations and observers onto it: their statuses flip back to registered without a
    /// restart. A poll rather than window activation, because Clowd mostly lives in the tray — the
    /// user grants in System Settings and goes straight back to the app they were in. Windows
    /// failures, and a failed event queue anywhere, are specific to the process and stay put.
    /// A stale grant (granted as far as <c>AXIsProcessTrusted</c> says, yet the tap gets no keys)
    /// says so (<see cref="StaleGrantMessage"/>) and is retried less and less often, up to every
    /// 32 s, until it heals or the permission reads as missing.</para>
    /// </remarks>
    internal sealed class NativeHotkeyHost : IGlobalTriggerHost, IDisposable
    {
        private const uint EventTimeoutMs = 500;
        private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);

        /// <summary>The retry poll, matching the standby capturer's own (CAPTURE_PROTOCOL.md).</summary>
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

        /// <summary>How long a retry waits for a dead hook's event thread, whose queue is closed and
        /// draining, before leaving its handle for the next tick.</summary>
        private static readonly TimeSpan DeadPumpJoinTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>No restart hint: the host picks the grant up by itself.</summary>
        internal const string PermissionMessage =
            "Could not start the global hotkey listener. Grant Accessibility permission to Clowd under " +
            MacPermissions.GrantLocation + "; the hotkeys start working as soon as you do.";

        /// <summary>The permission check passed, yet the tap was made without key events: a grant
        /// macOS still lists (an older build's, or one removed with '−' while AXIsProcessTrusted
        /// still answers yes) but no longer honours. Granting again does nothing; re-adding does.
        /// The System page shows Accessibility as granted, so this must not ask for it.</summary>
        internal const string StaleGrantMessage =
            "Could not start the global hotkey listener. macOS lists Clowd as allowed to use Accessibility, " +
            "but the permission no longer works: remove Clowd from System Settings → Privacy & Security → " +
            "Accessibility and add it again; the hotkeys start working as soon as you do.";

        /// <summary>The longest a stale grant waits between attempts, in retry ticks (32 s): each
        /// attempt makes and tears down a keyboard tap on the UI thread.</summary>
        private const int StaleGrantMaxTicks = 16;

        private readonly object _gate = new();
        private readonly Dictionary<uint, Registration> _registrations = new(); // live on _handle, by id
        private readonly List<Registration> _waiting = new(); // for a hook to come back (_recoverable)
        private Action<GlobalKeyEvent>[] _observers = Array.Empty<Action<GlobalKeyEvent>>();
        private IntPtr _handle; // the live hook's, or a dead one's until a retry or Dispose frees it
        private Thread _pump;
        private bool _started;
        private string _hookError; // non-null while the hook failed to start or is dead
        private bool _recoverable; // _hookError may heal (macOS), so RetryStart tries again
        private int _generation; // bumped per hook started, so a dead hook's late events spare its successor
        private bool _reportedHookFailure;
        private bool _reportedHookStop;
        private int _staleGrantTicks; // retry ticks between attempts while the grant is stale (0: none)
        private int _staleGrantSkip; // ticks left before the next of those attempts
        private Timer _retryTimer;
        private bool _disposed;

        // UI thread only (written by StartRecording and the session, read by posted callbacks)
        private volatile Session _recording;

        private readonly INativeHotkeys _native;
        private readonly Action<Action> _post;
        private readonly bool _isMacOS;
        private readonly Func<bool> _isTrusted;
        private readonly AccessibilityPrompt _accessibilityPrompt;
        private readonly TimeSpan _retryInterval;
        private readonly Action<Exception, string> _report;

        /// <summary>Raised by <see cref="RetryStart"/> once the hook is back.</summary>
        public event EventHandler RecordingAvailable;

        public NativeHotkeyHost()
            : this(HotkeysNative.Binding, action => Dispatcher.UIThread.Post(action), OperatingSystem.IsMacOS(),
                   () => MacPermissions.HasAccessibility, AccessibilityPrompt.Shared, RetryInterval,
                   SentryConfig.CaptureHandled)
        {
        }

        /// <param name="native">The library (a fake in tests).</param>
        /// <param name="post">Runs an action on the UI thread, in order.</param>
        /// <param name="isMacOS">Whether a hook that is down may come back (see <see cref="RetryStart"/>).</param>
        /// <param name="isTrusted">The live Accessibility check (<c>AXIsProcessTrusted</c>).</param>
        /// <param name="accessibilityPrompt">Asked when the hook lacks the permission.</param>
        /// <param name="retryInterval">How often a hook that is down is tried again;
        /// <see cref="Timeout.InfiniteTimeSpan"/> leaves it to <see cref="RetryStart"/> calls.</param>
        /// <param name="report">Files a handled error (<see cref="SentryConfig.CaptureHandled"/>).</param>
        internal NativeHotkeyHost(INativeHotkeys native, Action<Action> post, bool isMacOS, Func<bool> isTrusted,
                                  AccessibilityPrompt accessibilityPrompt, TimeSpan retryInterval,
                                  Action<Exception, string> report)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _isMacOS = isMacOS;
            _isTrusted = isTrusted ?? throw new ArgumentNullException(nameof(isTrusted));
            _accessibilityPrompt = accessibilityPrompt ?? throw new ArgumentNullException(nameof(accessibilityPrompt));
            _retryInterval = retryInterval;
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        /// <summary>
        /// Starts the hook again if it is down for a reason that may heal and the Accessibility
        /// permission now reads as granted, moving the waiting registrations and the observers onto
        /// the new hook. UI thread (the retry timer posts it while the hook is down); a no-op in
        /// every other state. An attempt that fails gives the waiting registrations its own error;
        /// one that fails in a way that cannot heal ends the retries and leaves them failed.
        /// </summary>
        internal void RetryStart()
        {
            Thread deadPump;
            lock (_gate)
            {
                if (_disposed || _hookError == null || !_recoverable)
                    return;
                if (!_isTrusted())
                {
                    // really missing now (a stale grant removed, say): the next grant is the fix,
                    // so it is tried on the next tick that reads it
                    _staleGrantTicks = _staleGrantSkip = 0;
                    return;
                }
                // a stale grant heals only when the user re-adds Clowd: back off rather than make
                // a tap every tick, but never stop, since re-adding mid-run is what heals it
                if (_staleGrantSkip > 0)
                {
                    _staleGrantSkip--;
                    return;
                }
                deadPump = _pump;
            }

            // a dead hook's handle may only be freed once its event thread has delivered the
            // Stopped and ended. Joined outside the gate, which that thread takes to dispatch.
            if (deadPump != null && !deadPump.Join(DeadPumpJoinTimeout))
                return;

            var outcomes = new List<(Registration Registration, bool IsRegistered, string Error)>();
            string stillDown;
            lock (_gate)
            {
                if (_disposed || _hookError == null || !_recoverable || _pump != deadPump)
                    return;

                // nothing else can be using it: its event thread has ended and every other call
                // takes the gate, and finds _handle replaced below
                if (_handle != IntPtr.Zero)
                    _native.Free(_handle);
                _handle = IntPtr.Zero;
                _pump = null;
                _started = false;
                _hookError = null;
                _recoverable = false;

                EnsureStartedLocked();
                stillDown = _hookError;
                if (stillDown != null)
                {
                    // still down, maybe for another reason now (granted, but the tap still cannot
                    // be made): the waiting rows say what this attempt said, not the first one
                    foreach (var registration in _waiting)
                        outcomes.Add((registration, false, stillDown));
                    // a failure that cannot heal (a panic): stop polling, and the waiting
                    // registrations fail for good. Otherwise EnsureStartedLocked kept the timer.
                    if (!_recoverable)
                    {
                        StopRetryTimerLocked();
                        _waiting.Clear();
                    }
                }
                else
                {
                    StopRetryTimerLocked();
                    if (_observers.Length > 0)
                        _native.SetObserve(_handle, true);

                    var waiting = _waiting.ToArray();
                    _waiting.Clear();
                    foreach (var registration in waiting)
                    {
                        var gesture = registration.Gesture;
                        int result = _native.Register(_handle, (int)gesture.Key, (uint)gesture.Modifiers, out var id);
                        if (result == CH_OK)
                        {
                            registration.Id = id;
                            _registrations[id] = registration;
                            outcomes.Add((registration, true, ""));
                        }
                        else
                        {
                            // died again already: the new hook's Stopped restarts the timer
                            if (result == CH_ERR_STOPPED)
                                _waiting.Add(registration);
                            outcomes.Add((registration, false, RegisterError(result, gesture)));
                        }
                    }
                }
            }

            foreach (var (registration, isRegistered, error) in outcomes)
                registration.SetStatus(isRegistered, error);
            if (stillDown != null)
                return;

            Debug.WriteLine("[NativeHotkeyHost] the global hotkey listener is running again");
            RecordingAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Whether the retry timer is running (tests).</summary>
        internal bool IsRetrying
        {
            get
            {
                lock (_gate)
                    return _retryTimer != null;
            }
        }

        // must be called while holding _gate.
        private void StartRetryTimerLocked()
        {
            if (_retryTimer != null || _retryInterval == Timeout.InfiniteTimeSpan)
                return;

            _retryTimer = new Timer(_ => _post(RetryStart), null, _retryInterval, _retryInterval);
        }

        // must be called while holding _gate.
        private void StopRetryTimerLocked()
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        public IGlobalTriggerRegistration RegisterTrigger(HotkeyGesture gesture, Action executed, bool fireOnRepeat = false)
        {
            if (gesture == null) throw new ArgumentNullException(nameof(gesture));
            if (executed == null) throw new ArgumentNullException(nameof(executed));

            lock (_gate)
            {
                if (_disposed)
                    return Registration.Failed("The hotkey host has been shut down.");

                EnsureStartedLocked();
                if (_hookError != null)
                    return _recoverable ? WaitLocked(gesture, executed, fireOnRepeat, _hookError) : Registration.Failed(_hookError);

                int result = _native.Register(_handle, (int)gesture.Key, (uint)gesture.Modifiers, out var id);
                // the hook died and its Stopped is still on the way: wait for its successor too
                if (result == CH_ERR_STOPPED && _isMacOS)
                    return WaitLocked(gesture, executed, fireOnRepeat, _hookError ?? StoppedMessage);
                if (result != CH_OK)
                    return Registration.Failed(RegisterError(result, gesture));

                return AddRegistration(id, gesture, executed, fireOnRepeat);
            }
        }

        /// <summary>Tracks a registration the hook accepted under <paramref name="id"/>: from here
        /// its presses run <paramref name="executed"/> until it is disposed.</summary>
        internal Registration AddRegistration(uint id, HotkeyGesture gesture, Action executed, bool fireOnRepeat)
        {
            lock (_gate)
            {
                var registration = new Registration(this, gesture, executed, fireOnRepeat, isRegistered: true, error: "") { Id = id };
                _registrations[id] = registration;
                return registration;
            }
        }

        /// <summary>A registration for a hook that is down, made for real by <see cref="RetryStart"/>
        /// once it is back. Must be called while holding _gate.</summary>
        private Registration WaitLocked(HotkeyGesture gesture, Action executed, bool fireOnRepeat, string error)
        {
            var registration = new Registration(this, gesture, executed, fireOnRepeat, isRegistered: false, error);
            _waiting.Add(registration);
            return registration;
        }

        // must be called while holding _gate.
        private string RegisterError(int result, HotkeyGesture gesture) => result switch
        {
            CH_ERR_DUPLICATE => "Gesture is already in-use by another hotkey.",
            CH_ERR_UNSUPPORTED_KEY or CH_ERR_INVALID_KEY => $"The key '{gesture}' cannot be used as a global hotkey.",
            CH_ERR_MODIFIER_KEY => "A modifier key on its own cannot be a global hotkey.",
            CH_ERR_NO_KEY => "This gesture has no key.",
            CH_ERR_STOPPED => _hookError ?? StoppedMessage,
            _ => $"Could not register the hotkey (error {result}).",
        };

        public IDisposable ObserveKeys(Action<GlobalKeyEvent> observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));

            lock (_gate)
            {
                if (_disposed)
                    return new ObserverHandle(null, null);

                EnsureStartedLocked();
                // copy-on-write: the event thread reads the array without taking the lock
                _observers = [.. _observers, observer];
                if (_observers.Length == 1 && _handle != IntPtr.Zero)
                    _native.SetObserve(_handle, true);
                return new ObserverHandle(this, observer);
            }
        }

        private void Unobserve(Action<GlobalKeyEvent> observer)
        {
            lock (_gate)
            {
                int index = Array.IndexOf(_observers, observer);
                if (index < 0)
                    return;

                _observers = _observers.Where((_, i) => i != index).ToArray();
                if (_observers.Length == 0 && _handle != IntPtr.Zero && !_disposed)
                    _native.SetObserve(_handle, false);
            }
        }

        public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
            Action<string> failed, out string error)
        {
            if (modifiersChanged == null) throw new ArgumentNullException(nameof(modifiersChanged));
            if (recorded == null) throw new ArgumentNullException(nameof(recorded));
            if (failed == null) throw new ArgumentNullException(nameof(failed));

            uint token;
            int generation;
            lock (_gate)
            {
                if (_disposed)
                {
                    error = "The hotkey host has been shut down.";
                    return null;
                }

                EnsureStartedLocked();
                if (_hookError != null)
                {
                    error = _hookError;
                    return null;
                }

                if (_recording != null)
                {
                    error = null;
                    return null;
                }

                if (_native.StartRecording(_handle, out token) != CH_OK)
                {
                    error = _hookError ?? StoppedMessage;
                    return null;
                }
                generation = _generation;
            }

            error = null;
            return BeginSession(token, generation, modifiersChanged, recorded, failed);
        }

        /// <summary>UI thread: makes the session the hook of <paramref name="generation"/> just
        /// armed under <paramref name="token"/> the live one, which the posted recording events are
        /// delivered to.</summary>
        internal IDisposable BeginSession(uint token, int generation, Action<HotkeyModifiers> modifiersChanged,
            Action<HotkeyGesture> recorded, Action<string> failed)
        {
            var session = new Session(this, token, generation, modifiersChanged, recorded, failed);
            _recording = session;
            return session;
        }

        private void StopRecording(uint token, int generation)
        {
            lock (_gate)
            {
                // a token is only meaningful to the hook that issued it
                if (_handle != IntPtr.Zero && !_disposed && generation == _generation)
                    _native.StopRecording(_handle, token);
            }
        }

        private void Unregister(Registration registration)
        {
            lock (_gate)
            {
                _waiting.Remove(registration);
                uint id = registration.Id;
                if (id == 0 || !_registrations.Remove(id))
                    return;
                if (_handle != IntPtr.Zero && !_disposed)
                    _native.Unregister(_handle, id);
            }
        }

        // must be called while holding _gate. Tried once per hook: only RetryStart tries again,
        // and only after a failure that may heal (_recoverable).
        private void EnsureStartedLocked()
        {
            if (_started)
                return;

            _started = true;
            var loadError = _native.EnsureLoaded();
            if (loadError != null)
            {
                _hookError = loadError;
                Debug.WriteLine("[NativeHotkeyHost] " + loadError);
                // a release build ships the library beside the app: missing or stale is a defect
                if (!OperatingSystem.IsLinux())
                    _report(new InvalidOperationException(loadError), "hotkey.library");
                return;
            }

            var buffer = new byte[512];
            int result;
            IntPtr handle;
            try
            {
                result = _native.Start(out handle, buffer);
            }
            catch (Exception ex)
            {
                // a library that loaded but cannot be called: a defect, like a missing one
                _hookError = StartError(ex);
                Debug.WriteLine("[NativeHotkeyHost] start threw: " + ex);
                _report(ex, "hotkey.hook-start");
                return;
            }

            if (result != CH_OK)
            {
                bool staleGrant = result == CH_ERR_PERMISSION && _isMacOS && _isTrusted();
                _hookError = staleGrant ? StaleGrantMessage : StartError(result, Utf8(buffer, buffer.Length));
                Debug.WriteLine("[NativeHotkeyHost] " + _hookError);
                // a NULL tap on macOS comes down to the grant too, as the standby capturer treats it
                _recoverable = _isMacOS && result is CH_ERR_PERMISSION or CH_ERR_HOOK_FAILED;
                // a listed app gets no prompt: keep this run's one for a grant that is really missing
                if (result == CH_ERR_PERMISSION && !staleGrant)
                    _accessibilityPrompt.RequestOnce();
                _staleGrantTicks = staleGrant ? Math.Clamp(_staleGrantTicks * 2, 2, StaleGrantMaxTicks) : 0;
                _staleGrantSkip = _staleGrantTicks > 0 ? _staleGrantTicks - 1 : 0;
                // once: a retried failure would otherwise report every RetryInterval
                if (result == CH_ERR_HOOK_FAILED && !_reportedHookFailure)
                {
                    _reportedHookFailure = true;
                    _report(new InvalidOperationException(_hookError), "hotkey.hook-start");
                }
                if (_recoverable)
                    StartRetryTimerLocked();
                return;
            }

            _handle = handle;
            int generation = ++_generation;
            _staleGrantTicks = _staleGrantSkip = 0;
            _pump = new Thread(() => Pump(handle, generation))
            {
                Name = "clowd-hotkeys-events",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();
        }

        /// <summary>Why <c>clowd_hotkeys_start</c> refused, from its code and the message it
        /// copied out. A missing permission (macOS only) asks for no restart: the host picks the
        /// grant up by itself.</summary>
        internal static string StartError(int result, string detail) => result switch
        {
            CH_ERR_PERMISSION => PermissionMessage,
            CH_ERR_UNSUPPORTED => "Global hotkeys are not supported on Linux yet.",
            _ => "Could not start the global hotkey listener: " + (String.IsNullOrEmpty(detail) ? $"error {result}" : detail),
        };

        /// <summary>Why the hook could not start when the call itself threw.</summary>
        internal static string StartError(Exception exception) =>
            "Could not start the global hotkey listener: " + exception.Message;

        /// <summary>What every registration and recording reports once the hook has died, with the
        /// reason it gave (or none). Only a hook that comes back by itself
        /// (<paramref name="restartsItself"/>, macOS) asks for no restart.</summary>
        internal static string StoppedError(string reason, bool restartsItself = false) =>
            "The global hotkey listener stopped" + (String.IsNullOrEmpty(reason) ? "." : ": " + reason.TrimEnd('.') + ".")
            + (restartsItself ? "" : " Restart Clowd to use global hotkeys again.");

        /// <summary>What a call reports when it finds the hook dead before its Stopped arrived.</summary>
        private string StoppedMessage => StoppedError("", restartsItself: _isMacOS);

        /// <summary>What a <c>clowd_hotkeys_next_event</c> result means for the event thread.</summary>
        internal enum NextEvent
        {
            /// <summary>An event was filled in.</summary>
            Event,
            Timeout,
            /// <summary>The queue closed: the hook stopped and everything it queued was delivered.
            /// The only clean end.</summary>
            Closed,
            /// <summary>Anything else (<c>CH_ERR_PANIC</c>, <c>CH_ERR_NULL</c>): the queue failed
            /// with the hook possibly still running and swallowing its gestures.</summary>
            Failed,
        }

        internal static NextEvent ClassifyNextEvent(int result) => result switch
        {
            1 => NextEvent.Event,
            0 => NextEvent.Timeout,
            -1 => NextEvent.Closed,
            _ => NextEvent.Failed,
        };

        /// <summary>The event thread of the hook of <paramref name="generation"/>: the only user of
        /// <paramref name="handle"/> outside <see cref="_gate"/>. Ends when the queue closes (the
        /// hook stopped and everything it queued was delivered), or fails — the hook is then
        /// stopped here.</summary>
        private void Pump(IntPtr handle, int generation)
        {
            while (true)
            {
                int result;
                HotkeyEventNative ev;
                try
                {
                    result = _native.NextEvent(handle, out ev, EventTimeoutMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] next_event threw: " + ex);
                    HookFailed(handle, generation, StoppedError(ex.Message), ex, "hotkey.events", stopHook: true, recoverable: false);
                    return;
                }

                switch (ClassifyNextEvent(result))
                {
                    case NextEvent.Timeout:
                        continue;
                    case NextEvent.Closed:
                        return;
                    case NextEvent.Failed:
                        HookFailed(handle, generation, StoppedError($"error {result}"), null, "hotkey.events", stopHook: true,
                            recoverable: false);
                        return;
                }

                try
                {
                    Dispatch(handle, generation, ev);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] event dispatch threw: " + ex);
                    _report(ex, "hotkey.events");
                }
            }
        }

        private void Dispatch(IntPtr handle, int generation, HotkeyEventNative ev)
        {
            bool down = (ev.Flags & 1) != 0;
            bool repeat = (ev.Flags & 2) != 0;
            var modifiers = (HotkeyModifiers)ev.Modifiers;

            switch ((int)ev.Kind)
            {
                case CH_EVENT_HOTKEY:
                {
                    Registration registration;
                    lock (_gate)
                        _registrations.TryGetValue(ev.Id, out registration);
                    if (registration == null || (repeat && !registration.FireOnRepeat))
                        return;

                    _post(() => RunHotkey(registration));
                    break;
                }

                case CH_EVENT_KEY:
                {
                    var observers = _observers;
                    if (observers.Length == 0)
                        return;

                    var character = ev.Ch == 0 ? null : char.ConvertFromUtf32((int)ev.Ch);
                    var key = new GlobalKeyEvent(down, repeat, (int)ev.RawCode, modifiers, character);
                    foreach (var observer in observers)
                    {
                        try
                        {
                            observer(key);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[NativeHotkeyHost] key observer threw: " + ex);
                        }
                    }
                    break;
                }

                case CH_EVENT_RECORDING_MODIFIERS:
                {
                    uint token = ev.Id;
                    _post(() =>
                    {
                        var session = _recording;
                        if (session != null && session.Token == token)
                            session.OnModifiersChanged(modifiers);
                    });
                    break;
                }

                case CH_EVENT_RECORDED:
                {
                    uint token = ev.Id;
                    var gesture = new HotkeyGesture((HotkeyKey)ev.Key, modifiers);
                    _post(() => DeliverRecorded(token, gesture));
                    break;
                }

                case CH_EVENT_STOPPED:
                {
                    // the hook ended itself; the queue drains and closes after this. On macOS this
                    // is the grant again (a revoked one ends the tap): RetryStart brings it back.
                    var buffer = new byte[512];
                    int length = _native.StopReason(handle, buffer);
                    HookFailed(handle, generation, StoppedError(Utf8(buffer, length), restartsItself: _isMacOS), null,
                        "hotkey.stopped", stopHook: false, recoverable: _isMacOS);
                    break;
                }
            }
        }

        /// <summary>UI thread: runs a press's action, unless its registration was disposed (or the
        /// host shut down, or its hook died) after the press was posted — its owner has already
        /// let it go.</summary>
        internal void RunHotkey(Registration registration)
        {
            lock (_gate)
            {
                if (!_registrations.TryGetValue(registration.Id, out var live) || !ReferenceEquals(live, registration))
                    return;
            }

            try
            {
                registration.Executed();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] hotkey callback threw: " + ex);
                _report(ex, "hotkey.callback");
            }
        }

        /// <summary>UI thread: hands a recorded gesture to the live session. One whose token is not
        /// the live session's belongs to a canceled (or failed) one, and is dropped.</summary>
        internal void DeliverRecorded(uint token, HotkeyGesture gesture)
        {
            var session = _recording;
            if (session == null || session.Token != token)
                return;

            _recording = null;
            session.OnRecorded(gesture);
        }

        /// <summary>
        /// Event thread: the hook of <paramref name="generation"/> is dead — it reported itself
        /// stopped, or its queue failed. Latches <see cref="_hookError"/> so every later
        /// registration and recording fails fast with it, stops the hook when it may still be
        /// running (<paramref name="stopHook"/>), so the gestures it swallowed reach apps again,
        /// and fails everything live on the UI thread. Acts once per hook. A
        /// <paramref name="recoverable"/> failure (macOS) moves the registrations to wait for
        /// <see cref="RetryStart"/>, which clears the latch when it starts the next hook.
        /// </summary>
        private void HookFailed(IntPtr handle, int generation, string error, Exception exception, string operation,
            bool stopHook, bool recoverable)
        {
            if (stopHook)
            {
                try
                {
                    // thread-safe and idempotent; the handle is freed only after this thread ends
                    _native.Stop(handle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
                }
            }

            // the user switching Accessibility off is expected, not a defect
            bool revoked = recoverable && !_isTrusted();

            Registration[] failed;
            bool report;
            lock (_gate)
            {
                if (_disposed || _hookError != null || generation != _generation)
                    return;

                _hookError = error;
                _recoverable = recoverable;
                // one waiting for this hook's successor (it found the hook dead first) has none now
                failed = recoverable ? _registrations.Values.ToArray() : [.. _registrations.Values, .. _waiting];
                if (!recoverable)
                    _waiting.Clear();
                // once per run: on macOS a hook that keeps dying is restarted each time
                report = !revoked && !(recoverable && _reportedHookStop);
                _reportedHookStop |= recoverable && report;
                if (recoverable)
                {
                    _registrations.Clear();
                    foreach (var registration in failed)
                    {
                        registration.Id = 0;
                        _waiting.Add(registration);
                    }
                    StartRetryTimerLocked();
                }
            }

            Debug.WriteLine("[NativeHotkeyHost] " + error);
            if (report)
                _report(exception ?? new InvalidOperationException(error), operation);
            _post(() => ApplyHookFailure(error, failed, generation));
        }

        /// <summary>UI thread: the hook of <paramref name="generation"/> died. Every registration it
        /// held fails with <paramref name="error"/> — unless a retry that ran first has set their
        /// statuses itself — and a live recording on it ends with it: no gesture will come, and the
        /// recording's owner (and through it the standby capturer's paused hotkeys) must not wait
        /// for one.</summary>
        internal void ApplyHookFailure(string error, Registration[] failed, int generation)
        {
            bool superseded;
            lock (_gate)
                superseded = _generation != generation;
            if (!superseded)
            {
                foreach (var registration in failed)
                    registration.SetStatus(false, error);
            }

            var session = _recording;
            if (session != null && session.Generation == generation)
            {
                _recording = null;
                session.OnFailed(error);
            }
        }

        private static string Utf8(byte[] buffer, int length)
        {
            length = Math.Clamp(length, 0, buffer.Length);
            int end = Array.IndexOf(buffer, (byte)0, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? length : end);
        }

        public void Dispose()
        {
            IntPtr handle;
            Thread pump;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                handle = _handle;
                pump = _pump;
                StopRetryTimerLocked();
                _registrations.Clear();
                _waiting.Clear();
                _observers = Array.Empty<Action<GlobalKeyEvent>>();
            }

            _recording = null;
            if (handle == IntPtr.Zero)
                return;

            try
            {
                // thread-safe, and makes the event thread's next_event return -1 once drained
                _native.Stop(handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
                _report(ex, "hotkey.dispose");
            }

            if (pump != null && !pump.Join(PumpJoinTimeout))
            {
                // freeing under a live next_event would be a use-after-free: leak instead (this is
                // app exit). _disposed already keeps every other host method off the handle.
                Debug.WriteLine("[NativeHotkeyHost] event thread did not exit; leaking the handle");
                return;
            }

            lock (_gate)
                _handle = IntPtr.Zero;
            _native.Free(handle);
        }

        private sealed class ObserverHandle : IDisposable
        {
            private NativeHotkeyHost _host;
            private readonly Action<GlobalKeyEvent> _observer;

            public ObserverHandle(NativeHotkeyHost host, Action<GlobalKeyEvent> observer)
            {
                _host = host;
                _observer = observer;
            }

            public void Dispose()
            {
                var host = _host;
                _host = null;
                host?.Unobserve(_observer);
            }
        }

        /// <summary>One recording (UI thread). Ends once: recorded, failed (its hook died), or
        /// disposed (a cancel).</summary>
        private sealed class Session : IDisposable
        {
            public uint Token { get; }

            /// <summary>The hook it records on (<see cref="_generation"/>).</summary>
            public int Generation { get; }

            private readonly NativeHotkeyHost _host;
            private readonly Action<HotkeyModifiers> _modifiersChanged;
            private readonly Action<HotkeyGesture> _recorded;
            private readonly Action<string> _failed;
            private bool _finished;

            public Session(NativeHotkeyHost host, uint token, int generation, Action<HotkeyModifiers> modifiersChanged,
                           Action<HotkeyGesture> recorded, Action<string> failed)
            {
                _host = host;
                Token = token;
                Generation = generation;
                _modifiersChanged = modifiersChanged;
                _recorded = recorded;
                _failed = failed;
            }

            public void OnModifiersChanged(HotkeyModifiers modifiers)
            {
                if (_finished)
                    return;

                try
                {
                    _modifiersChanged(modifiers);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] recording preview threw: " + ex);
                    _host._report(ex, "hotkey.recording");
                }
            }

            public void OnRecorded(HotkeyGesture gesture)
            {
                if (_finished)
                    return;

                _finished = true;
                try
                {
                    _recorded(gesture);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] recording callback threw: " + ex);
                    _host._report(ex, "hotkey.recording");
                }
            }

            public void OnFailed(string error)
            {
                if (_finished)
                    return;

                _finished = true;
                try
                {
                    _failed(error);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] recording failure callback threw: " + ex);
                    _host._report(ex, "hotkey.recording");
                }
            }

            public void Dispose()
            {
                if (_finished)
                    return;

                _finished = true;
                if (ReferenceEquals(_host._recording, this))
                    _host._recording = null;
                _host.StopRecording(Token, Generation);
            }
        }

        internal sealed class Registration : IGlobalTriggerRegistration
        {
            public readonly HotkeyGesture Gesture;
            public readonly Action Executed;
            public readonly bool FireOnRepeat;

            /// <summary>Its id on the live hook; 0 while it waits for one. Under the host's _gate.</summary>
            public uint Id;

            private NativeHotkeyHost _host;

            public bool IsRegistered { get; private set; }

            public string Error { get; private set; }

            public event EventHandler StatusChanged;

            public Registration(NativeHotkeyHost host, HotkeyGesture gesture, Action executed, bool fireOnRepeat,
                                bool isRegistered, string error)
            {
                _host = host;
                Gesture = gesture;
                Executed = executed;
                FireOnRepeat = fireOnRepeat;
                IsRegistered = isRegistered;
                Error = error ?? "";
            }

            /// <summary>A registration that failed up-front and never fires (a hook that cannot
            /// come back, a key the hook cannot use, a conflict).</summary>
            public static Registration Failed(string error) => new(null, null, null, false, false, error);

            // called on the UI thread
            public void SetStatus(bool isRegistered, string error)
            {
                error ??= "";
                if (IsRegistered == isRegistered && Error == error)
                    return;

                IsRegistered = isRegistered;
                Error = error;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            public void Dispose()
            {
                var host = _host;
                _host = null;
                host?.Unregister(this);
            }
        }
    }
}
