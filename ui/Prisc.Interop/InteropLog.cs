using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>The native library's log sink (README D3). Off until a host installs one.</summary>
    public static class InteropLog
    {
        private static Action<InteropLogLevel, string>? _sink;

        /// <summary>
        /// Installs (or, with null, removes) the process-global sink. Returns only after every
        /// in-flight call of the previous sink has returned. The sink runs on whatever thread called
        /// the library; it must not block and must not call into Prisc.Interop. A no-op when the
        /// library is not available.
        /// </summary>
        public static unsafe void SetSink(Action<InteropLogLevel, string>? sink)
        {
            if (!InteropLibrary.IsAvailable)
                return;

            Volatile.Write(ref _sink, sink);
            // The sink is process-global, so the thunk needs no user state and no GCHandle.
            delegate* unmanaged[Cdecl]<int, byte*, nuint, void*, void> thunk = sink == null ? null : &Receive;
            NativeMethods.prisc_log_set_callback(thunk, null);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void Receive(int level, byte* message, nuint length, void* user)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            try
            {
                Volatile.Read(ref _sink)?.Invoke((InteropLogLevel)level, Utf8.Read(message, length));
            }
            catch
            {
                // A log sink is never worth a crash.
            }
        }
    }
}
