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
    /// disposed on app exit.
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
    /// missing), every registration and recording reports why, and the app keeps running. If it
    /// dies later (it reports itself stopped, or its event queue fails), the hook is stopped for
    /// good — so the gestures it swallowed reach apps again — and every live registration and
    /// recording fails with the reason. Nothing restarts it short of restarting the app.</para>
    /// </remarks>
    internal sealed class NativeHotkeyHost : IGlobalTriggerHost, IDisposable
    {
        private static readonly string StoppedMessage = StoppedError("");
        private const int EventTimeoutMs = 500;
        private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);

        private readonly object _gate = new();
        private readonly Dictionary<uint, Registration> _registrations = new();
        private Action<GlobalKeyEvent>[] _observers = Array.Empty<Action<GlobalKeyEvent>>();
        private IntPtr _handle;
        private Thread _pump;
        private bool _started;
        private string _hookError; // non-null once the hook failed to start or died
        private bool _disposed;

        // UI thread only (written by StartRecording and the session, read by posted callbacks)
        private volatile Session _recording;

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
                    return Registration.Failed(_hookError);

                int result = HotkeysNative.clowd_hotkeys_register(_handle, (int)gesture.Key, (uint)gesture.Modifiers, out var id);
                if (result != CH_OK)
                    return Registration.Failed(RegisterError(result, gesture));

                return AddRegistration(id, executed, fireOnRepeat);
            }
        }

        /// <summary>Tracks a registration the hook accepted under <paramref name="id"/>: from here
        /// its presses run <paramref name="executed"/> until it is disposed.</summary>
        internal Registration AddRegistration(uint id, Action executed, bool fireOnRepeat)
        {
            lock (_gate)
            {
                var registration = new Registration(this, id, executed, fireOnRepeat);
                _registrations[id] = registration;
                return registration;
            }
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
                    HotkeysNative.clowd_hotkeys_set_observe(_handle, 1);
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
                    HotkeysNative.clowd_hotkeys_set_observe(_handle, 0);
            }
        }

        public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
            Action<string> failed, out string error)
        {
            if (modifiersChanged == null) throw new ArgumentNullException(nameof(modifiersChanged));
            if (recorded == null) throw new ArgumentNullException(nameof(recorded));
            if (failed == null) throw new ArgumentNullException(nameof(failed));

            uint token;
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

                if (HotkeysNative.clowd_hotkeys_start_recording(_handle, out token) != CH_OK)
                {
                    error = _hookError ?? StoppedMessage;
                    return null;
                }
            }

            error = null;
            return BeginSession(token, modifiersChanged, recorded, failed);
        }

        /// <summary>UI thread: makes the session the hook just armed under <paramref name="token"/>
        /// the live one, which the posted recording events are delivered to.</summary>
        internal IDisposable BeginSession(uint token, Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
            Action<string> failed)
        {
            var session = new Session(this, token, modifiersChanged, recorded, failed);
            _recording = session;
            return session;
        }

        private void StopRecording(uint token)
        {
            lock (_gate)
            {
                if (_handle != IntPtr.Zero && !_disposed)
                    HotkeysNative.clowd_hotkeys_stop_recording(_handle, token);
            }
        }

        private void Unregister(uint id)
        {
            lock (_gate)
            {
                _registrations.Remove(id);
                if (_handle != IntPtr.Zero && !_disposed)
                    HotkeysNative.clowd_hotkeys_unregister(_handle, id);
            }
        }

        // must be called while holding _gate. Tried once: an error is never retried.
        private void EnsureStartedLocked()
        {
            if (_started)
                return;

            _started = true;
            var loadError = HotkeysNative.EnsureLoaded();
            if (loadError != null)
            {
                _hookError = loadError;
                Debug.WriteLine("[NativeHotkeyHost] " + loadError);
                // a release build ships the library beside the app: missing or stale is a defect
                if (!OperatingSystem.IsLinux())
                    SentryConfig.CaptureHandled(new InvalidOperationException(loadError), "hotkey.library");
                return;
            }

            var buffer = new byte[512];
            int result;
            IntPtr handle;
            try
            {
                result = HotkeysNative.clowd_hotkeys_start(out handle, buffer, (uint)buffer.Length);
            }
            catch (Exception ex)
            {
                // a library that loaded but cannot be called: a defect, like a missing one
                _hookError = StartError(ex);
                Debug.WriteLine("[NativeHotkeyHost] start threw: " + ex);
                SentryConfig.CaptureHandled(ex, "hotkey.hook-start");
                return;
            }

            if (result != CH_OK)
            {
                _hookError = StartError(result, Utf8(buffer, buffer.Length));
                Debug.WriteLine("[NativeHotkeyHost] " + _hookError);
                if (result == CH_ERR_HOOK_FAILED)
                    SentryConfig.CaptureHandled(new InvalidOperationException(_hookError), "hotkey.hook-start");
                return;
            }

            _handle = handle;
            _pump = new Thread(() => Pump(handle))
            {
                Name = "clowd-hotkeys-events",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();
        }

        /// <summary>Why <c>clowd_hotkeys_start</c> refused, from its code and the message it
        /// copied out.</summary>
        internal static string StartError(int result, string detail) => result switch
        {
            CH_ERR_PERMISSION => "Could not start the global hotkey listener. Grant Accessibility permission to Clowd under " +
                                 "Settings → System → Permissions, then restart Clowd.",
            CH_ERR_UNSUPPORTED => "Global hotkeys are not supported on Linux yet.",
            _ => "Could not start the global hotkey listener: " + (String.IsNullOrEmpty(detail) ? $"error {result}" : detail),
        };

        /// <summary>Why the hook could not start when the call itself threw.</summary>
        internal static string StartError(Exception exception) =>
            "Could not start the global hotkey listener: " + exception.Message;

        /// <summary>What every registration and recording reports once the hook has died, with the
        /// reason it gave (or none).</summary>
        internal static string StoppedError(string reason) =>
            "The global hotkey listener stopped" + (String.IsNullOrEmpty(reason) ? "." : ": " + reason.TrimEnd('.') + ".")
            + " Restart Clowd to use global hotkeys again.";

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

        /// <summary>The event thread: the only user of <paramref name="handle"/> outside
        /// <see cref="_gate"/>. Ends when the queue closes (the hook stopped and everything it
        /// queued was delivered), or fails — the hook is then stopped here.</summary>
        private void Pump(IntPtr handle)
        {
            while (true)
            {
                int result;
                HotkeyEventNative ev;
                try
                {
                    result = HotkeysNative.clowd_hotkeys_next_event(handle, out ev, EventTimeoutMs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] next_event threw: " + ex);
                    HookFailed(handle, StoppedError(ex.Message), ex, "hotkey.events", stopHook: true);
                    return;
                }

                switch (ClassifyNextEvent(result))
                {
                    case NextEvent.Timeout:
                        continue;
                    case NextEvent.Closed:
                        return;
                    case NextEvent.Failed:
                        HookFailed(handle, StoppedError($"error {result}"), null, "hotkey.events", stopHook: true);
                        return;
                }

                try
                {
                    Dispatch(handle, ev);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] event dispatch threw: " + ex);
                    SentryConfig.CaptureHandled(ex, "hotkey.events");
                }
            }
        }

        private void Dispatch(IntPtr handle, HotkeyEventNative ev)
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

                    Dispatcher.UIThread.Post(() => RunHotkey(registration));
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
                    Dispatcher.UIThread.Post(() =>
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
                    Dispatcher.UIThread.Post(() => DeliverRecorded(token, gesture));
                    break;
                }

                case CH_EVENT_STOPPED:
                {
                    // the hook ended itself; the queue drains and closes after this
                    var buffer = new byte[512];
                    int length = (int)HotkeysNative.clowd_hotkeys_stop_reason(handle, buffer, (uint)buffer.Length);
                    HookFailed(handle, StoppedError(Utf8(buffer, length)), null, "hotkey.stopped", stopHook: false);
                    break;
                }
            }
        }

        /// <summary>UI thread: runs a press's action, unless its registration was disposed (or the
        /// host shut down) after the press was posted — its owner has already let it go.</summary>
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
                SentryConfig.CaptureHandled(ex, "hotkey.callback");
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
        /// Event thread: the hook is dead — it reported itself stopped, or its queue failed. Latches
        /// <see cref="_hookError"/> so every later registration and recording fails fast with it,
        /// stops the hook when it may still be running (<paramref name="stopHook"/>), so the gestures
        /// it swallowed reach apps again, and fails everything live on the UI thread. Acts once.
        /// </summary>
        private void HookFailed(IntPtr handle, string error, Exception exception, string operation, bool stopHook)
        {
            if (stopHook)
            {
                try
                {
                    // thread-safe and idempotent; the handle is freed only after this thread ends
                    HotkeysNative.clowd_hotkeys_stop(handle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
                }
            }

            Registration[] failed;
            lock (_gate)
            {
                if (_disposed || _hookError != null)
                    return;

                _hookError = error;
                failed = _registrations.Values.ToArray();
            }

            Debug.WriteLine("[NativeHotkeyHost] " + error);
            SentryConfig.CaptureHandled(exception ?? new InvalidOperationException(error), operation);
            Dispatcher.UIThread.Post(() => ApplyHookFailure(error, failed));
        }

        /// <summary>UI thread: the hook died. Every registration it held fails with
        /// <paramref name="error"/>, and a live recording ends with it — no gesture will come, and
        /// the recording's owner (and through it the standby capturer's paused hotkeys) must not
        /// wait for one.</summary>
        internal void ApplyHookFailure(string error, Registration[] failed)
        {
            foreach (var registration in failed)
                registration.SetStatus(false, error);

            var session = _recording;
            if (session != null)
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
                _registrations.Clear();
                _observers = Array.Empty<Action<GlobalKeyEvent>>();
            }

            _recording = null;
            if (handle == IntPtr.Zero)
                return;

            try
            {
                // thread-safe, and makes the event thread's next_event return -1 once drained
                HotkeysNative.clowd_hotkeys_stop(handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NativeHotkeyHost] stop threw: " + ex);
                SentryConfig.CaptureHandled(ex, "hotkey.dispose");
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
            HotkeysNative.clowd_hotkeys_free(handle);
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

        /// <summary>One recording (UI thread). Ends once: recorded, failed (the hook died), or
        /// disposed (a cancel).</summary>
        private sealed class Session : IDisposable
        {
            public uint Token { get; }

            private readonly NativeHotkeyHost _host;
            private readonly Action<HotkeyModifiers> _modifiersChanged;
            private readonly Action<HotkeyGesture> _recorded;
            private readonly Action<string> _failed;
            private bool _finished;

            public Session(NativeHotkeyHost host, uint token, Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
                Action<string> failed)
            {
                _host = host;
                Token = token;
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
                    SentryConfig.CaptureHandled(ex, "hotkey.recording");
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
                    SentryConfig.CaptureHandled(ex, "hotkey.recording");
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
                    SentryConfig.CaptureHandled(ex, "hotkey.recording");
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
            public readonly uint Id;
            public readonly Action Executed;
            public readonly bool FireOnRepeat;

            private NativeHotkeyHost _host;

            public bool IsRegistered { get; private set; }

            public string Error { get; private set; } = "";

            public event EventHandler StatusChanged;

            public Registration(NativeHotkeyHost host, uint id, Action executed, bool fireOnRepeat)
            {
                _host = host;
                Id = id;
                Executed = executed;
                FireOnRepeat = fireOnRepeat;
                IsRegistered = host != null;
            }

            /// <summary>A registration that failed up-front and never fires (no hook, a key the
            /// hook cannot use, a conflict).</summary>
            public static Registration Failed(string error) => new(null, 0, null, false) { Error = error };

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
                host?.Unregister(Id);
            }
        }
    }
}
