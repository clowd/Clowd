using System;
using System.IO;
using System.Linq;

namespace Clowd.UI
{
    /// <summary>
    /// Locates the native <c>clowd_hotkeys</c> library behind the global hotkeys
    /// (<see cref="HotkeysNative"/>). Probe order: the <c>CLOWD_HOTKEYS_PATH</c> environment
    /// variable, then alongside the Clowd.Ui executable (release layout), then walking up from the
    /// app base directory to a cargo workspace root and taking the newest of
    /// <c>target/{debug,release}</c> and their <c>deps/</c> folders (debug-time layout) — the
    /// shape <see cref="AiBinaryLocator"/> uses.
    /// </summary>
    /// <remarks>
    /// <c>deps/</c> matters: building a crate that depends on clowd_hotkeys (<c>cargo build -p
    /// clowd_capture</c>, <c>cargo r</c>) compiles the library into <c>target/&lt;profile&gt;/deps/</c>
    /// without copying it up to <c>target/&lt;profile&gt;/</c>, so probing only the latter would
    /// load a stale copy and fail the ABI check. Newest wins for the same reason.
    /// <para>Dev loop: a running Clowd.Ui holds the library open, so relinking clowd_hotkeys
    /// fails with LNK1104 on Windows. Close Clowd.Ui and the standby capturer, build, then
    /// relaunch.</para>
    /// </remarks>
    public static class HotkeysLibraryLocator
    {
        public const string EnvVarName = "CLOWD_HOTKEYS_PATH";

        public static string FileName =>
            OperatingSystem.IsWindows() ? "clowd_hotkeys.dll"
            : OperatingSystem.IsMacOS() ? "libclowd_hotkeys.dylib"
            : "libclowd_hotkeys.so";

        public static string Resolve() =>
            Resolve(Environment.GetEnvironmentVariable(EnvVarName), AppContext.BaseDirectory);

        /// <summary>Testable overload. Returns null when the library cannot be found.</summary>
        public static string Resolve(string envVarValue, string baseDirectory)
        {
            // (a) explicit override via environment variable
            if (!String.IsNullOrWhiteSpace(envVarValue) && File.Exists(envVarValue))
                return Path.GetFullPath(envVarValue);

            // (b) next to the Clowd.Ui executable (release layout copies the library alongside)
            var local = Path.Combine(baseDirectory, FileName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // (c) walk up to a directory containing Cargo.toml (the cargo workspace root), and keep
            // walking past a crate manifest that has no built library under it
            var dir = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
                {
                    var newest = new[] { "debug", "release" }
                        .SelectMany(profile => new[]
                        {
                            Path.Combine(dir.FullName, "target", profile, FileName),
                            Path.Combine(dir.FullName, "target", profile, "deps", FileName),
                        })
                        .Select(path => new FileInfo(path))
                        .Where(file => file.Exists)
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest != null)
                        return newest.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
