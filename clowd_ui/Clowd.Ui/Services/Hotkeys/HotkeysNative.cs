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
    /// anything else. Used only by <see cref="NativeHotkeyHost"/>, through <see cref="Binding"/>.
    /// </summary>
    internal static class HotkeysNative
    {
        private const string Library = "clowd_hotkeys";

        /// <summary>This library as <see cref="INativeHotkeys"/>.</summary>
        public static INativeHotkeys Binding { get; } = new LibraryBinding();

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

        private sealed class LibraryBinding : INativeHotkeys
        {
            public string EnsureLoaded() => HotkeysNative.EnsureLoaded();
            public int Start(out IntPtr handle, byte[] errBuf) => clowd_hotkeys_start(out handle, errBuf, (uint)errBuf.Length);
            public int Register(IntPtr handle, int key, uint modifiers, out uint id) => clowd_hotkeys_register(handle, key, modifiers, out id);
            public void Unregister(IntPtr handle, uint id) => clowd_hotkeys_unregister(handle, id);
            public void SetObserve(IntPtr handle, bool observe) => clowd_hotkeys_set_observe(handle, observe ? 1u : 0u);
            public int StartRecording(IntPtr handle, out uint session) => clowd_hotkeys_start_recording(handle, out session);
            public void StopRecording(IntPtr handle, uint session) => clowd_hotkeys_stop_recording(handle, session);
            public int NextEvent(IntPtr handle, out HotkeyEventNative ev, uint timeoutMs) => clowd_hotkeys_next_event(handle, out ev, timeoutMs);
            public int StopReason(IntPtr handle, byte[] buf) => (int)clowd_hotkeys_stop_reason(handle, buf, (uint)buf.Length);
            public void Stop(IntPtr handle) => clowd_hotkeys_stop(handle);
            public void Free(IntPtr handle) => clowd_hotkeys_free(handle);
        }
    }

    /// <summary>
    /// The clowd_hotkeys C ABI as <see cref="NativeHotkeyHost"/> calls it, one member per
    /// function with the same contract (see <c>ffi.rs</c>): <see cref="HotkeysNative.Binding"/>
    /// in the app, a fake in the tests, so the host's lifecycle — a hook that cannot start, dies,
    /// or comes back — is testable without installing one.
    /// </summary>
    internal interface INativeHotkeys
    {
        /// <summary>See <see cref="HotkeysNative.EnsureLoaded"/>; null on success.</summary>
        string EnsureLoaded();
        int Start(out IntPtr handle, byte[] errBuf);
        int Register(IntPtr handle, int key, uint modifiers, out uint id);
        void Unregister(IntPtr handle, uint id);
        void SetObserve(IntPtr handle, bool observe);
        int StartRecording(IntPtr handle, out uint session);
        void StopRecording(IntPtr handle, uint session);
        int NextEvent(IntPtr handle, out HotkeyEventNative ev, uint timeoutMs);
        /// <summary>The Stopped reason's length in <paramref name="buf"/>, 0 when there is none.</summary>
        int StopReason(IntPtr handle, byte[] buf);
        void Stop(IntPtr handle);
        void Free(IntPtr handle);
    }
}
