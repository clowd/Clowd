using System;

namespace Clowd.Config
{
    /// <summary>
    /// Pluggable OS hotkey backend for <see cref="GlobalTrigger"/>. Clowd.Drawing contains no
    /// platform hook code — the UI layer installs an implementation via <see cref="GlobalTrigger.Host"/>
    /// at startup (SharpHook in Clowd.Ui). While no host is installed every trigger behaves like an
    /// inert stub: never registered, never fires.
    /// </summary>
    public interface IGlobalTriggerHost
    {
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
