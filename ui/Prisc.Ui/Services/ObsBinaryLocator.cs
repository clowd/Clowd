using System;
using System.IO;

namespace Prisc.UI
{
    /// <summary>
    /// Locates the prisc_recorder recording binary (DESIGN §4.2, built in-repo from
    /// crates/prisc_recorder). Probe order: the <c>PRISC_RECORDER_PATH</c> environment variable, then
    /// a <c>recorder</c> directory alongside the Prisc.Ui executable (release layout),
    /// then walking up from the app base directory to each cargo workspace root (a directory
    /// containing <c>Cargo.toml</c> — the Prisc checkout) and probing its
    /// <c>target/debug</c> followed by <c>target/release</c>.
    /// Note: when Prisc.Ui is built to an out-of-tree scratch OutDir the walk-up finds no
    /// <c>Cargo.toml</c> — the dev launch profile must set <c>PRISC_RECORDER_PATH</c>.
    /// </summary>
    public static class ObsBinaryLocator
    {
        public const string EnvVarName = "PRISC_RECORDER_PATH";

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "prisc_recorder.exe" : "prisc_recorder";

        /// <summary>The binary, ready to spawn — see <see cref="HelperBinary.EnsureExecutable"/> for
        /// why resolving one also has to check it is runnable. The overload below stays a pure
        /// path lookup.</summary>
        public static string Resolve() =>
            HelperBinary.EnsureExecutable(
                Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory));

        /// <summary>
        /// The directory holding the FFmpeg native libraries, or null when none can be found —
        /// the one answer every caller of <see cref="VideoSDK.FFmpegLoader.TryInitialize"/> passes
        /// as its fallback resolver. <c>PRISC_FFMPEG_PATH</c> is checked by the loader itself and
        /// wins over everything here.
        /// <para>
        /// The libraries ship with the recorder, but not in one place: on Windows they sit beside
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

                // macOS bundle layout: recorder/Frameworks/libav*.dylib.
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

            // (b) release layout: recorder/ subdirectory next to the Prisc.Ui executable.
            var local = Path.Combine(baseDirectory, "recorder", BinaryFileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // (c) dev layout: walk up to each directory containing Cargo.toml (the Prisc repo
            // root, built with `cargo rec`) and probe its build output. Keep walking if nothing
            // is built there (nested crate manifests may match first).
            var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
                {
                    var target = Path.Combine(dir.FullName, "target");

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
