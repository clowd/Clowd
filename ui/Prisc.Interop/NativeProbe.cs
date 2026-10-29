using System;
using System.IO;

namespace Prisc.Interop
{
    /// <summary>
    /// The one probe for everything Prisc spawns or loads from disk (HELPER_PROTOCOL.md section 7.1):
    /// the Rust helpers, the prisc_interop library and Prisc.VideoRender. First existing file wins:
    /// <list type="number">
    /// <item>each directory of <c>PRISC_HELPERS_DIR</c> (a <see cref="Path.PathSeparator"/>-separated
    /// list), then its <c>recorder/</c> subdirectory;</item>
    /// <item>the base directory, then its <c>recorder/</c> subdirectory (the release layout);</item>
    /// <item>the dev walk-up: at each ancestor holding <c>Cargo.toml</c>, the newer of
    /// <c>target/debug</c> and <c>target/release</c> (debug wins a tie), plus their <c>deps/</c> for a
    /// library. Prisc.VideoRender instead probes <c>ui/Prisc.VideoRender/bin/{Debug,Release}/net10.0</c>
    /// at each ancestor. The walk carries on past a <c>Cargo.toml</c> with nothing built under it.</item>
    /// </list>
    /// Prisc.Ipc's <c>HelperLocator</c> is the facade most callers use; it delegates here.
    /// </summary>
    public static class NativeProbe
    {
        /// <summary>The one helper that is a .NET project rather than a cargo target.</summary>
        private const string VideoRenderStem = "Prisc.VideoRender";

        /// <summary>The executable for <paramref name="stem"/>: <c>stem.exe</c> on Windows, the bare
        /// stem elsewhere.</summary>
        public static string ExeName(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

        /// <summary>The native library for <paramref name="stem"/>: <c>stem.dll</c>,
        /// <c>libstem.dylib</c> or <c>libstem.so</c>.</summary>
        public static string LibName(string stem) =>
            OperatingSystem.IsWindows() ? stem + ".dll"
            : OperatingSystem.IsMacOS() ? "lib" + stem + ".dylib"
            : "lib" + stem + ".so";

        /// <summary>The probe. <paramref name="library"/> adds <c>target/&lt;profile&gt;/deps/</c> to the
        /// dev walk-up: a crate built as a dependency (or by <c>cargo test</c>) leaves its library there
        /// without copying it up, and probing only <c>target/&lt;profile&gt;/</c> would load a stale copy.
        /// Null when nothing is found.</summary>
        public static string? Find(string fileName, string? envDirs, string? baseDirectory, bool library)
        {
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

            var shipped = InDirectory(baseDirectory, fileName);
            if (shipped != null)
                return shipped;

            var isVideoRender = fileName == ExeName(VideoRenderStem);
            DirectoryInfo? current;
            try { current = new DirectoryInfo(Path.GetFullPath(baseDirectory)); }
            catch { return null; }

            while (current != null)
            {
                string? hit;
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
        private static string? InDirectory(string dir, string fileName)
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
        /// greater, so an exact tie (coarse filesystem timestamps) keeps <paramref name="first"/>.</summary>
        private static string? Newer(string? first, string? second)
        {
            var firstBuilt = LastWriteUtcOrNull(first);
            var secondBuilt = LastWriteUtcOrNull(second);
            if (firstBuilt.HasValue && secondBuilt.HasValue)
                return secondBuilt.Value > firstBuilt.Value ? second : first;
            if (firstBuilt.HasValue)
                return first;
            return secondBuilt.HasValue ? second : null;
        }

        /// <summary>Last-write time in UTC, or null when the file is absent or cannot be stat'd; a
        /// <c>cargo build</c> running meanwhile makes a candidate unselectable rather than throwing.</summary>
        private static DateTime? LastWriteUtcOrNull(string? path)
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
    }
}
