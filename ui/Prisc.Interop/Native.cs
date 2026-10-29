using System;

namespace Prisc.Interop
{
    /// <summary>Status handling shared by every wrapper in this assembly.</summary>
    internal static class Native
    {
        /// <summary>Returns the status when it is not a failure (zero or positive: OK, None, Timeout,
        /// Closed), so a caller can still tell them apart; throws <see cref="InteropException"/> with
        /// this thread's last error for a negative one.</summary>
        internal static InteropStatus Check(int status, string operation)
            => status >= 0 ? (InteropStatus)status : throw Failure(status, operation);

        /// <summary>The exception a negative status becomes, with this thread's last-error message and
        /// OS code (read at once, on the calling thread, before anything else can overwrite them).</summary>
        internal static InteropException Failure(int status, string operation)
        {
            long code = 0;
            string message = String.Empty;
            if (InteropLibrary.IsAvailable)
            {
                code = NativeMethods.prisc_last_error_code();
                message = LastErrorMessage();
            }
            return new InteropException((InteropStatus)status, code, operation, message);
        }

        /// <summary>This thread's last error text (size, then read: the native side writes nothing
        /// unless the whole message fits); empty when there is none.</summary>
        internal static unsafe string LastErrorMessage()
        {
            nuint length = NativeMethods.prisc_last_error_message(null, 0);
            if (length == 0 || length > Int32.MaxValue)
                return String.Empty;

            var buffer = new byte[(int)length];
            fixed (byte* p = buffer)
            {
                if (NativeMethods.prisc_last_error_message(p, length) != length)
                    return String.Empty;
            }

            return System.Text.Encoding.UTF8.GetString(buffer);
        }
    }
}
