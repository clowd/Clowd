using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    public static class Constants
    {
        public const string ClowdAppName = "Clowd";
        public const string ClowdNamedPipe = "ClowdRunningPipe";
        public const string ClowdMutex = "ClowdMutex000";
        public const string DirectShowAppName = "ClowdDirectShow";
        public const string ShortcutName = ClowdAppName + ".lnk";
        public const string PublishingCompany = "Caesa Consulting Ltd.";
        public const string ContextMenuShellName = "Upload with Clowd";
        public const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string UninstallRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        public const string ServiceDomain = "caesay.com";
        public const string ReleaseFeedUrl = "https://caesay.com/clowd-updates";
        public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ClowdAppName);
    }
}
