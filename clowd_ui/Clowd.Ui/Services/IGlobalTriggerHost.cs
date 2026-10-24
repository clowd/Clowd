using System;

namespace Clowd.UI
{
    /// <summary>
    /// OS hotkey backend used by <see cref="HotkeyManager"/>. The settings layer (Clowd.Shared)
    /// only stores gestures — all registration machinery lives in the UI layer.
    /// </summary>
    public interface IGlobalTriggerHost
    {
        /// <summary>
        /// Begin listening for <paramref name="gesture"/>. The returned handle reports live
        /// registration status (raising <see cref="IGlobalTriggerRegistration.StatusChanged"/> on the
        /// UI thread when it changes, e.g. when the hook fails to start asynchronously) and invokes
        /// <paramref name="executed"/> on the UI thread each time the gesture is pressed.
        /// Dispose the handle to remove the registration. With <paramref name="fireOnRepeat"/> the
        /// action also runs on the key's auto-repeats (the media keys); otherwise once per press.
        /// </summary>
        IGlobalTriggerRegistration RegisterTrigger(HotkeyGesture gesture, Action executed, bool fireOnRepeat = false);

        /// <summary>
        /// Observes every key press and release system-wide — for showing keystrokes, never for
        /// acting on them: nothing is swallowed. <paramref name="observer"/> runs on the host's
        /// hotkey event thread — not the hook thread (the hook only queues the edge) and not the UI
        /// thread — in the order the edges happened, so it must marshal anything it hands the UI.
        /// Starts the hook if no hotkey has yet. Silently never fires if the hook cannot run (macOS
        /// without the Accessibility permission). Dispose the handle to stop observing.
        /// </summary>
        IDisposable ObserveKeys(Action<GlobalKeyEvent> observer);

        /// <summary>
        /// Records one gesture through the hook, from whichever app has the focus: the next key
        /// press that is not a modifier is swallowed and handed to <paramref name="recorded"/>,
        /// with the modifiers held. Registered hotkeys do not fire meanwhile.
        /// <paramref name="modifiersChanged"/> follows the modifiers held as they change, for a
        /// preview. Both run on the UI thread; <paramref name="recorded"/> fires at most once and
        /// ends the session, and disposing the handle cancels it. Null when the hook is unavailable
        /// (<paramref name="error"/> says why, e.g. the Accessibility message) or another recording
        /// is already active (<paramref name="error"/> is null).
        /// </summary>
        IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded, out string error);
    }

    /// <summary>One key edge seen by <see cref="IGlobalTriggerHost.ObserveKeys"/>.</summary>
    /// <param name="IsDown">A press (auto-repeats included) rather than a release.</param>
    /// <param name="IsRepeat">An auto-repeat of a press.</param>
    /// <param name="RawCode">The OS's own key code: a Win32 virtual-key code on Windows, a
    /// <c>CGKeyCode</c> on macOS — the numbering the input-capture recorder writes.</param>
    /// <param name="Modifiers">The modifiers the hook saw held with the event.</param>
    /// <param name="Character">What a press typed under the foreground keyboard layout, worked
    /// out in the hook at the moment of the key; null for releases and keys that type nothing.</param>
    public readonly record struct GlobalKeyEvent(bool IsDown, bool IsRepeat, int RawCode, HotkeyModifiers Modifiers, string Character);

    /// <summary>Live handle for a single hotkey registration created by <see cref="IGlobalTriggerHost"/>.</summary>
    public interface IGlobalTriggerRegistration : IDisposable
    {
        bool IsRegistered { get; }

        string Error { get; }

        event EventHandler StatusChanged;
    }
}
