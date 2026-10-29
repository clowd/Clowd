namespace Prisc.Interop
{
    /// <summary>
    /// Hands this process's foreground rights to another process, so it may bring a window to the
    /// front once (<c>prisc_process_allow_foreground_windows</c>, the same call prisc_capture and
    /// the shell extension make).
    /// </summary>
    /// <remarks>Windows only: there is no foreground lock to hand over elsewhere, so off Windows
    /// every member returns false. Best-effort on Windows too: the grant works only while this
    /// process still holds the rights, so callers make it while they are the foreground app, and a
    /// refusal is false rather than an exception.</remarks>
    public static class Foreground
    {
        /// <summary>Lets <paramref name="processId"/> take the foreground once. False when nothing
        /// happened, and always for a processId of zero or less: the export reads 0 as "any
        /// process", which is reachable only through <see cref="AllowAny"/>. Any thread.</summary>
        public static bool Allow(int processId) => processId > 0 && Grant((uint)processId);

        /// <summary>Lets whichever process comes forward next take the foreground once
        /// (<c>ASFW_ANY</c>), for when the process that will raise a window is not known by pid.
        /// Any thread.</summary>
        public static bool AllowAny() => Grant(0);

        private static bool Grant(uint processId)
        {
            InteropLibrary.Require();
            int status = NativeMethods.prisc_process_allow_foreground_windows(processId);
            if (status == (int)InteropStatus.Unsupported || status == (int)InteropStatus.Os)
                return false;
            Native.Check(status, "prisc_process_allow_foreground_windows");
            return true;
        }
    }
}
