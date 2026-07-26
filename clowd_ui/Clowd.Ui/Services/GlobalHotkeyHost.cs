using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using SharpHook;
using SharpHook.Data;

namespace Clowd.UI
{
    /// <summary>
    /// SharpHook (libuiohook) implementation of <see cref="IGlobalTriggerHost"/>. Owned by
    /// <see cref="HotkeyManager"/> so the settings layer never references SharpHook itself.
    /// The underlying keyboard hook is started lazily on the first registration and runs until
    /// the host is disposed on app exit.
    /// </summary>
    /// <remarks>
    /// Threading: <see cref="SimpleGlobalHook"/> raises events on its dedicated hook thread, which is
    /// required for <see cref="HookEventArgs.SuppressEvent"/> to take effect synchronously (this is
    /// what stops e.g. PrintScreen from also firing the OS screenshot handler). Trigger callbacks and
    /// status updates are marshalled to the Avalonia UI thread. On macOS the hook needs the
    /// Accessibility permission; if it fails to start, all registrations report a helpful error via
    /// <see cref="IGlobalTriggerRegistration.StatusChanged"/> and the app keeps running normally.
    /// </remarks>
    internal sealed class GlobalHotkeyHost : IGlobalTriggerHost, IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<Registration> _registrations = new List<Registration>();
        private SimpleGlobalHook _hook;
        private string _hookError; // non-null once the hook failed to start
        private bool _disposed;
        private volatile bool _isPaused;

        /// <summary>While true, hotkeys neither fire nor swallow key presses (checked on the hook
        /// thread before <see cref="HookEventArgs.SuppressEvent"/> is applied).</summary>
        public bool IsPaused
        {
            get => _isPaused;
            set => _isPaused = value;
        }

        public IGlobalTriggerRegistration RegisterTrigger(SimpleKeyGesture gesture, Action executed)
        {
            if (gesture == null) throw new ArgumentNullException(nameof(gesture));
            if (executed == null) throw new ArgumentNullException(nameof(executed));

            var keyCode = SharpHookKeyMap.TryMapKey(gesture.Key);
            if (keyCode == null)
                return Registration.Failed($"The key '{gesture.Key}' cannot be used as a global hotkey.");

            lock (_gate)
            {
                if (_disposed)
                    return Registration.Failed("The hotkey host has been shut down.");

                foreach (var other in _registrations)
                {
                    if (other.KeyCode == keyCode.Value && other.Modifiers == gesture.Modifiers)
                        return Registration.Failed("Gesture is already in-use by another hotkey.");
                }

                EnsureHookStarted();

                var reg = new Registration(this, keyCode.Value, gesture.Modifiers, executed);
                reg.SetStatus(_hookError == null, _hookError);
                _registrations.Add(reg);
                return reg;
            }
        }

        // must be called while holding _gate.
        private void EnsureHookStarted()
        {
            if (_hook != null || _hookError != null)
                return;

            try
            {
                var hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
                hook.KeyPressed += OnKeyPressed;
                _hook = hook;

                hook.RunAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        OnHookFailed(t.Exception.GetBaseException());
                }, TaskScheduler.Default);

                Debug.WriteLine("[GlobalHotkeyHost] SharpHook keyboard hook started");
            }
            catch (Exception ex)
            {
                _hook = null;
                OnHookFailed(ex);
                SentryConfig.CaptureHandled(ex, "hotkey.hook-start");
            }
        }

        private void OnHookFailed(Exception ex)
        {
            Debug.WriteLine("[GlobalHotkeyHost] failed to start SharpHook: " + ex);

            string message = OperatingSystem.IsMacOS()
                ? "Could not start the global hotkey listener. Grant Accessibility permission to Clowd under " +
                  "Settings → General → Permissions, then restart Clowd."
                : "Could not start the global hotkey listener: " + ex.Message;

            SimpleGlobalHook hook;
            Registration[] failed;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _hookError = message;
                hook = _hook;
                _hook = null;
                failed = _registrations.ToArray();
            }

            DisposeHook(hook);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var reg in failed)
                    reg.SetStatus(false, message);
            });
        }

        private void OnKeyPressed(object sender, KeyboardHookEventArgs e)
        {
            // while a gesture is being edited in settings, hotkeys must neither fire nor swallow keys.
            if (_isPaused)
                return;

            Registration match = null;
            lock (_gate)
            {
                foreach (var reg in _registrations)
                {
                    if (reg.KeyCode != e.Data.KeyCode)
                        continue;
                    if (!SharpHookKeyMap.ModifiersMatch(e.RawEvent.Mask, reg.Modifiers))
                        continue;

                    match = reg;
                    break;
                }
            }

            if (match == null)
                return;

            // suppress BEFORE marshalling — must happen on the hook thread to take effect.
            e.SuppressEvent = true;

            var executed = match.Executed;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    executed();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GlobalHotkeyHost] hotkey callback threw: " + ex);
                    SentryConfig.CaptureHandled(ex, "hotkey.callback");
                }
            });
        }

        private void Unregister(Registration reg)
        {
            lock (_gate)
            {
                _registrations.Remove(reg);
            }
        }

        public void Dispose()
        {
            SimpleGlobalHook hook;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _registrations.Clear();
                hook = _hook;
                _hook = null;
            }

            DisposeHook(hook);
        }

        private void DisposeHook(SimpleGlobalHook hook)
        {
            if (hook == null)
                return;

            hook.KeyPressed -= OnKeyPressed;
            try
            {
                hook.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GlobalHotkeyHost] hook dispose threw: " + ex);
                SentryConfig.CaptureHandled(ex, "hotkey.dispose");
            }
        }

        private sealed class Registration : IGlobalTriggerRegistration
        {
            public readonly KeyCode KeyCode;
            public readonly Avalonia.Input.KeyModifiers Modifiers;
            public readonly Action Executed;

            private GlobalHotkeyHost _host;

            public bool IsRegistered { get; private set; }

            public string Error { get; private set; } = "";

            public event EventHandler StatusChanged;

            public Registration(GlobalHotkeyHost host, KeyCode keyCode, Avalonia.Input.KeyModifiers modifiers, Action executed)
            {
                _host = host;
                KeyCode = keyCode;
                Modifiers = modifiers;
                Executed = executed;
            }

            /// <summary>A registration that failed up-front and never listens (unmappable key, conflict).</summary>
            public static Registration Failed(string error)
            {
                return new Registration(null, KeyCode.VcUndefined, Avalonia.Input.KeyModifiers.None, null) { Error = error };
            }

            // called on the UI thread (or synchronously during RegisterTrigger, before anyone subscribes).
            public void SetStatus(bool isRegistered, string error)
            {
                error = error ?? "";
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
