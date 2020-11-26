using NAppUpdate.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Clowd.Installer
{
    public static class UpdateHelper
    {
        static UpdateManager _updateManager;

        public static UpdateManager GetUpdaterInstance(string applicationDirectory, string applicationExePath)
        {
            if (_updateManager == null)
            {
                // NAppUpdater uses relative paths, so the current directory must be set accordingly.
                //Environment.CurrentDirectory = applicationDirectory;// Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                _updateManager = UpdateManager.Instance;
                _updateManager.Config.DirectoryToUpdate = applicationDirectory;
                _updateManager.Config.ApplicationPath = applicationExePath;
                _updateManager.Config.TempFolder = PathConstants.UpdateData;
                _updateManager.Config.BackupFolder = PathConstants.BackupData;
                //_updateManager.Config.UpdateExecutableName = "clowd-upd.exe";
            }

            return _updateManager;
        }

        public static async Task<UpdatePackage> GetLatestChannelReleaseAsync(string channel = null)
        {
            var avl = await GetAvailablePackagesAsync();

            if (String.IsNullOrEmpty(channel))
                channel = avl.MainChannel;

            var rel = avl.Packages
                .Where(p => p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Version)
                .FirstOrDefault();

            return rel;
        }

        public static UpdatePackage GetLatestChannelRelease(string channel = null)
        {
            var avl = GetAvailablePackages();

            if (String.IsNullOrEmpty(channel))
                channel = avl.MainChannel;

            var rel = avl.Packages
                .Where(p => p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Version)
                .FirstOrDefault();

            return rel;
        }

        public static string GetCurrentVersion()
        {
            if (File.Exists("version"))
                return File.ReadAllText("version");

            return "dev-build";
        }

        public static async Task<AvailablePackagesResult> GetAvailablePackagesAsync()
        {
            using (var wc = new WebClient())
            {
                var feed = await wc.DownloadStringTaskAsync(Constants.ReleaseFeedUrl);
                var doc = XDocument.Parse(feed);

                var defaultChannel = doc.Root.Attribute("MainChannel").Value;
                var packages = new List<UpdatePackage>();

                foreach (var el in doc.Root.Elements("Package"))
                {
                    var version = el.Attribute("Version").Value;
                    var channel = el.Attribute("Channel").Value;
                    var url = el.Attribute("FeedUrl").Value;

                    packages.Add(new UpdatePackage()
                    {
                        Version = version,
                        Channel = channel,
                        FeedUrl = url
                    });
                }

                return new AvailablePackagesResult()
                {
                    MainChannel = defaultChannel,
                    Packages = packages,
                };
            }
        }

        public static AvailablePackagesResult GetAvailablePackages()
        {
            using (var wc = new WebClient())
            {
                var feed = wc.DownloadString(Constants.ReleaseFeedUrl);
                var doc = XDocument.Parse(feed);

                var defaultChannel = doc.Root.Attribute("MainChannel").Value;
                var packages = new List<UpdatePackage>();

                foreach (var el in doc.Root.Elements("Package"))
                {
                    var version = el.Attribute("Version").Value;
                    var channel = el.Attribute("Channel").Value;
                    var url = el.Attribute("FeedUrl").Value;

                    packages.Add(new UpdatePackage()
                    {
                        Version = version,
                        Channel = channel,
                        FeedUrl = url
                    });
                }

                return new AvailablePackagesResult()
                {
                    MainChannel = defaultChannel,
                    Packages = packages,
                };
            }
        }
    }

    public class AvailablePackagesResult
    {
        public string MainChannel { get; set; }
        public List<UpdatePackage> Packages { get; set; }
    }

    public class UpdatePackage
    {
        public string Version { get; set; }
        public string Channel { get; set; }
        public string FeedUrl { get; set; }
    }
}
