using System;
using Prisc.Interop;

namespace Prisc.UI
{
    /// <summary>
    /// One supervised hook as <see cref="NativeHotkeyHost"/> drives it, member for member the
    /// <see cref="GlobalKeyboardHook"/> wrapper over the <c>prisc_hotkeys_*</c> exports
    /// (<see cref="HotkeysBinding"/> in the app, a fake in the tests), so the host's handling of a
    /// hook that is down, comes back or dies is testable without installing one. The hook retries,
    /// re-registers and reports its state by itself (prisc_hotkeys' supervisor); the host only maps
    /// its events onto registrations, recordings and user text.
    /// </summary>
    internal interface INativeHotkeys : IDisposable
    {
        /// <summary>False with the refusal when the gesture itself cannot be a global hotkey. A
        /// registration made while the hook is down is held and goes live when it is back.</summary>
        bool TryRegister(int key, uint modifiers, out uint id, out GestureRefusal refusal);

        void Unregister(uint id);

        void SetObserve(bool observe);

        /// <summary>False while the hook is down.</summary>
        bool TryStartRecording(out uint session);

        void StopRecording(uint session);

        /// <summary><see cref="InteropStatus.Ok"/> (an event), <see cref="InteropStatus.Timeout"/> or
        /// <see cref="InteropStatus.Closed"/> (stopped and drained); throws when the queue failed.</summary>
        InteropStatus NextEvent(uint timeoutMs, out HotkeyEvent hotkeyEvent);

        /// <summary>Stops the hook; a blocked <see cref="NextEvent"/> then drains and closes.</summary>
        void Stop();
    }

    /// <summary><see cref="INativeHotkeys"/> over prisc_interop's <see cref="GlobalKeyboardHook"/>.</summary>
    internal sealed class HotkeysBinding : INativeHotkeys
    {
        private readonly GlobalKeyboardHook _hook;

        private HotkeysBinding(GlobalKeyboardHook hook) => _hook = hook;

        /// <summary>Starts the hook (see <see cref="GlobalKeyboardHook.Create"/> for what it throws).</summary>
        public static INativeHotkeys Create() => new HotkeysBinding(GlobalKeyboardHook.Create());

        public bool TryRegister(int key, uint modifiers, out uint id, out GestureRefusal refusal) =>
            _hook.TryRegister(key, modifiers, out id, out refusal);

        public void Unregister(uint id) => _hook.Unregister(id);

        public void SetObserve(bool observe) => _hook.SetObserve(observe);

        public bool TryStartRecording(out uint session) => _hook.TryStartRecording(out session);

        public void StopRecording(uint session) => _hook.StopRecording(session);

        public InteropStatus NextEvent(uint timeoutMs, out HotkeyEvent hotkeyEvent) => _hook.NextEvent(timeoutMs, out hotkeyEvent);

        public void Stop() => _hook.Stop();

        public void Dispose() => _hook.Dispose();
    }
}
