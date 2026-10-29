using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>The macOS Services provider (<c>prisc_services_register_provider_macos</c>), behind
    /// Finder's "Upload with Prisc".</summary>
    /// <remarks>The registration lasts for the process: AppKit holds the provider unretained and no
    /// caller ever withdraws it, so there is nothing to dispose. The callback is therefore
    /// process-global, like <see cref="InteropLog"/>'s sink, and needs no GCHandle.</remarks>
    public static unsafe class MacServices
    {
        private static Action<string[]>? _onFiles;

        /// <summary>
        /// Registers the provider under <paramref name="portName"/> (the bundle's <c>NSServices</c>
        /// <c>NSPortName</c>) and refreshes the system's services cache. <paramref name="onFiles"/>
        /// runs on the main thread, inside AppKit's dispatch, with the paths of each request that
        /// carried at least one file; an exception it throws is swallowed here, so it should report
        /// its own. macOS main thread only, once per process: throws <see cref="InteropException"/>
        /// with <see cref="InteropStatus.State"/> off the main thread or on a second registration,
        /// <see cref="InteropStatus.InvalidArg"/> for an empty port name, and
        /// <see cref="InteropStatus.Unsupported"/> off macOS.
        /// </summary>
        public static void RegisterProvider(string portName, Action<string[]> onFiles)
        {
            ArgumentNullException.ThrowIfNull(portName);
            ArgumentNullException.ThrowIfNull(onFiles);
            InteropLibrary.Require();

            var previous = Interlocked.CompareExchange(ref _onFiles, onFiles, null);
            byte[] name = Utf8.Bytes(portName);
            int status;
            fixed (byte* p = name)
            {
                status = NativeMethods.prisc_services_register_provider_macos(p, (nuint)name.Length, &Receive, null);
            }

            if (status < 0 && previous == null)
                Volatile.Write(ref _onFiles, null);
            Native.Check(status, "prisc_services_register_provider_macos");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Receive(void* user, PriscString* paths, nuint count)
        {
            // Nothing may escape an UnmanagedCallersOnly method: an exception here is a fail-fast.
            try
            {
                var files = new string[(int)count];
                for (int i = 0; i < files.Length; i++)
                    files[i] = Utf8.Read(paths[i]);
                Volatile.Read(ref _onFiles)?.Invoke(files);
            }
            catch
            {
                // The callback reports its own failures; a Services request is never worth a crash.
            }
        }
    }
}
