using System;
using System.Diagnostics;
using Prisc.Interop;

namespace Prisc.Ipc
{
    /// <summary>
    /// The Windows kill-on-close job every helper started with
    /// <see cref="HelperStartOptions.KillWithParent"/> is put in (HELPER_PROTOCOL.md §7.4): prisc_interop's
    /// <see cref="KillOnCloseJob"/>, one job per process, created on first use and never closed, so the
    /// OS kills every process still in it when Prisc.Ui exits, crashes or is killed.
    /// <para>
    /// Only direct children are in it: the job's silent breakaway keeps whatever a helper spawns out
    /// of it, most importantly the user's browser that prisc_capture opens for an OCR search, which
    /// would otherwise close whenever Prisc.Ui exits or updates. Helper grandchildren end through
    /// their own stdin lease instead.
    /// </para>
    /// <para>
    /// Strictly best-effort: Prisc.Ui may itself run inside a job that forbids nesting or breakaway
    /// (some launchers and CI runners), in which case assignment fails, is noted once in the
    /// helper's log, and the child falls back on stdin EOF. No-op on macOS and Linux, where stdin
    /// EOF and EPIPE do the same job.
    /// </para>
    /// </summary>
    internal static class JobObject
    {
        private static readonly object Sync = new();
        private static bool _failureReported;

        /// <summary>Puts <paramref name="process"/> in the job. Never throws; false when it could not
        /// (or on a platform without jobs).</summary>
        public static bool TryAssign(Process process, HelperLog log)
        {
            if (!OperatingSystem.IsWindows() || process == null)
                return false;

            try
            {
                return KillOnCloseJob.Assign(process.Handle);
            }
            catch (Exception ex)
            {
                ReportOnce(ex.Message, log);
                return false;
            }
        }

        private static void ReportOnce(string failure, HelperLog log)
        {
            lock (Sync)
            {
                if (_failureReported)
                    return;
                _failureReported = true;
            }

            var line = "[prisc] could not put the helper in the kill-on-close job; it will rely on stdin EOF: " + failure;
            Debug.WriteLine(line);
            log?.Append(line);
        }
    }
}
