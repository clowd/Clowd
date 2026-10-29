using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using Prisc.Interop;

namespace Prisc.UI
{
    /// <summary>
    /// <see cref="IGlobalTriggerHost"/> over prisc_interop's supervised global keyboard hook
    /// (WH_KEYBOARD_LL on Windows, a CGEventTap on macOS). Owned by <see cref="HotkeyManager"/>. The
    /// hook starts lazily on the first registration, observer or recording, and runs until the host
    /// is disposed on app exit.
    /// </summary>
    /// <remarks>
    /// <para>The hook itself decides what to swallow and only queues what happened; nothing here runs
    /// on it. A dedicated event thread (<c>prisc-hotkeys-events</c>) blocks on the queue, hands key
    /// edges to the observers right there, and posts everything else to the UI thread: hotkey
    /// actions, recording progress and state changes.</para>
    /// <para>Supervision is the hook's own (prisc_hotkeys' supervisor, shared with the standby
    /// capturer): a hook that cannot start or dies is retried where that can help (macOS: a missing
    /// or stale Accessibility grant, a tap that could not be made or died), a stale grant backing off
    /// to every 32 s; registrations keep their ids across restarts, and every change of state arrives
    /// as a Status event. This host maps those onto the registrations' statuses and the user text
    /// (<see cref="HotkeyStatusText"/>), fails a live recording when the hook goes down, and raises
    /// <see cref="RecordingAvailable"/> when it comes back.</para>
    /// <para>A missing permission asks for it (<see cref="AccessibilityPrompt"/>, once per run) while
    /// the hook has never run; a grant revoked under a running hook is the user's choice and asks for
    /// nothing. A stale grant asks for nothing either: the System page shows it as granted.</para>
    /// <para>Native-call discipline: registrations and recording sessions can outlive the host (an
    /// editor window closing during shutdown), so they never hold the hook. Every call goes through a
    /// host method that takes <see cref="_gate"/> and does nothing once the host is disposed; the
    /// event thread is the only other user, and the wrapper's lease keeps the handle alive under a
    /// call in flight.</para>
    /// </remarks>
    internal sealed class NativeHotkeyHost : IGlobalTriggerHost, IDisposable
    {
        private const uint EventTimeoutMs = 500;

        /// <summary>The first event (the initial Status) is queued before the hook's create returns;
        /// this only bounds a wait for one that is not.</summary>
        private const uint FirstStatusTimeoutMs = 2000;

        private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);

        private readonly object _gate = new();
        private readonly Dictionary<uint, Registration> _registrations = new(); // held by the hook, by id
        private Action<GlobalKeyEvent>[] _observers = Array.Empty<Action<GlobalKeyEvent>>();
        private INativeHotkeys _native;
        private Thread _pump;
        private bool _started;

        /// <summary>Non-null when there is no hook to wait for: it could not be created, or its event
        /// queue failed. Every registration and recording fails with it.</summary>
        private string _deadError;

        private HookState _state = HookState.Running;
        private string _stateError = ""; // the user text of a down _state
        private bool _everRan;
        private int _statusSeq; // bumped per Status, so a down state ends only the recordings it saw start
        private bool _reportedHookFailure;
        private bool _reportedHookStop;
        private bool _disposed;

        // UI thread only (written by StartRecording and the session, read by posted callbacks)
        private volatile Session _recording;

        private readonly Func<INativeHotkeys> _create;
        private readonly Action<Action> _post;
        private readonly AccessibilityPrompt _accessibilityPrompt;
        private readonly Action<Exception, string> _report;

        /// <summary>Raised on the UI thread when the hook is running again after it was down.</summary>
        public event EventHandler RecordingAvailable;

        public NativeHotkeyHost()
            : this(HotkeysBinding.Create, action => Dispatcher.UIThread.Post(action), AccessibilityPrompt.Shared,
                   SentryConfig.CaptureHandled)
        {
        }

        /// <param name="create">Starts the supervised hook (a fake in tests).</param>
        /// <param name="post">Runs an action on the UI thread, in order.</param>
        /// <param name="accessibilityPrompt">Asked when the hook lacks the permission.</param>
        /// <param name="report">Files a handled error (<see cref="SentryConfig.CaptureHandled"/>).</param>
        internal NativeHotkeyHost(Func<INativeHotkeys> create, Action<Action> post, AccessibilityPrompt accessibilityPrompt,
                                  Action<Exception, string> report)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _accessibilityPrompt = accessibilityPrompt ?? throw new ArgumentNullException(nameof(accessibilityPrompt));
            _report = report ?? throw new ArgumentNullException(nameof(report));
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
                if (_deadError != null)
                    return Registration.Failed(_deadError);

