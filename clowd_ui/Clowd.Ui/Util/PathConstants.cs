using System;
using System.IO;
using System.Linq;

namespace Clowd
{
    public static class Constants
    {
        public const string ClowdAppName = "Clowd";
        /// <summary>Kept short deliberately. On unix .NET backs named pipes with a domain socket at
        /// <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>, and macOS caps that path at 104 chars against a
        /// ~49 char per-user TMPDIR — a GUID-length name here means the pipe can never be created.</summary>
        public const string ClowdNamedPipe = "Clowd.02c1544e";
        public const string ClowdMutex = "ClowdMutex:02c1544e-7d60-435a-bce8-f61496bdbabe";
        public const string PublishingCompany = "Caelan Sayler";
    }

    public static class PathConstants
    {
        public static string LogData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "logs");
        public static string SessionData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "sessions");

        /// <summary>Per-user cache of generated session previews. Like every other folder here the
        /// getter has a side effect — <see cref="GetClowdFolder(string, string)"/> creates the whole
        /// tree — so it must never be read from the UI or a render thread. The preview engine
        /// resolves it exactly once, lazily, on a background worker.</summary>
        public static string PreviewCache => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "previews");

        public static string SettingsData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");

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
            name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "." + extension.TrimStart('.');

        public static string GetFreePatternFileName(string directory, string pattern)
        {
            var files = Directory.EnumerateFiles(directory).Select(Path.GetFileNameWithoutExtension).ToArray();

            for (int i = 0; i < 100; i++)
            {
                var dateStr = DateTime.Now.ToString(pattern);
                if (i > 0) dateStr += $" ({i})";

                if (files.Any(f => String.Equals(f, dateStr, StringComparison.OrdinalIgnoreCase)))
                    continue;

                return dateStr;
            }

            return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        }

        private static string GetClowdFolder(Environment.SpecialFolder dataDirectory, string dataName) =>
            GetClowdFolder(GetDataDirectory(dataDirectory), dataName);

        /// <summary>
        /// .NET 8 changed <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> on macOS to
        /// return Apple-convention paths (LocalApplicationData → ~/Library/Application Support). The
        /// session directory is shared with the Rust capture side (§2.11), which writes to the XDG data
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

        private static string GetClowdFolder(string dataDirectory, string dataName)
        {
            if (String.IsNullOrWhiteSpace(dataName))
                throw new ArgumentException($"Directory name can not be empty.");

            // unlike the WPF original (which threw), create the parent data directory if missing —
            // on macOS the unix mapping of LocalApplicationData (~/.local/share) may not exist yet.
            if (!Directory.Exists(dataDirectory))
                Directory.CreateDirectory(dataDirectory);

            var clowdPath = Path.Combine(dataDirectory, "Clowd");

            if (!Directory.Exists(clowdPath))
                Directory.CreateDirectory(clowdPath);

            if (String.IsNullOrEmpty(dataName))
                return Path.GetFullPath(clowdPath);

            var dataPath = Path.Combine(clowdPath, dataName);

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
