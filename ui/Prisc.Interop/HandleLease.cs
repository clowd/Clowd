using System;
using System.Runtime.InteropServices;

namespace Prisc.Interop
{
    /// <summary>Keeps an <see cref="InteropHandle"/> from being destroyed while one native call uses
    /// it (README R19): <see cref="SafeHandle.DangerousAddRef"/> on entry, <see cref="SafeHandle.DangerousRelease"/>
    /// on <see cref="Dispose"/>. A ref struct, so bracketing a per-frame call allocates nothing.</summary>
    /// <remarks>Leasing a handle that is already disposed throws <see cref="ObjectDisposedException"/>,
    /// which makes a call after <c>Dispose</c> a managed error rather than a dangling pointer.</remarks>
    internal ref struct HandleLease
    {
        private readonly SafeHandle _handle;
        private bool _taken;

        public HandleLease(SafeHandle handle)
        {
            _handle = handle;
            _taken = false;
            handle.DangerousAddRef(ref _taken);
        }

        /// <summary>The raw handle, valid for as long as this lease is.</summary>
        public readonly IntPtr Handle => _handle.DangerousGetHandle();

        public void Dispose()
        {
            if (_taken)
            {
                _taken = false;
                _handle.DangerousRelease();
            }
        }
    }
}
