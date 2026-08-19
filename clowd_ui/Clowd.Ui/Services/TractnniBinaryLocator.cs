using System;
using System.IO;

namespace Clowd.UI
{
    /// <summary>
    /// Locates the external <c>clowd_tractnni</c> AI inference binary (person mattes and audio
    /// denoise — see <c>Clowd.VideoSDK.Ai</c>). Probe order: the <c>CLOWD_TRACTNNI_PATH</c>
    /// environment variable, then alongside the Clowd.Ui executable (release layout), then walking
    /// up from the app base directory to a cargo workspace root and probing <c>target/debug</c>
    /// followed by <c>target/release</c> (debug-time layout) — the same order
    /// <see cref="CaptureBinaryLocator"/> uses for the capture overlay. The app installs this as
    /// <c>TractnniLoader</c>'s resolver at startup; the loader re-resolves on every generation
    /// run, so a binary that appears mid-session is picked up.
    /// </summary>
    public static class TractnniBinaryLocator
    {
        public const string EnvVarName = "CLOWD_TRACTNNI_PATH";

        public static string BinaryFileName =>
            OperatingSystem.IsWindows() ? "clowd_tractnni.exe" : "clowd_tractnni";

        public static string Resolve() =>
            Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory);

        /// <summary>Testable overload. Returns null when the binary cannot be found.</summary>
        public static string Resolve(string envVarValue, string baseDirectory)
        {
            // (a) explicit override via environment variable
            if (!String.IsNullOrWhiteSpace(envVarValue) && File.Exists(envVarValue))
                return Path.GetFullPath(envVarValue);

            // (b) next to the Clowd.Ui executable (release layout copies the binary alongside)
            var local = Path.Combine(baseDirectory, BinaryFileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // (c) walk up to a directory containing Cargo.toml (the cargo workspace root) and
            // probe target/debug and target/release. Keep walking if a Cargo.toml has no
            // built binary (a crate manifest may sit below the workspace root that owns
            // target/). The ONNX Runtime is statically linked into the binary, so any built
            // exe is usable as-is.
            var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
                {
                    foreach (var profile in new[] { "debug", "release" })
                    {
                        var candidate = Path.Combine(dir.FullName, "target", profile, BinaryFileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
