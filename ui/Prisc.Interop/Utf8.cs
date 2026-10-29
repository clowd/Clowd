using System;
using System.Text;

namespace Prisc.Interop
{
    /// <summary>UTF-8 across the boundary, both ways (README B5, B6).</summary>
    /// <remarks>Strings in are a pointer and a byte length, never NUL-terminated (<see cref="Bytes"/>,
    /// pinned with <c>fixed</c>). Strings out are a <c>PriscString</c>: <see cref="Take"/> copies and
    /// frees a mode 1 (standalone owned) one; <see cref="Read"/> copies a mode 2 or mode 3 one, which
    /// its owner frees.</remarks>
    internal static unsafe class Utf8
    {
        /// <summary>A string on its way in, as UTF-8 bytes with no terminator.</summary>
        internal static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

        /// <summary>Copies a standalone owned string (mode 1) and frees it; the native free zeroes
        /// the struct, so a second call reads the empty string.</summary>
        internal static string Take(ref PriscString s)
        {
            string value = Read(s);
            fixed (PriscString* p = &s)
            {
                NativeMethods.prisc_string_free(p);
            }
            return value;
        }

        /// <summary>Copies a string the native side still owns (mode 2 or 3).</summary>
        internal static string Read(in PriscString s) => Read(s.ptr, s.len);

        /// <summary>Copies <paramref name="length"/> bytes of UTF-8; empty for null or zero.</summary>
        internal static string Read(byte* buffer, nuint length)
        {
            if (buffer == null || length == 0 || length > Int32.MaxValue)
                return String.Empty;

            return Encoding.UTF8.GetString(buffer, (int)length);
        }
    }
}
