namespace Prisc.Interop
{
    /// <summary>The boundary's self-check, for the interop probe and diagnostics: no OS, GPU or
    /// permission behind it.</summary>
    public static class InteropDiagnostics
    {
        /// <summary>Asks <c>prisc_interop_selftest</c> for the outcome <paramref name="kind"/> names and
        /// returns the status it answered (never throws for a failing status); <paramref name="value"/>
        /// is 42 for <see cref="SelftestKind.Ok"/>. <paramref name="lastError"/> is this thread's last
        /// error text right after the call.</summary>
        public static unsafe InteropStatus SelfTest(SelftestKind kind, out long value, out string lastError)
        {
            InteropLibrary.Require();
            long v = 0;
            int status = NativeMethods.prisc_interop_selftest((int)kind, &v);
            lastError = Native.LastErrorMessage();
            value = v;
            return (InteropStatus)status;
        }
    }
}
