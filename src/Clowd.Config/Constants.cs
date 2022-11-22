﻿using RT.Util.ExtensionMethods;

namespace Clowd;

public static class Constants
{
    public const string ClowdAppName = "Clowd";
    public const string ClowdExeName = "Clowd.exe";
    public const string ClowdNamedPipe = "ClowdNamedPipe:02c1544e-7d60-435a-bce8-f61496bdbabe";
    public const string ClowdMutex = "ClowdMutex:02c1544e-7d60-435a-bce8-f61496bdbabe";
    public const string ClowdNativeLibName = "Clowd.Native";
    public const string ShortcutName = "Clowd.lnk";
    public const string PublishingCompany = "Caelan Sayler";
    public const string ContextMenuShellName = "Upload with Clowd";
    public const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string UninstallRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    public const string ServiceDomain = "caesay.com";
    public const string StableReleaseFeedUrl = "https://clowd-releases.s3.eu-central-003.backblazeb2.com/stable/";
    public const string ExperimentalReleaseFeedUrl = "https://clowd-releases.s3.eu-central-003.backblazeb2.com/experimental/";
    public const string InstallerExeName = "ClowdCLI.exe";
    public const string UpdateProcessName = "ClowdUpdate";
}

public static class PathConstants
{
    //public static string RoamingAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");
    //public static string LocalAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd");

    public static string AppData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "bin");
    public static string UpdateData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "updates");
    public static string BackupData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "backup");
    public static string LogData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "logs");
    public static string SessionData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "sessions");
    public static string PluginData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "plugins");
    public static string SettingsData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");
    public static string AppRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd");

    public static string GetFolderPath(string name, string parentDirectory)
    {
        var d = Path.Combine(Path.GetFullPath(parentDirectory), name);
        return d;
    }

    public static string GetFilePath(string name, string extension, string directory) => Path.Combine(Path.GetFullPath(directory), name + "." + extension.TrimStart('.'));

    public static string GetDatedFilePath(string name, string extension, string directory) =>
        Path.Combine(Path.GetFullPath(directory), GetDatedFileName(name, extension));

    public static string GetDatedFileName(string name, string extension) => name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "." + extension.TrimStart('.');

    public static string GetFreePatternFileName(string directory, string pattern)
    {
        var files = Directory.EnumerateFiles(directory).Select(Path.GetFileNameWithoutExtension).ToArray();

        for (int i = 0; i < 100; i++)
        {
            var dateStr = DateTime.Now.ToString(pattern);
            if (i > 0) dateStr += $" ({i})";

            if (files.Any(f => f.EqualsIgnoreCase(dateStr)))
                continue;

            return dateStr;
        }

        return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
    }

    private static string GetClowdFolder(Environment.SpecialFolder dataDirectory, string dataName) =>
        GetClowdFolder(Environment.GetFolderPath(dataDirectory), dataName);

    private static string GetClowdFolder(string dataDirectory, string dataName)
    {
        if (!Directory.Exists(dataDirectory))
            throw new ArgumentException($"Directory '{dataDirectory}' does not exist.");

        if (String.IsNullOrWhiteSpace(dataName))
            throw new ArgumentException($"Directory name can not be empty.");

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
