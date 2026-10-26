using System;
using System.IO;

namespace Clowd.UI
{
    /// <summary>
    /// Locates the external obs-express recording binary (DESIGN §4.2, sibling repository
    /// obs-express-rs). Probe order: the <c>CLOWD_OBS_EXPRESS_PATH</c> environment variable, then
    /// an <c>obs-express</c> directory alongside the Clowd.Ui executable (future release layout),
    /// then walking up from the app base directory to each cargo workspace root (a directory
    /// containing <c>Cargo.toml</c> — the clowd-rust checkout) and probing the *sibling*
    /// <c>obs-express-rs</c> checkout's <c>target/debug</c> followed by <c>target/release</c>.
    /// Note: when Clowd.Ui is built to an out-of-tree scratch OutDir the walk-up finds no
    /// <c>Cargo.toml</c> — the dev launch profile must set <c>CLOWD_OBS_EXPRESS_PATH</c>.
    /// </summary>
    public static class ObsBinaryLocator
    {
        public const string EnvVarName = "CLOWD_OBS_EXPRESS_PATH";

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "obs-express.exe" : "obs-express";

        /// <summary>The binary, ready to spawn — see <see cref="HelperBinary.EnsureExecutable"/> for
        /// why resolving one also has to check it is runnable. The overload below stays a pure
        /// path lookup.</summary>
        public static string Resolve() =>
            HelperBinary.EnsureExecutable(
                Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory));

        /// <summary>
        /// The directory holding the FFmpeg native libraries, or null when none can be found —
        /// the one answer every caller of <see cref="VideoSDK.FFmpegLoader.TryInitialize"/> passes
        /// as its fallback resolver. <c>CLOWD_FFMPEG_PATH</c> is checked by the loader itself and
        /// wins over everything here.
        /// <para>
        /// The libraries ship with obs-express, but not in one place: on Windows they sit beside
        /// the binary, and in a macOS bundle they are in a <c>Frameworks/</c> subdirectory next to
        /// it. Both are probed, in that order, and each is confirmed to actually hold an avcodec
        /// of the major the bindings were generated against before it is returned — a directory
        /// that merely exists has repeatedly been the thing that turned a missing dependency into
        /// an unrelated failure much further along.
        /// </para>
        /// </summary>
        public static string ResolveFFmpegDirectory()
        {
            var obs = Resolve();
            if (!String.IsNullOrEmpty(obs))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(obs));
                if (HasFFmpeg(dir))
                    return dir;

                // macOS bundle layout: obs-express/Frameworks/libav*.dylib.
                var frameworks = Path.Combine(dir ?? "", "Frameworks");
                if (HasFFmpeg(frameworks))
                    return frameworks;

                // nothing recognizable, but the obs directory is still the best guess we have and
                // the loader's own error names it — better than reporting "not found" for a build
                // that is merely laid out in some way we do not know about.
                return dir;
            }

            return null;
        }

        /// <summary>True when <paramref name="directory"/> holds an avcodec of the major the
        /// bindings were generated against. The name is built from the binding constant rather
        /// than written out, so bumping FFmpeg.AutoGen cannot leave a stale literal behind.
        /// </summary>
        private static bool HasFFmpeg(string directory)
        {
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return false;

            int major = FFmpeg.AutoGen.Abstractions.ffmpeg.LIBAVCODEC_VERSION_MAJOR;
            string name = OperatingSystem.IsWindows() ? $"avcodec-{major}.dll"
                : OperatingSystem.IsMacOS() ? $"libavcodec.{major}.dylib"
                : $"libavcodec.so.{major}";

            return File.Exists(Path.Combine(directory, name));
        }

        /// <summary>Testable overload. Returns null when the binary cannot be found.</summary>
        public static string Resolve(string envVarValue, string baseDirectory)
        {
            // (a) explicit override via environment variable
            if (!String.IsNullOrWhiteSpace(envVarValue) && File.Exists(envVarValue))
                return Path.GetFullPath(envVarValue);

            // (b) release layout: obs-express/ subdirectory next to the Clowd.Ui executable
            // (harmless probe until packaging exists).
            var local = Path.Combine(baseDirectory, "obs-express", BinaryFileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // (c) dev layout: walk up to each directory containing Cargo.toml (the clowd-rust
            // repo root) and probe the sibling obs-express-rs checkout's build output. Keep
            // walking if nothing is built there (nested crate manifests may match first).
            var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            while (dir != null)
            {
                if (dir.Parent != null && File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
                {
                    var target = Path.Combine(dir.Parent.FullName, "obs-express-rs", "target");

                    var debug = Path.Combine(target, "debug", BinaryFileName);
                    if (File.Exists(debug))
                        return debug;

                    var release = Path.Combine(target, "release", BinaryFileName);
                    if (File.Exists(release))
                        return release;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
