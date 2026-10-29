using System;
using System.Diagnostics;
using System.IO;
using Prisc.Interop;

namespace Prisc.Ipc
{
    /// <summary>
    /// Finds every helper Prisc spawns or loads (HELPER_PROTOCOL.md §7.1): the Rust helpers, the
    /// native libraries and Prisc.VideoRender. The probe itself is <see cref="NativeProbe"/>, shared
    /// with the prisc_interop loader. One probe order for all of them, first existing
    /// file wins:
    /// <list type="number">
    /// <item>each directory of <c>PRISC_HELPERS_DIR</c> (a <see cref="Path.PathSeparator"/>-separated
    /// list), then its <c>recorder/</c> subdirectory;</item>
    /// <item><see cref="AppContext.BaseDirectory"/>, then its <c>recorder/</c> subdirectory (the release
    /// layout: capture, scroll driver, ai, VideoRender and the hotkeys library beside Prisc.Ui;
    /// recorder, share_region and vid2gif in <c>recorder/</c>);</item>
    /// <item>the dev walk-up: at each ancestor holding <c>Cargo.toml</c>, the newer of
    /// <c>target/debug</c> and <c>target/release</c> (debug wins a tie). Prisc.VideoRender instead
    /// probes <c>ui/Prisc.VideoRender/bin/{Debug,Release}/net10.0</c> at each ancestor. The walk
    /// carries on past a <c>Cargo.toml</c> with nothing built under it (a crate manifest below the
    /// workspace root).</item>
    /// </list>
    /// A path list is what makes an out-of-tree Prisc.Ui <c>OutDir</c> work, where the walk-up finds
    /// no <c>Cargo.toml</c> and no single directory holds both the Rust helpers and VideoRender:
    /// <c>&lt;repo&gt;/target/debug:&lt;repo&gt;/ui/Prisc.VideoRender/bin/Debug/net10.0</c>
    /// (<c>;</c> on Windows).
    /// </summary>
    public static class HelperLocator
    {
        public const string EnvVarName = EnvVars.HelpersDir;

        /// <summary>Test seam (DenoiseGeneratorTests, MatteGeneratorTests): when set, it is asked first,
        /// by stem. A non-null answer is final (the path if it exists, otherwise "not found"); null
        /// falls through to the normal probe, so a seam for one helper leaves the others alone. Null
        /// in production.</summary>
        internal static volatile Func<string, string> Override;

        /// <summary>The executable for <paramref name="stem"/> (no extension), ready to spawn; null
        /// when it cannot be found.</summary>
        public static string Find(string stem)
        {
            var path = Lookup(stem, Platform.ExeName(stem), library: false);
            return EnsureExecutable(path);
        }

        /// <summary>The native library for <paramref name="stem"/> (<c>prisc_hotkeys</c>); null when it
        /// cannot be found.</summary>
        public static string FindLibrary(string stem) => Lookup(stem, Platform.LibName(stem), library: true);

        private static string Lookup(string stem, string fileName, bool library)
        {
            var seam = Override;
            if (seam != null)
            {
                string overridden;
                try { overridden = seam(stem); }
                catch { overridden = ""; }
                if (overridden != null)
                    return !String.IsNullOrWhiteSpace(overridden) && File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
            }

            return Find(fileName, Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory, library);
        }

        /// <summary>The pure probe behind <see cref="Find(string)"/>, for tests.</summary>
        internal static string Find(string fileName, string envDirs, string baseDirectory) =>
            Find(fileName, envDirs, baseDirectory, library: false);

        /// <summary>The pure probe (<see cref="NativeProbe.Find"/>, the one implementation shared with
        /// the prisc_interop loader). <paramref name="library"/> adds <c>target/&lt;profile&gt;/deps/</c> to
        /// the dev walk-up.</summary>
        internal static string Find(string fileName, string envDirs, string baseDirectory, bool library) =>
            NativeProbe.Find(fileName, envDirs, baseDirectory, library);

        /// <summary>
        /// Makes sure <paramref name="path"/> can actually be executed, and returns it unchanged. No-op
        /// on Windows, which has no execute bit.
        /// <para>
        /// The recorder payload arrives as a zip, and the macOS one has carried mode 644 on its
        /// executables: the archive was built somewhere the bit was already lost, so the helpers
        /// unpacked un-runnable and every recording died at Process.Start with "permission denied".
        /// Restoring it here costs one stat and covers any other archive that loses the bit on the
        /// way to a user's disk.
        /// </para>
        /// <para>
        /// Failure is deliberately swallowed: on a read-only volume, or a file owned by someone else,
        /// there is nothing useful to do here and the spawn that follows reports the real problem.
        /// </para>
        /// </summary>
        internal static string EnsureExecutable(string path)
        {
            // Two separate guards, not one `||`: the platform analyzer recognizes a bare
            // `if (OperatingSystem.IsWindows()) return;` as proof that the Unix-only file-mode APIs
            // below are unreachable there, and folding it into a compound condition costs that.
            if (OperatingSystem.IsWindows())
                return path;
            if (String.IsNullOrEmpty(path))
                return path;

            try
            {
                var mode = File.GetUnixFileMode(path);

                // whoever can read it can run it, matching how the bit would have survived the
                // archive, rather than granting anything the file did not already offer.
                var wanted = mode;
                if ((mode & UnixFileMode.UserRead) != 0) wanted |= UnixFileMode.UserExecute;
                if ((mode & UnixFileMode.GroupRead) != 0) wanted |= UnixFileMode.GroupExecute;
                if ((mode & UnixFileMode.OtherRead) != 0) wanted |= UnixFileMode.OtherExecute;

                if (wanted != mode)
                    File.SetUnixFileMode(path, wanted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not mark '{path}' executable: {ex.Message}");
            }

            return path;
        }
    }
}
