using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Screeney
{
    public static class Interop
    {
        [DllImport("msvcrt.dll", EntryPoint = "memcpy",
            CallingConvention = CallingConvention.Cdecl,
            SetLastError = false)]
        public static extern IntPtr memcpy(IntPtr dest, IntPtr src, UIntPtr count);
    }
}
