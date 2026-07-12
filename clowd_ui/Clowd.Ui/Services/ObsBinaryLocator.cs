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

        public static string Resolve() =>
            Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory);

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
