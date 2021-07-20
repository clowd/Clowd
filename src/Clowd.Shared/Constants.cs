﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public static class Constants
    {
        public const string ClowdAppName = "Clowd";
        public const string ClowdExeName = "Clowd.exe";
        public const string ClowdNamedPipe = "ClowdRunningPipe";
        public const string ClowdMutex = "ClowdMutex000";
        public const string DirectShowAppName = "ClowdDirectShow";
        public const string ShortcutName = "Clowd.lnk";
        public const string PublishingCompany = "Caesa Consulting Ltd.";
        public const string ContextMenuShellName = "Upload with Clowd";
        public const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string UninstallRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        public const string ServiceDomain = "caesay.com";
        public const string ReleaseFeedUrl = "https://caesay.com/clowd-updates";
        public const string InstallerExeName = "ClowdCLI.exe";
        public const string UpdateProcessName = "ClowdUpdate";
        public static bool Debugging => System.Diagnostics.Debugger.IsAttached;
        public static string CurrentExePath => System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
    }

    public static class PathConstants
    {
        //public static string RoamingAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd");
        //public static string LocalAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd");

        public static string UpdateData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "Updates");
        public static string BackupData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "Backup");
        public static string LogData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "Logs");
        public static string SessionData => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "Session");
        public static string Plugins => GetClowdFolder(Environment.SpecialFolder.LocalApplicationData, "Plugins");

        public static string GetFolderPath(string name, string parentDirectory)
        {
            var d = Path.Combine(Path.GetFullPath(parentDirectory), name);
            return d;
        }
        public static string GetFilePath(string name, string extension, string directory) => Path.Combine(Path.GetFullPath(directory), name + "." + extension);
        public static string GetDatedFilePath(string name, string extension, string directory) => Path.Combine(Path.GetFullPath(directory), GetDatedFileName(name, extension));
        public static string GetDatedFileName(string name, string extension) => name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "." + extension;

        private static string GetClowdFolder(Environment.SpecialFolder dataDirectory, string dataName) => GetClowdFolder(Environment.GetFolderPath(dataDirectory), dataName);

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
                using (FileStream fs = File.Create(Path.Combine(dirPath, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose))
                { }
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
