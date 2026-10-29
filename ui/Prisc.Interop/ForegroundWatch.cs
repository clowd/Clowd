using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>
    /// Windows: calls back whenever the system's foreground window changes, to any window of any
    /// process, until disposed (<c>prisc_window_foreground_watch_*_windows</c>). The hook is out of
    /// context, so its events arrive through the creating thread's message loop and the callback runs
    /// there, synchronously (README R20): create it on the UI thread, and dispose it there.
    /// </summary>
    /// <remarks>
    /// Not a SafeHandle and no finalizer (README C1): unhooking is thread-affine, and a finalizer
    /// thread is the wrong one. The callback may dispose the watch from inside itself; the library
    /// releases its registry lock before calling.
    /// </remarks>
    public sealed unsafe class ForegroundWatch : IDisposable
    {
        private readonly Action _changed;
        private GCHandle _self;
        private PriscForegroundWatch* _watch;
        private int _logged;

        private ForegroundWatch(Action changed)
        {
            _changed = changed;
        }

        /// <summary>
        /// Starts watching, or answers null off Windows (no such hook). Throws
        /// <see cref="InteropException"/> when Windows refuses the hook.
        /// </summary>
        public static ForegroundWatch? Start(Action changed)
        {
            ArgumentNullException.ThrowIfNull(changed);
            InteropLibrary.Require();

            var watch = new ForegroundWatch(changed);
            watch._self = GCHandle.Alloc(watch);
            PriscForegroundWatch* native = null;
            int status = NativeMethods.prisc_window_foreground_watch_create_windows(
                &OnChanged, (void*)GCHandle.ToIntPtr(watch._self), &native);
            if (status == (int)InteropStatus.Ok)
            {
                watch._watch = native;
                return watch;
            }

            watch._self.Free();
            if (status == (int)InteropStatus.Unsupported)
                return null;
            throw Native.Failure(status, "prisc_window_foreground_watch_create_windows");
        }

        /// <summary>Unhooks; no callback runs once this returns. Must run on the creating thread
        /// (anywhere else throws and leaves the watch running). Idempotent.</summary>
        public void Dispose()
        {
            if (_watch == null)
                return;

            int status = NativeMethods.prisc_window_foreground_watch_destroy_windows(_watch);
            if (status == (int)InteropStatus.State)
                throw Native.Failure(status, "prisc_window_foreground_watch_destroy_windows");

            // gone either way; a panic the callback path recorded is reported, not fatal
            _watch = null;
            _self.Free();
            if (status < 0)
                Debug.WriteLine($"prisc_window_foreground_watch_destroy_windows: {(InteropStatus)status}. {Native.LastErrorMessage()}");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnChanged(void* user)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            ForegroundWatch? watch = null;
            try
            {
                watch = GCHandle.FromIntPtr((nint)user).Target as ForegroundWatch;
                watch?._changed();
            }
            catch (Exception e)
            {
                if (watch == null || Interlocked.Exchange(ref watch._logged, 1) == 0)
                    Debug.WriteLine($"A foreground watch callback threw: {e}");
            }
        }
    }
}
