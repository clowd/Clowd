using Microsoft.Win32.SafeHandles;

namespace Prisc.Interop
{
    /// <summary>
    /// The helper protocol's stdout guard for a .NET helper (HELPER_PROTOCOL.md §3.2 and §7.6,
    /// <c>prisc_ipc_claim_stdout</c>): the same code every Rust helper runs.
    /// </summary>
    public static unsafe class StdoutGuard
    {
        /// <summary>
        /// Claims the process stdout: returns a private, non-inheritable handle onto the real stdout
        /// (the caller owns it), after which fd 1 and, on Windows, <c>STD_OUTPUT_HANDLE</c> and the
        /// UCRT's fd 1 point at stderr. <paramref name="redirected"/> is false when that redirect
        /// failed (already logged to stderr; stdout then still reaches the pipe). Once per process:
        /// a second call throws <see cref="InteropException"/> with <see cref="InteropStatus.State"/>.
        /// Call it before any native library is initialised. Any thread.
        /// </summary>
        public static SafeFileHandle Claim(out bool redirected)
        {
            InteropLibrary.Require();
            nint handle;
            byte moved;
            Native.Check(NativeMethods.prisc_ipc_claim_stdout(&handle, &moved), "prisc_ipc_claim_stdout");
            redirected = moved != 0;
            return new SafeFileHandle(handle, ownsHandle: true);
        }
    }
}
