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
        /// While true, registered gestures neither fire nor swallow key presses (used while a
        /// gesture is being captured in the settings editor).
        /// </summary>
        bool IsPaused { get; set; }

        /// <summary>
        /// Begin listening for <paramref name="gesture"/>. The returned handle reports live
        /// registration status (raising <see cref="IGlobalTriggerRegistration.StatusChanged"/> on the
        /// UI thread when it changes, e.g. when the hook fails to start asynchronously) and invokes
        /// <paramref name="executed"/> on the UI thread each time the gesture is pressed.
        /// Dispose the handle to remove the registration.
        /// </summary>
        IGlobalTriggerRegistration RegisterTrigger(SimpleKeyGesture gesture, Action executed);

        /// <summary>
        /// Observes every key press and release system-wide — for showing keystrokes, never for
        /// acting on them: nothing is swallowed. <paramref name="observer"/> runs on the hook
        /// thread, synchronously with the OS event, so it must be quick (a slow low-level hook is
        /// skipped by Windows) and must marshal anything it hands the UI. Starts the hook if no
        /// hotkey has yet. Silently never fires if the hook cannot run (macOS without the
        /// Accessibility permission). Dispose the handle to stop observing.
        /// </summary>
        IDisposable ObserveKeys(Action<GlobalKeyEvent> observer);
    }

    /// <summary>One key edge seen by <see cref="IGlobalTriggerHost.ObserveKeys"/>.</summary>
    /// <param name="IsDown">A press (auto-repeats included) rather than a release.</param>
    /// <param name="RawCode">The OS's own key code: a Win32 virtual-key code on Windows, a
    /// <c>CGKeyCode</c> on macOS — the numbering the input-capture recorder writes.</param>
    /// <param name="Modifiers">The modifiers the OS reported held with the event.</param>
    public readonly record struct GlobalKeyEvent(bool IsDown, int RawCode, Avalonia.Input.KeyModifiers Modifiers);

    /// <summary>Live handle for a single hotkey registration created by <see cref="IGlobalTriggerHost"/>.</summary>
    public interface IGlobalTriggerRegistration : IDisposable
    {
        bool IsRegistered { get; }

        string Error { get; }

        event EventHandler StatusChanged;
    }
}
