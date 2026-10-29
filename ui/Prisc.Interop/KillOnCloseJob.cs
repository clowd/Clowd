using System;

namespace Prisc.Interop
{
    /// <summary>
    /// The Windows kill-on-close job every helper spawned with <c>KillWithParent</c> joins
    /// (HELPER_PROTOCOL.md §7.4, <c>prisc_process_assign_kill_on_close_job_windows</c>): one job per
    /// process, created on first use and never closed, so the OS kills every process still in it
    /// when this one exits, crashes or is killed. Whatever a helper spawns stays out of it
    /// (silent breakaway).
    /// </summary>
    public static class KillOnCloseJob
    {
        /// <summary>
        /// Puts the process behind <paramref name="processHandle"/> (borrowed for the call) into the
        /// job. True when it is in; false off Windows, where stdin EOF and EPIPE do the same job.
        /// Throws <see cref="InteropException"/> when the job could not be created (latched: every
        /// later call fails the same way) or the process could not be assigned, which a host job
        /// that forbids nesting or breakaway causes. Any thread.
        /// </summary>
        public static bool Assign(IntPtr processHandle)
        {
            InteropLibrary.Require();
            int status = NativeMethods.prisc_process_assign_kill_on_close_job_windows(processHandle);
            if (status == (int)InteropStatus.Unsupported)
                return false;
            Native.Check(status, "prisc_process_assign_kill_on_close_job_windows");
            return true;
        }
    }
}