                uint id;
                GestureRefusal refusal;
                try
                {
                    if (!_native.TryRegister((int)gesture.Key, (uint)gesture.Modifiers, out id, out refusal))
                        return Registration.Failed(HotkeyStatusText.For(refusal, gesture));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] register threw: " + ex);
                    return Registration.Failed("Could not register the hotkey: " + ex.Message);
                }

                bool running = _state == HookState.Running;
                var registration = new Registration(this, gesture, executed, fireOnRepeat, running, running ? "" : _stateError) { Id = id };
                _registrations[id] = registration;
                return registration;
            }
        }

        /// <summary>Tracks a registration the hook holds under <paramref name="id"/>: from here its
        /// presses run <paramref name="executed"/> until it is disposed (tests).</summary>
        internal Registration AddRegistration(uint id, HotkeyGesture gesture, Action executed, bool fireOnRepeat)
        {
            lock (_gate)
            {
                var registration = new Registration(this, gesture, executed, fireOnRepeat, isRegistered: true, error: "") { Id = id };
                _registrations[id] = registration;
                return registration;
            }
        }

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
                // the hook keeps the switch across restarts, so it is set whatever its state
                if (_observers.Length == 1 && _native != null)
                    CallLocked(native => native.SetObserve(true), "set_observe");
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
                if (_observers.Length == 0 && _native != null && !_disposed)
                    CallLocked(native => native.SetObserve(false), "set_observe");
            }
        }

        public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
            Action<string> failed, out string error)
        {
            if (modifiersChanged == null) throw new ArgumentNullException(nameof(modifiersChanged));
            if (recorded == null) throw new ArgumentNullException(nameof(recorded));
            if (failed == null) throw new ArgumentNullException(nameof(failed));

            uint token;
            int seq;
            lock (_gate)
            {
                if (_disposed)
                {
                    error = "The hotkey host has been shut down.";
                    return null;
                }

                EnsureStartedLocked();
                if (_deadError != null)
                {
                    error = _deadError;
                    return null;
                }

                if (_state != HookState.Running)
                {
                    error = _stateError;
                    return null;
                }

                if (_recording != null)
                {
                    error = null;
                    return null;
                }

                bool started;
                try
                {
                    started = _native.TryStartRecording(out token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] start_recording threw: " + ex);
                    error = "Could not start recording: " + ex.Message;
                    return null;
                }

                if (!started)
                {
                    // down already, its Status still on the way
                    error = HotkeyStatusText.StoppedError("", restartsItself: true);
                    return null;
                }
                seq = _statusSeq;
            }

            error = null;
            return BeginSession(token, seq, modifiersChanged, recorded, failed);
        }

        /// <summary>UI thread: makes the session the hook just armed under <paramref name="token"/>,
        /// as of Status <paramref name="seq"/>, the live one the recording events are delivered to.</summary>
        internal IDisposable BeginSession(uint token, int seq, Action<HotkeyModifiers> modifiersChanged,
            Action<HotkeyGesture> recorded, Action<string> failed)
        {
            var session = new Session(this, token, seq, modifiersChanged, recorded, failed);
            _recording = session;
            return session;
        }

        private void StopRecording(uint token)
        {
            lock (_gate)
            {
                // tokens are the hook's own across restarts: a stale one cancels nothing
                if (_native != null && !_disposed)
                    CallLocked(native => native.StopRecording(token), "stop_recording");
            }
        }

        private void Unregister(Registration registration)
        {
            lock (_gate)
            {
                uint id = registration.Id;
                if (id == 0 || !_registrations.Remove(id))
                    return;
                if (_native != null && !_disposed)
                    CallLocked(native => native.Unregister(id), "unregister");
            }
        }

        // must be called while holding _gate. A call on a hook that is gone (stopped after its queue
        // failed) is harmless; one that throws is a defect worth a report, never a crash.
        private void CallLocked(Action<INativeHotkeys> call, string operation)
        {
            try
            {
                call(_native);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeHotkeyHost] {operation} threw: {ex}");
                _report(ex, "hotkey." + operation);
            }
        }

        // must be called while holding _gate. Creates the one hook this host ever has: the hook
        // restarts itself, so a failure here is one with no hook to wait for.
        private void EnsureStartedLocked()
        {
            if (_started)
                return;

            _started = true;
            INativeHotkeys native;
            try
            {
                native = _create();
            }
            catch (InteropException ex) when (ex.Status == InteropStatus.Unsupported)
            {
                _deadError = HotkeyStatusText.UnsupportedMessage;
                return;
            }
            catch (InteropUnavailableException ex)
            {
                _deadError = HotkeyStatusText.StartError(ex.Message);
                Debug.WriteLine("[NativeHotkeyHost] " + _deadError);
                // a release build ships the library beside the app: missing is a defect
                if (!OperatingSystem.IsLinux())
                    _report(ex, "hotkey.library");
                return;
            }
            catch (Exception ex)
            {
                _deadError = HotkeyStatusText.StartError(ex.Message);
                Debug.WriteLine("[NativeHotkeyHost] create threw: " + ex);
                _report(ex, "hotkey.hook-start");
                return;
            }

            // the first event is the hook's initial state, queued before create returned
            HotkeyEvent first;
            try
            {
                if (native.NextEvent(FirstStatusTimeoutMs, out first) != InteropStatus.Ok || first.Kind != HotkeysEventKind.Status)
                    throw new InvalidOperationException("the hook reported no initial state");
            }
            catch (Exception ex)
            {
                _deadError = HotkeyStatusText.StartError(ex.Message);
                Debug.WriteLine("[NativeHotkeyHost] " + _deadError);
                _report(ex, "hotkey.hook-start");
                StopAndDispose(native);
                return;
            }

            _native = native;
            UpdateStateLocked(first);
            _pump = new Thread(() => Pump(native))
            {
                Name = "prisc-hotkeys-events",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();
        }

        // must be called while holding _gate: takes in a Status, asks for the permission or files a
        // report where that is due. Returns the state it replaced.
        private HookState UpdateStateLocked(HotkeyEvent status)
        {
            var was = _state;
            _state = status.HookState;
            _statusSeq++;
            string code = HotkeyStatusText.CodeOf(_state);
            _stateError = code == null ? "" : HotkeyStatusText.For(code, status.Reason, status.IsRecoverable);
            Debug.WriteLine("[NativeHotkeyHost] hook state " + _state + (code == null ? "" : ": " + _stateError));
            switch (_state)
            {
                case HookState.Running:
                    _everRan = true;
                    break;
                case HookState.PermissionMissing:
                    // a grant revoked under a running hook is the user's choice: ask only for one
                    // that was never there
                    if (!_everRan)
                        _accessibilityPrompt.RequestOnce();
                    break;
                case HookState.Failed:
                    // once: a retried failure would otherwise report every retry
                    if (!_reportedHookFailure)
                    {
                        _reportedHookFailure = true;
                        _report(new InvalidOperationException(_stateError), "hotkey.hook-start");
                    }
                    break;
                case HookState.Stopped:
                    // once per run where the hook restarts itself, as a tap that keeps dying would
                    // otherwise report every time
                    bool report = !(status.IsRecoverable && _reportedHookStop);
                    _reportedHookStop |= status.IsRecoverable && report;
                    if (report)
                        _report(new InvalidOperationException(_stateError), "hotkey.stopped");
                    break;
            }
            return was;
        }

        /// <summary>The event thread: the only user of the hook outside <see cref="_gate"/>. Ends
        /// when the queue closes (the host stopped it) or fails, which stops the hook for good.</summary>
        private void Pump(INativeHotkeys native)
        {
            while (true)
            {
                InteropStatus result;
                HotkeyEvent ev;
                try
                {
                    result = native.NextEvent(EventTimeoutMs, out ev);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] next_event threw: " + ex);
                    QueueFailed(native, ex);
                    return;
                }

                if (result == InteropStatus.Timeout)
                    continue;
                if (result != InteropStatus.Ok)
                    return;

                try
                {
                    Dispatch(ev);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] event dispatch threw: " + ex);
                    _report(ex, "hotkey.events");
                }
            }
        }

        private void Dispatch(HotkeyEvent ev)
        {
            var modifiers = (HotkeyModifiers)ev.Modifiers;
            switch (ev.Kind)
            {
                case HotkeysEventKind.Hotkey:
                {
                    Registration registration;
                    lock (_gate)
                        _registrations.TryGetValue(ev.Id, out registration);
                    if (registration == null || (ev.IsRepeat && !registration.FireOnRepeat))
                        return;

                    _post(() => RunHotkey(registration));
                    break;
                }

                case HotkeysEventKind.Key:
                {
                    var observers = _observers;
                    if (observers.Length == 0)
                        return;

                    var key = new GlobalKeyEvent(ev.IsDown, ev.IsRepeat, ev.RawCode, modifiers, ev.Character);
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

                case HotkeysEventKind.RecordingModifiers:
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

                case HotkeysEventKind.Recorded:
                {
                    uint token = ev.Id;
                    var gesture = new HotkeyGesture((HotkeyKey)ev.Key, modifiers);
                    _post(() => DeliverRecorded(token, gesture));
                    break;
                }

                case HotkeysEventKind.Status:
                    ApplyStatus(ev);
                    break;
            }
        }

        /// <summary>Event thread: the hook came up, went down, or is down for another reason now.</summary>
        private void ApplyStatus(HotkeyEvent status)
        {
            Registration[] registrations;
            bool running, cameBack;
            string error;
            int seq;
            lock (_gate)
            {
                if (_disposed || _deadError != null)
                    return;

                var was = UpdateStateLocked(status);
                running = _state == HookState.Running;
                cameBack = running && was != HookState.Running;
                error = _stateError;
                seq = _statusSeq;
                registrations = _registrations.Values.ToArray();
            }

            if (running)
            {
                _post(() =>
                {
                    foreach (var registration in registrations)
                        registration.SetStatus(true, "");
                    if (cameBack)
                    {
                        Debug.WriteLine("[NativeHotkeyHost] the global hotkey listener is running again");
                        RecordingAvailable?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
            else
            {
                _post(() => ApplyHookFailure(error, registrations, seq));
            }
        }

        /// <summary>Event thread: the hook's queue failed, with the hook possibly still running and
        /// swallowing its gestures: stop it, so they reach apps again, and fail everything for good.</summary>
        private void QueueFailed(INativeHotkeys native, Exception exception)
        {
            try
            {
                native.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
            }

            Registration[] failed;
            string error;
            int seq;
            lock (_gate)
            {
                if (_disposed || _deadError != null)
                    return;
                error = _deadError = HotkeyStatusText.StoppedError(exception.Message);
                seq = ++_statusSeq;
                failed = _registrations.Values.ToArray();
            }

            _report(exception, "hotkey.events");
            _post(() => ApplyHookFailure(error, failed, seq));
        }

        /// <summary>UI thread: runs a press's action, unless its registration was disposed (or the
        /// host shut down) after the press was posted: its owner has already let it go.</summary>
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
        /// the live session's belongs to a cancelled (or failed) one, and is dropped.</summary>
        internal void DeliverRecorded(uint token, HotkeyGesture gesture)
        {
            var session = _recording;
            if (session == null || session.Token != token)
                return;

            _recording = null;
            session.OnRecorded(gesture);
        }

        /// <summary>UI thread: the hook is down as of Status <paramref name="seq"/>. Every
        /// registration it held reports <paramref name="error"/>, and a recording started before that
        /// Status ends with it: no gesture will come, and the recording's owner (and through it the
        /// standby capturer's paused hotkeys) must not wait for one.</summary>
        internal void ApplyHookFailure(string error, Registration[] failed, int seq)
        {
            foreach (var registration in failed)
                registration.SetStatus(false, error);

            var session = _recording;
            if (session != null && session.Seq < seq)
            {
                _recording = null;
                session.OnFailed(error);
            }
        }

        public void Dispose()
        {
            INativeHotkeys native;
            Thread pump;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                native = _native;
                pump = _pump;
                _registrations.Clear();
                _observers = Array.Empty<Action<GlobalKeyEvent>>();
            }

            _recording = null;
            if (native == null)
                return;

            try
            {
                // makes the event thread's NextEvent drain and report Closed
                native.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
                _report(ex, "hotkey.dispose");
            }

            if (pump != null && !pump.Join(PumpJoinTimeout))
                Debug.WriteLine("[NativeHotkeyHost] event thread did not exit; the handle goes once its call returns");

            // the wrapper's lease defers the native destroy past a call still in flight
            native.Dispose();
        }

        private static void StopAndDispose(INativeHotkeys native)
        {
            try
            {
                native.Stop();
                native.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
            }
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

        /// <summary>One recording (UI thread). Ends once: recorded, failed (the hook went down), or
        /// disposed (a cancel).</summary>
        private sealed class Session : IDisposable
        {
            public uint Token { get; }

            /// <summary>The Status it started under (<see cref="_statusSeq"/>).</summary>
            public int Seq { get; }

            private readonly NativeHotkeyHost _host;
            private readonly Action<HotkeyModifiers> _modifiersChanged;
            private readonly Action<HotkeyGesture> _recorded;
            private readonly Action<string> _failed;
            private bool _finished;

            public Session(NativeHotkeyHost host, uint token, int seq, Action<HotkeyModifiers> modifiersChanged,
                           Action<HotkeyGesture> recorded, Action<string> failed)
            {
                _host = host;
                Token = token;
                Seq = seq;
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
                _host.StopRecording(Token);
            }
        }

        internal sealed class Registration : IGlobalTriggerRegistration
        {
            public readonly HotkeyGesture Gesture;
            public readonly Action Executed;
            public readonly bool FireOnRepeat;

            /// <summary>Its id on the hook, stable across the hook's restarts. Under the host's _gate.</summary>
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

            /// <summary>A registration that failed up front and never fires (no hook, a key the hook
            /// cannot use, a duplicate).</summary>
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
