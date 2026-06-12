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
    }

    /// <summary>Live handle for a single hotkey registration created by <see cref="IGlobalTriggerHost"/>.</summary>
    public interface IGlobalTriggerRegistration : IDisposable
    {
        bool IsRegistered { get; }

        string Error { get; }

        event EventHandler StatusChanged;
    }
}
