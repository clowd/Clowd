using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ControlPanelInfo
    {
        // Required
        public string DisplayName { get; set; }
        public string UninstallString { get; set; }

        // Optional
        public string Publisher { get; set; }
        public string InstallDirectory { get; set; }
        public string DisplayIconPath { get; set; }
        public string DisplayVersion { get; set; }
        public int? EstimatedSizeInKB { get; set; }
        public string HelpLink { get; set; }

        // other possible registry entries -
        // from https://nsis.sourceforge.io/Add_uninstall_information_to_Add/Remove_Programs

        // ModifyPath 
        // InstallSource
        // ProductID
        // Readme
        // RegOwner
        // RegCompany
        // VersionMajor (dword)
        // VersionMinor (dword)
        // NoModify (dword)
        // NoRepair (dword)
        // SystemComponent (dword)  - prevents display of the application in the Programs List of the Add/Remove Programs 
        // Comments
        // URLUpdateInfo
        // URLInfoAbout

        public static void Install(string appkey, ControlPanelInfo info, InstallMode mode)
        {
            if (String.IsNullOrEmpty(info.DisplayName))
                throw new ArgumentNullException(nameof(DisplayName));

            if (String.IsNullOrEmpty(info.UninstallString))
                throw new ArgumentNullException(nameof(DisplayName));

            if (info.EstimatedSizeInKB == null && Directory.Exists(info.InstallDirectory))
            {
                int sizeInKb = 0;
                foreach (var p in Directory.EnumerateFiles(info.InstallDirectory))
                {
                    var f = new FileInfo(p);
                    sizeInKb += (int)(f.Length / 1000);
                }
                if (sizeInKb > 0)
                {
                    info.EstimatedSizeInKB = sizeInKb;
                }
            }

            using (var key = RegistryEx.CreateKeyFromRootPath(Constants.UninstallRegistryPath + "\\" + info.DisplayName, mode))
            {
                key.SetValue("DisplayName", info.DisplayName);
                key.SetValue("UninstallString", "\"" + info.UninstallString + "\"");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

                if (!String.IsNullOrWhiteSpace(info.Publisher))
                    key.SetValue("Publisher", info.Publisher);

                if (!String.IsNullOrWhiteSpace(info.InstallDirectory))
                    key.SetValue("InstallLocation", "\"" + info.InstallDirectory + "\"");

                if (!String.IsNullOrWhiteSpace(info.DisplayIconPath))
                    key.SetValue("DisplayIcon", "\"" + info.DisplayIconPath + "\"");

                if (!String.IsNullOrWhiteSpace(info.DisplayVersion))
                    key.SetValue("DisplayVersion", info.DisplayVersion);

                if (info.EstimatedSizeInKB != null)
                    key.SetValue("EstimatedSize", info.EstimatedSizeInKB.Value);

                if (!String.IsNullOrWhiteSpace(info.HelpLink))
                    key.SetValue("HelpLink", info.HelpLink);
            }
        }

        public static ControlPanelInfo GetInfo(string appKey, RegistryQuery query)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, query))
            {
                using (root)
                {
                    var appkey = root.OpenSubKey(Constants.ClowdAppName);
                    if (appkey != null)
                    {
                        var info = new ControlPanelInfo()
                        {
                            DisplayName = appkey.GetValue("DisplayName") as string,
                            UninstallString = (appkey.GetValue("UninstallString") as string)?.Trim('"'),
                            Publisher = appkey.GetValue("Publisher") as string,
                            InstallDirectory = (appkey.GetValue("InstallLocation") as string)?.Trim('"'),
                            DisplayIconPath = (appkey.GetValue("DisplayIcon") as string)?.Trim('"'),
                            DisplayVersion = appkey.GetValue("DisplayVersion") as string,
                            EstimatedSizeInKB = appkey.GetValue("EstimatedSize") as int?,
                            HelpLink = appkey.GetValue("HelpLink") as string,
                        };
                        return info;
                    }
                }
            }
            return null;
        }

        public static void Uninstall(string appKey, RegistryQuery query)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, query))
            {
                using (root)
                {
                    root.DeleteSubKey(appKey, false);
                }
            }
        }
    }
}
