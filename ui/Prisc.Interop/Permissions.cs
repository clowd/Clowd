namespace Prisc.Interop
{
    /// <summary>
    /// The OS privacy permissions (<c>prisc_permission_state</c>, <c>prisc_permission_request</c>).
    /// Screen Recording and Accessibility follow the one rule the hotkey hook, the scrolling driver and
    /// the capturer share (Accessibility is <c>AXIsProcessTrusted &amp;&amp; CGPreflightPostEventAccess</c>).
    /// Every kind is <see cref="PermissionState.NotApplicable"/> off macOS. Any thread.
    /// </summary>
    public static unsafe class Permissions
    {
        /// <summary>The current state of <paramref name="kind"/>. Never prompts.</summary>
        public static PermissionState State(PermissionKind kind)
        {
            InteropLibrary.Require();
            int state;
            Native.Check(NativeMethods.prisc_permission_state((int)kind, &state), "prisc_permission_state");
            return (PermissionState)state;
        }

        /// <summary>
        /// Makes sure <paramref name="kind"/> is granted, prompting only when it is not, and returns the
        /// state read afterwards. Already granted returns at once, so it is safe on every use. macOS
        /// offers each prompt once per app and a fresh Screen Recording grant reaches a running process
        /// only after a restart, so <see cref="PermissionState.Denied"/> here means "not usable yet",
        /// not "refused". Only Screen Recording and Accessibility can be requested: Microphone and
        /// Camera throw <see cref="InteropException"/> with <see cref="InteropStatus.InvalidArg"/>.
        /// </summary>
        public static PermissionState Ensure(PermissionKind kind)
        {
            InteropLibrary.Require();
            int state;
            Native.Check(NativeMethods.prisc_permission_request((int)kind, &state), "prisc_permission_request");
            return (PermissionState)state;
        }

        /// <summary>Whether nothing stands in the way: granted, or no such permission on this OS.</summary>
        public static bool IsUsable(this PermissionState state)
            => state is PermissionState.Granted or PermissionState.NotApplicable;
    }
}
