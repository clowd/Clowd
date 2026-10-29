using System;
using System.Runtime.InteropServices;

namespace Prisc.Interop
{
    /// <summary>A SafeHandle over one opaque prisc_interop handle: it owns exactly one
    /// <c>_create</c> result and calls exactly one <c>_destroy</c> (README C1, R19).</summary>
    /// <remarks>
    /// <para>csbindgen's bindings take raw pointers, so the CLR's automatic AddRef around a SafeHandle
    /// argument does not happen. Every call on a handle therefore goes through
    /// <c>using var lease = handle.Lease();</c> (<see cref="HandleLease"/>): <c>Dispose</c> while a call
    /// is in flight only marks the handle closed, and <see cref="Destroy"/> runs from
    /// <see cref="ReleaseHandle"/> once the last lease is returned.</para>
    /// <para>A wrapper whose handle can sit in a blocking call (an event wait, audio input) calls its
    /// <c>_stop</c> / <c>_request_stop</c> export before <c>Dispose</c>, so the blocked call returns.
    /// Thread-affine handles (the Windows foreground watch, the macOS services registration) do not
    /// use this type: they are plain <see cref="IDisposable"/>s with no finalizer.</para>
    /// </remarks>
    public abstract class InteropHandle : SafeHandle
    {
        protected InteropHandle() : base(IntPtr.Zero, ownsHandle: true) { }

        /// <summary>A crate handle is a pointer, or null when the <c>_create</c> failed.</summary>
        public override bool IsInvalid => handle == IntPtr.Zero;

        /// <summary>Calls this handle type's <c>_destroy</c> export; a negative status is a failure.</summary>
        protected abstract int Destroy(IntPtr h);

        private volatile bool _disposeRequested;

        /// <summary>Holds the handle open for one call. Throws <see cref="ObjectDisposedException"/>
        /// once <c>Dispose</c> was called, even while an earlier call still holds a lease (the CLR alone
        /// would hand out new references until the last one is returned).</summary>
        internal HandleLease Lease()
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            return new HandleLease(this);
        }

        protected override void Dispose(bool disposing)
        {
            _disposeRequested = true;
            base.Dispose(disposing);
        }

        /// <summary>Adopts a pointer a <c>_create</c> export wrote.</summary>
        protected void Adopt(IntPtr value) => SetHandle(value);

        protected override bool ReleaseHandle()
        {
            // Never throws: an exception out of ReleaseHandle on the finalizer thread ends the process,
            // and Destroy is a P/Invoke whose resolver throws when the library is unavailable.
            if (!InteropLibrary.IsAvailable && RequiresLibrary)
                return false;

            try
            {
                return Destroy(handle) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether <see cref="Destroy"/> calls into the library (true for every real handle;
        /// a test double overrides it).</summary>
        protected virtual bool RequiresLibrary => true;
    }
}
