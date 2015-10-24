using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    public static class Constants
    {
        public const string AppName = "Clowd";
        public const string PublishingCompany = "Caesa Consulting Ltd.";
        public const string ContextMenuShellName = "Upload with Clowd";
        public const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string UninstallRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        public const string ServiceDomain = "clowd.ca";
        public static readonly string ServiceFeedUrl = $"http://{ServiceDomain}/app_updates/feed.xml";
    }
}
