using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Clowd.UI
{
    /// <summary>
    /// P/Invoke surface of the native <c>clowd_hotkeys</c> library (the crate's C ABI, see
    /// <c>clowd_hotkeys/src/ffi.rs</c>). The return codes, event kinds and the event struct are
    /// generated from the crate (<see cref="HotkeysAbi"/>, <see cref="HotkeyEventNative"/>); only
    /// these signatures are written by hand, and the crate's ABI revision — folded into
    /// <see cref="HotkeyKeys.AbiVersion"/> — guards them. Call <see cref="EnsureLoaded"/> before
    /// anything else. Used only by <see cref="NativeHotkeyHost"/>.
    /// </summary>
    internal static class HotkeysNative
    {
        private const string Library = "clowd_hotkeys";

        private static readonly object Gate = new();
        private static bool s_attempted;
        private static string s_error;
        private static IntPtr s_handle;

        /// <summary>
        /// Loads the library (once; later calls return the first answer) and checks it was built
        /// from the same key table and ABI as this app. Null on success, otherwise why not.
        /// </summary>
        public static string EnsureLoaded()
        {
            lock (Gate)
            {
                if (!s_attempted)
                {
                    s_attempted = true;
                    // fail closed: a throw anywhere in Load (a second resolver, a DLL that lacks the
                    // ABI probe) must latch an error, never leave the half-loaded binding "loaded"
                    try
                    {
                        s_error = Load();
                    }
                    catch (Exception ex)
                    {
                        s_error = "the clowd_hotkeys library could not be loaded: " + ex.Message;
                    }
                }

                return s_error;
            }
        }

        private static string Load()
        {
            // nothing builds or ships the library for the UI on Linux yet
            if (OperatingSystem.IsLinux())
                return "Global hotkeys are not supported on Linux yet.";

            var path = HotkeysLibraryLocator.Resolve();
            if (path == null)
                return "the clowd_hotkeys library was not found";

            s_handle = NativeLibrary.Load(path);

            // only our own library name: user32, CoreGraphics and the rest resolve as usual
            NativeLibrary.SetDllImportResolver(typeof(HotkeysNative).Assembly, Resolve);

            if (clowd_hotkeys_abi_version() != HotkeyKeys.AbiVersion)
                return "the clowd_hotkeys library is out of date (rebuild it: cargo build -p clowd_hotkeys)";

            return null;
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
            libraryName == Library ? s_handle : IntPtr.Zero;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint clowd_hotkeys_abi_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int clowd_hotkeys_start(out IntPtr handle, [Out] byte[] errBuf, uint errCap);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int clowd_hotkeys_register(IntPtr handle, int key, uint modifiers, out uint id);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void clowd_hotkeys_unregister(IntPtr handle, uint id);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void clowd_hotkeys_set_observe(IntPtr handle, uint observe);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int clowd_hotkeys_start_recording(IntPtr handle, out uint session);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void clowd_hotkeys_stop_recording(IntPtr handle, uint session);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int clowd_hotkeys_next_event(IntPtr handle, out HotkeyEventNative ev, uint timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint clowd_hotkeys_stop_reason(IntPtr handle, [Out] byte[] buf, uint cap);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void clowd_hotkeys_stop(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void clowd_hotkeys_free(IntPtr handle);
    }
}
