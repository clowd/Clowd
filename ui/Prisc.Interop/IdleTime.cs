using System;

namespace Prisc.Interop
{
    /// <summary>How long the whole machine has been without input (<c>prisc_idle_seconds</c>).</summary>
    public static unsafe class IdleTime
    {
        /// <summary>
        /// Time since the last keyboard or mouse input anywhere on the machine, not just in Prisc;
        /// null when the OS gave no answer or the platform has no system idle time. Throws
        /// <see cref="InteropException"/> when the OS query itself failed. Any thread.
        /// </summary>
        public static TimeSpan? SinceLastInput()
        {
            InteropLibrary.Require();
            double seconds;
            int status = NativeMethods.prisc_idle_seconds(&seconds);
            if (status == (int)InteropStatus.Unsupported)
                return null;
            return Native.Check(status, "prisc_idle_seconds") == InteropStatus.Ok ? TimeSpan.FromSeconds(seconds) : null;
        }
    }
}
