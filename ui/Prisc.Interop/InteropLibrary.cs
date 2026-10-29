using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>
    /// Finds, checks and loads <c>prisc_interop</c>, and reports whether it is there.
    /// </summary>
    /// <remarks>
    /// <para>The load is lazy, happens once behind a lock, never throws, and latches the handle, the
    /// path and the reason. Resolve order, the same on every OS: (1) <c>PRISC_INTEROP_PATH</c>,
    /// authoritative (set but not a file means unavailable, no fallback); (2) <see cref="NativeProbe"/>
    /// over <c>PRISC_HELPERS_DIR</c>, the application directory and the dev walk-up.</para>
    /// <para>Before any DllImport resolves, the loader calls <c>prisc_interop_abi_hash</c> through
    /// <see cref="NativeLibrary.TryGetExport"/> and refuses (frees) a library whose fingerprint differs
    /// from the generated <see cref="InteropAbi.Hash"/>.</para>
    /// <para>The library is a hard requirement of Prisc.Ui and Prisc.VideoRender: they call
    /// <see cref="Require"/> at start and stop with one message naming <see cref="UnavailableReason"/>.</para>
    /// </remarks>
    public static class InteropLibrary
    {
        /// <summary>The name the generated bindings import and the resolver answers for.</summary>
        internal const string DllName = "prisc_interop";

        private static readonly object Gate = new();
        private static LoadState? _state;

        /// <summary>The platform spelling of the library file.</summary>
        public static string LibraryFileName => NativeProbe.LibName(DllName);

        /// <summary>Whether the library is loaded and speaks the ABI these bindings were generated for.
        /// Forces the one-time probe and load.</summary>
        public static bool IsAvailable => Current.Handle != IntPtr.Zero;

        /// <summary>Why the library is not available; empty when it is.</summary>
        public static string UnavailableReason => Current.Reason;

        /// <summary>The ABI fingerprint these bindings were generated for (the generated
        /// <c>InteropAbi.Hash</c>); a loaded library reported the same.</summary>
        public static ulong AbiHash => InteropAbi.Hash;

        /// <summary>The absolute path the library was loaded from, or null.</summary>
        public static string? LoadedPath => Current.Path;

        /// <summary>The library's own version (<c>PRISC_VERSION</c> at build time), or null when it is
        /// not available.</summary>
        public static unsafe string? Version
        {
            get
            {
                if (!IsAvailable)
                    return null;

                PriscString text = default;
                if (NativeMethods.prisc_interop_version(&text) != 0)
                    return null;
                return Utf8.Take(ref text);
            }
        }

        /// <summary>Forces the probe and the load now. Prisc.Ui calls this early so the first real
        /// caller (the foreground hand-off right after a helper starts) does not pay for it.</summary>
        public static void Warm() => _ = IsAvailable;

        /// <summary>Throws <see cref="InteropUnavailableException"/> naming the reason when the
        /// library is not available.</summary>
        public static void Require() => Current.Require();

        [ModuleInitializer]
        internal static void Install()
        {
            // Bookkeeping only: no file system, no dlopen, so a module initializer can never be the
            // thing that fails an assembly load.
            NativeLibrary.SetDllImportResolver(typeof(InteropLibrary).Assembly, ResolveImport);
        }

        private static LoadState Current
        {
            get
            {
                var state = Volatile.Read(ref _state);
                if (state != null)
                    return state;

                lock (Gate)
                {
                    if (_state == null)
                        Volatile.Write(ref _state, Load(Environment.GetEnvironmentVariable(EnvVars.InteropPath),
                            Environment.GetEnvironmentVariable(EnvVars.HelpersDir), AppContext.BaseDirectory, InteropAbi.Hash));
                    return _state!;
                }
            }
        }

        /// <summary>Where the library would be loaded from: the path, or why there is none. Pure.</summary>
        internal static (string? Path, string? Failure) ResolvePath(string? interopPath, string? helpersDirs, string? baseDirectory)
        {
            if (!String.IsNullOrWhiteSpace(interopPath))
            {
                return File.Exists(interopPath)
                    ? (System.IO.Path.GetFullPath(interopPath), null)
                    : (null, $"{EnvVars.InteropPath} is set to \"{interopPath}\", which is not a file.");
            }

            var found = NativeProbe.Find(LibraryFileName, helpersDirs, baseDirectory, library: true);
            return found != null
                ? (found, null)
                : (null, $"{LibraryFileName} was not found in {EnvVars.HelpersDir}, beside the application or in a cargo target "
                         + "directory above it (build it with: cargo build -p prisc_interop).");
        }

        /// <summary>Resolves, loads and checks the library, never throwing. A rejected library is freed.</summary>
        internal static unsafe LoadState Load(string? interopPath, string? helpersDirs, string? baseDirectory, ulong expectedHash)
        {
            try
            {
                var (path, failure) = ResolvePath(interopPath, helpersDirs, baseDirectory);
                if (path == null)
                    return LoadState.Missing(failure ?? "the library could not be located.");

                // TryLoad on an absolute path, never a P/Invoke: a P/Invoke here would re-enter the
                // resolver while the state is still null.
                if (!NativeLibrary.TryLoad(path, out var handle))
                    return LoadState.Missing($"{path} could not be loaded.");

                if (!NativeLibrary.TryGetExport(handle, "prisc_interop_abi_hash", out var export))
                    return Reject(handle, $"{path} exports no prisc_interop_abi_hash, so it is not prisc_interop.");

                ulong hash = 0;
                int status = ((delegate* unmanaged[Cdecl]<ulong*, int>)export)(&hash);
                if (status != 0)
                    return Reject(handle, $"{path}: prisc_interop_abi_hash returned {status}.");

                if (hash != expectedHash)
                {
                    return Reject(handle,
                        $"stale prisc_interop at {path}: the bindings and the library disagree (run cargo codegen, then cargo build -p prisc_interop). "
                        + $"Library ABI 0x{hash:X16}, bindings 0x{expectedHash:X16}.");
                }

                return new LoadState(handle, path, String.Empty);
            }
            catch (Exception ex)
            {
                return LoadState.Missing($"loading it threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static LoadState Reject(IntPtr handle, string reason)
        {
            NativeLibrary.Free(handle);
            return LoadState.Missing(reason);
        }

        private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != DllName)
                return IntPtr.Zero;

            var state = Current;
            if (state.Handle != IntPtr.Zero)
                return state.Handle;

            // Returning IntPtr.Zero would hand the decision back to the CLR, whose own probing would
            // happily load the very library just refused. Throwing makes the decision stick.
            throw new DllNotFoundException($"{LibraryFileName} is not available. {state.Reason}");
        }

        /// <summary>One load outcome: a handle and its path, or the reason there is none.</summary>
        internal sealed class LoadState
        {
            internal LoadState(IntPtr handle, string? path, string reason)
            {
                Handle = handle;
                Path = path;
                Reason = reason;
            }

            internal IntPtr Handle { get; }

            internal string? Path { get; }

            internal string Reason { get; }

            internal static LoadState Missing(string reason) => new(IntPtr.Zero, null, reason);

            internal void Require()
            {
                if (Handle == IntPtr.Zero)
                    throw new InteropUnavailableException(Reason);
            }
        }
    }
}
