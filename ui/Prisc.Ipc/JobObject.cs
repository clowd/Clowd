using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prisc.Ipc
{
    /// <summary>
    /// The Windows kill-on-close job every helper started with
    /// <see cref="HelperStartOptions.KillWithParent"/> is put in (HELPER_PROTOCOL.md §7.4). One job
    /// per process, created on first use and never closed: the OS closes the handle when Prisc.Ui
    /// exits, crashes or is killed, and that kills every process still in the job.
    /// <para>
    /// Only direct children are in it. <c>JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK</c> keeps whatever a
    /// helper spawns out of the job, most importantly the user's browser that prisc_capture opens
    /// for an OCR search: without it that browser would be created inside the job when none was
    /// already running, and closed whenever Prisc.Ui exits or updates. Helper grandchildren end
    /// through their own stdin lease instead.
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
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private static readonly object Sync = new();
        private static IntPtr _job;
        private static bool _createFailed;
        private static bool _failureReported;

        /// <summary>Puts <paramref name="process"/> in the job. Never throws; false when it could not
        /// (or on a platform without jobs).</summary>
        public static bool TryAssign(Process process, HelperLog log)
        {
            if (!OperatingSystem.IsWindows() || process == null)
                return false;

            string failure;
            try
            {
                var job = GetOrCreate(out failure);
                if (job != IntPtr.Zero)
                {
                    if (AssignProcessToJobObject(job, process.Handle))
                        return true;
                    failure = "AssignProcessToJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            ReportOnce(failure, log);
            return false;
        }

        private static IntPtr GetOrCreate(out string failure)
        {
            failure = null;
            lock (Sync)
            {
                if (_job != IntPtr.Zero)
                    return _job;
                if (_createFailed)
                {
                    failure = "the kill-on-close job could not be created";
                    return IntPtr.Zero;
                }

                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    _createFailed = true;
                    failure = "CreateJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
                    },
                };
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                        (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    failure = "SetInformationJobObject failed (error " + Marshal.GetLastWin32Error() + ")";
                    CloseHandle(job);
                    _createFailed = true;
                    return IntPtr.Zero;
                }

                _job = job;
                return job;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
