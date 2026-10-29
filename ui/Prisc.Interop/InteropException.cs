using System;

namespace Prisc.Interop
{
    /// <summary>A native call failed. <see cref="Status"/> says how, <see cref="NativeCode"/> carries
    /// the raw HRESULT, OSStatus or errno when there was one, <see cref="Operation"/> names the export.</summary>
    /// <remarks>The numeric code stays out of the message's leading clause (it is in
    /// <see cref="NativeCode"/> and <c>Data["prisc.native_code"]</c>), so Sentry groups these by
    /// operation rather than splitting one fault across every code it can produce.</remarks>
    public sealed class InteropException : Exception
    {
        public InteropException(InteropStatus status, long nativeCode, string operation, string detail)
            : base(String.IsNullOrEmpty(detail) ? $"{operation} failed: {status}" : $"{operation} failed: {status}. {detail}")
        {
            Status = status;
            NativeCode = nativeCode;
            Operation = operation;
            Data["prisc.native_code"] = nativeCode;
        }

        /// <summary>How the call failed.</summary>
        public InteropStatus Status { get; }

        /// <summary>The raw OS code, sign-extended from 32 bits; 0 when the failure was not an OS call.</summary>
        public long NativeCode { get; }

        /// <summary>The export that failed.</summary>
        public string Operation { get; }
    }

    /// <summary>prisc_interop is not available (<see cref="InteropLibrary.Require"/>).</summary>
    public sealed class InteropUnavailableException : Exception
    {
        public InteropUnavailableException(string reason)
            : base($"{InteropLibrary.LibraryFileName} is not available: {reason}")
        {
            Reason = reason;
        }

        /// <summary>Why the library is not there (<see cref="InteropLibrary.UnavailableReason"/>).</summary>
        public string Reason { get; }
    }
}
