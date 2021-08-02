using NAppUpdate.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Clowd.Setup
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
                Environment.CurrentDirectory = applicationDirectory;
                _updateManager = UpdateManager.Instance;
                _updateManager.Config.DirectoryToUpdate = applicationDirectory;
                _updateManager.Config.ApplicationPath = applicationExePath;
                _updateManager.Config.TempFolder = PathConstants.UpdateData;
                _updateManager.Config.BackupFolder = PathConstants.BackupData;
                //_updateManager.Config.UpdateExecutableName = "clowd-upd.exe";
            }

            return _updateManager;
        }

        public static async Task<UpdatePackage> GetLatestReleaseAsync(bool includePrerelease = false)
        {
            var avl = await GetAvailablePackagesAsync();
            return GetLatest(avl, includePrerelease);
        }

        public static UpdatePackage GetLatestRelease(bool includePrerelease = false)
        {
            var avl = GetAvailablePackages();
            return GetLatest(avl, includePrerelease);
        }

        private static UpdatePackage GetLatest(AvailablePackagesResult packages, bool includePre)
        {
            var ordered = packages.Packages.OrderByDescending(p => Version.Parse(p.Version));

            if (includePre)
                return ordered.FirstOrDefault();

            return ordered.FirstOrDefault(o => !o.Prerelease) ?? ordered.FirstOrDefault();
        }

        public static AvailablePackagesResult GetAvailablePackages()
        {
            using var wc = new WebClient();
            var feed = wc.DownloadString(Constants.ReleaseFeedUrl);
            return ParsePackagesResult(feed);
        }

        public static async Task<AvailablePackagesResult> GetAvailablePackagesAsync()
        {
            using var wc = new WebClient();
            var feed = await wc.DownloadStringTaskAsync(Constants.ReleaseFeedUrl);
            return ParsePackagesResult(feed);
        }

        private static AvailablePackagesResult ParsePackagesResult(string xml)
        {
            var doc = XDocument.Parse(xml);
            var packages = new List<UpdatePackage>();

            foreach (var el in doc.Root.Elements("Package"))
            {
                var version = el.Attribute("version").Value;
                var pre = el.Attribute("pre")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                var url = el.Attribute("feedUrl").Value;

                packages.Add(new UpdatePackage()
                {
                    Version = version,
                    Prerelease = pre,
                    FeedUrl = url
                });
            }

            return new AvailablePackagesResult()
            {
                Packages = packages,
            };
        }
    }

    public class AvailablePackagesResult
    {
        //public string MainChannel { get; set; }
        public List<UpdatePackage> Packages { get; set; }
    }

    public class UpdatePackage
    {
        public string Version { get; set; }
        public string FeedUrl { get; set; }
        public bool Prerelease { get; set; }
    }
}
