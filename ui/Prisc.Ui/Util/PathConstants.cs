using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Velopack.Locators;

namespace Prisc
{
    public static class Constants
    {
        public const string PriscAppName = "Prisc";
        /// <summary>Kept short deliberately. On unix .NET backs named pipes with a domain socket at
        /// <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>, and macOS caps that path at 104 chars against a
        /// ~49 char per-user TMPDIR — a GUID-length name here means the pipe can never be created.</summary>
        public const string PriscNamedPipe = "Prisc.02c1544e";
        public const string PriscMutex = "PriscMutex:02c1544e-7d60-435a-bce8-f61496bdbabe";
        public const string PublishingCompany = "Caelan Sayler";
    }

    public static class PathConstants
    {
        public static string LogData => GetPriscFolder("logs");
        public static string SessionData => GetPriscFolder("sessions");

        /// <summary>Per-user cache of generated session previews. Like every other folder here the
        /// getter has a side effect — <see cref="GetPriscFolder(string, string)"/> creates the whole
        /// tree — so it must never be read from the UI or a render thread. The preview engine
        /// resolves it exactly once, lazily, on a background worker.</summary>
        public static string PreviewCache => GetPriscFolder("previews");

        /// <summary>The per-user data directory the app folder lives in: LocalAppData on Windows, the
        /// XDG data dir elsewhere (see <see cref="GetDataDirectory(Environment.SpecialFolder)"/>).
        /// Unlike the folders above it creates nothing.</summary>
        public static string DataDirectory => GetDataDirectory(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>
        /// The parent of <see cref="SessionData"/>, <see cref="PreviewCache"/> and
        /// <see cref="LogData"/>. Creates nothing, so the legacy data migration can run before
        /// anything touches the sessions folder.
        ///
        /// On an installed Windows build this is the Velopack install root itself
        /// (<see cref="VelopackInstallRoot"/>), as it always was: a fresh install's root is
        /// <c>%LOCALAPPDATA%\Prisc</c>, and an install upgraded across the rename keeps its root,
        /// and so its data, under the old name. Data must never live in a folder that is not this
        /// install's root but that Velopack Setup would claim as its default root (Setup renames
        /// and then deletes a non-empty target), and uninstall removes the data with the root.
        /// Everywhere else (dev builds, macOS, Linux) it is <c>&lt;DataDirectory&gt;/Prisc</c>.
        /// </summary>
        public static string AppDataRoot
        {
            get
            {
                if (OperatingSystem.IsWindows() && VelopackInstallRoot is { } root)
                    return Path.GetFullPath(root);

                return Path.Combine(DataDirectory, "Prisc");
            }
        }

        /// <summary>The Velopack install root of this app (on macOS its <c>.app</c> bundle), or null
        /// for a dev or loose build. Never throws.</summary>
        public static string VelopackInstallRoot
        {
            get
            {
                try
                {
                    if (!VelopackLocator.IsCurrentSet)
                        return null;

                    var locator = VelopackLocator.Current;
                    if (locator == null || locator.CurrentlyInstalledVersion == null || String.IsNullOrEmpty(locator.RootAppDir))
                        return null;

                    return locator.RootAppDir;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("PathConstants: could not read the Velopack locator: " + ex.Message);
                    return null;
                }
            }
        }

        public static string GetFolderPath(string name, string parentDirectory)
        {
            var d = Path.Combine(Path.GetFullPath(parentDirectory), name);
            return d;
        }

        public static string GetFilePath(string name, string extension, string directory) =>
            Path.Combine(Path.GetFullPath(directory), name + "." + extension.TrimStart('.'));

        public static string GetDatedFilePath(string name, string extension, string directory) =>
            Path.Combine(Path.GetFullPath(directory), GetDatedFileName(name, extension));

        public static string GetDatedFileName(string name, string extension) =>
            name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "." + extension.TrimStart('.');

        /// <summary>The pattern rendered for now, with a " (n)" suffix past any name already in the
        /// directory. Always the invariant culture: the overlay renders the same setting in Rust
        /// (<c>crates/prisc_capture/src/filename_pattern.rs</c>: Gregorian, literal separators, English
        /// names) and both routes must name one capture the same way, which the user's culture
        /// would break — th-TH writes the year 2569, de-DE turns "/" into ".".</summary>
        public static string GetFreePatternFileName(string directory, string pattern)
        {
            var files = Directory.EnumerateFiles(directory).Select(Path.GetFileNameWithoutExtension).ToArray();

            for (int i = 0; i < 100; i++)
            {
                var dateStr = DateTime.Now.ToString(pattern, CultureInfo.InvariantCulture);
                if (i > 0) dateStr += $" ({i})";

                if (files.Any(f => String.Equals(f, dateStr, StringComparison.OrdinalIgnoreCase)))
                    continue;

                return dateStr;
            }

            return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// .NET 8 changed <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> on macOS to
        /// return Apple-convention paths (LocalApplicationData → ~/Library/Application Support). The
        /// session directory is shared with the Rust capture side, which writes to the XDG data
        /// dir, so on non-Windows platforms we keep the XDG mapping ($XDG_DATA_HOME or ~/.local/share).
        /// </summary>
        private static string GetDataDirectory(Environment.SpecialFolder folder)
        {
            if (!OperatingSystem.IsWindows() && folder == Environment.SpecialFolder.LocalApplicationData)
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (!String.IsNullOrWhiteSpace(xdg))
                    return xdg;

                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Environment.GetFolderPath(folder);
        }

        private static string GetPriscFolder(string dataName)
        {
            if (String.IsNullOrWhiteSpace(dataName))
                throw new ArgumentException($"Directory name can not be empty.");

            // unlike the WPF original (which threw), create the whole tree if missing — on macOS
            // the unix mapping of LocalApplicationData (~/.local/share) may not exist yet.
            var priscPath = AppDataRoot;

            if (!Directory.Exists(priscPath))
                Directory.CreateDirectory(priscPath);

            var dataPath = Path.Combine(priscPath, dataName);

            if (!Directory.Exists(dataPath))
                Directory.CreateDirectory(dataPath);

            return Path.GetFullPath(dataPath);
        }

        public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
        {
            try
            {
                using FileStream fs = File.Create(Path.Combine(dirPath, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose);
                return true;
            }
            catch
            {
                if (throwIfFails)
                    throw;
                else
                    return false;
            }
        }
    }
}
