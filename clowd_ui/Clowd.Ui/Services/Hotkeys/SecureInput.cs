using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clowd.UI
{
    /// <summary>macOS Secure Event Input: while any app has it on (a focused password field, a
    /// terminal's "Secure Keyboard Entry"), the OS hides every key from event taps, ours
    /// included.</summary>
    internal static class SecureInput
    {
        /// <summary>Whether it is on now. A plain read of the system state; false off macOS, and
        /// false when the query is unavailable, since then nothing can be said about it.</summary>
        public static bool IsEnabled()
        {
            if (!OperatingSystem.IsMacOS())
                return false;

            try
            {
                return IsSecureEventInputEnabled() != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SecureInput] IsSecureEventInputEnabled unavailable: " + ex);
                return false;
            }
        }

        // Carbon's Boolean is an unsigned char
        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern byte IsSecureEventInputEnabled();
    }
}
