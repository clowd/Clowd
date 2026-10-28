using System;
using System.Diagnostics;
using System.IO;

namespace Prisc.Ipc
{
    /// <summary>
    /// Finds every helper Prisc spawns or loads (HELPER_PROTOCOL.md §7.1): the Rust helpers, the
    /// prisc_hotkeys library and Prisc.VideoRender. One probe order for all of them, first existing
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
        public const string EnvVarName = "PRISC_HELPERS_DIR";

        /// <summary>The one helper that is a .NET project rather than a cargo target.</summary>
        private const string VideoRenderStem = "Prisc.VideoRender";

        /// <summary>Test seam (DenoiseGeneratorTests): when set, it answers every lookup in place of
        /// the probe, by stem. A path it returns still has to exist. Null in production.</summary>
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
                catch { overridden = null; }
                return !String.IsNullOrWhiteSpace(overridden) && File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
            }

            return Find(fileName, Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory, library);
        }

        /// <summary>The pure probe behind <see cref="Find(string)"/>, for tests.</summary>
        internal static string Find(string fileName, string envDirs, string baseDirectory) =>
            Find(fileName, envDirs, baseDirectory, library: false);

        /// <summary>The pure probe. <paramref name="library"/> adds <c>target/&lt;profile&gt;/deps/</c> to
        /// the dev walk-up: building a crate that depends on prisc_hotkeys compiles the library there
        /// without copying it up, so probing only <c>target/&lt;profile&gt;/</c> would load a stale copy
        /// that then fails the ABI check.</summary>
        internal static string Find(string fileName, string envDirs, string baseDirectory, bool library)
        {
            // 1. the override directories, in order
            if (!String.IsNullOrWhiteSpace(envDirs))
            {
                foreach (var dir in envDirs.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var hit = InDirectory(dir, fileName);
                    if (hit != null)
                        return hit;
                }
            }

            if (String.IsNullOrEmpty(baseDirectory))
                return null;

            // 2. the release layout
            var shipped = InDirectory(baseDirectory, fileName);
            if (shipped != null)
                return shipped;

            // 3. the dev walk-up
            var isVideoRender = fileName == Platform.ExeName(VideoRenderStem);
            DirectoryInfo current;
            try { current = new DirectoryInfo(Path.GetFullPath(baseDirectory)); }
            catch { return null; }

            while (current != null)
            {
                string hit;
                if (isVideoRender)
                {
                    var project = Path.Combine(current.FullName, "ui", VideoRenderStem, "bin");
                    hit = Newer(Path.Combine(project, "Debug", "net10.0", fileName),
                                Path.Combine(project, "Release", "net10.0", fileName));
                }
                else if (File.Exists(Path.Combine(current.FullName, "Cargo.toml")))
                {
                    var target = Path.Combine(current.FullName, "target");
                    hit = Newer(Path.Combine(target, "debug", fileName), Path.Combine(target, "release", fileName));
                    if (library)
                    {
                        hit = Newer(hit, Newer(Path.Combine(target, "debug", "deps", fileName),
                                               Path.Combine(target, "release", "deps", fileName)));
                    }
                }
                else
                {
                    hit = null;
                }

                if (hit != null)
                    return hit;

                current = current.Parent;
            }

            return null;
        }

        /// <summary><paramref name="fileName"/> in <paramref name="dir"/>, then in its <c>recorder/</c>
        /// subdirectory.</summary>
        private static string InDirectory(string dir, string fileName)
        {
            try
            {
                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct))
                    return Path.GetFullPath(direct);

                var recorder = Path.Combine(dir, "recorder", fileName);
                if (File.Exists(recorder))
                    return Path.GetFullPath(recorder);
            }
            catch (ArgumentException)
            {
                // a malformed entry in the path list is skipped, not fatal
            }

            return null;
        }

        /// <summary>Whichever of the two exists, or the more recently written when both do. Strictly
        /// greater, so an exact tie (coarse filesystem timestamps) keeps <paramref name="first"/>,
        /// which is the debug build.</summary>
        private static string Newer(string first, string second)
        {
            var firstBuilt = LastWriteUtcOrNull(first);
            var secondBuilt = LastWriteUtcOrNull(second);
            if (firstBuilt.HasValue && secondBuilt.HasValue)
                return secondBuilt.Value > firstBuilt.Value ? second : first;
            if (firstBuilt.HasValue)
                return first;
            return secondBuilt.HasValue ? second : null;
        }

        /// <summary>Last-write time in UTC, or null when the file is absent or cannot be stat'd. Doubles
        /// as the existence check, so a <c>cargo build</c> or <c>cargo clean</c> running while Prisc is
        /// open makes a candidate unselectable rather than throwing out of the lookup.</summary>
        private static DateTime? LastWriteUtcOrNull(string path)
        {
            if (path == null)
                return null;

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

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
