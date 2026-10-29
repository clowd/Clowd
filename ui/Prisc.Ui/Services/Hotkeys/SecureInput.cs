using Prisc.Interop;

namespace Prisc.UI
{
    /// <summary>macOS Secure Event Input: while any app has it on (a focused password field, a
    /// terminal's "Secure Keyboard Entry"), the OS hides every key from event taps, ours
    /// included.</summary>
    internal static class SecureInput
    {
        /// <summary>Whether it is on now. A plain read of the system state
        /// (<see cref="NativeKeyboard.SecureInputEnabled"/>); false off macOS.</summary>
        public static bool IsEnabled() => NativeKeyboard.SecureInputEnabled();
    }
}
