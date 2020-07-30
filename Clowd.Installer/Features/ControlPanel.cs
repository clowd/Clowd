using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ControlPanel : IFeature
    {
        public bool CheckInstalled(string assetPath)
        {
            bool found = false;
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, RegistryQuery.CurrentUser))
            {
                using (root)
                {
                    var appkey = root.OpenSubKey(Constants.AppName);
                    if (appkey != null)
                    {
                        var location = appkey.GetValue("InstallLocation") as string;
                        if (SystemEx.AreFileSystemObjectsEqual(location, assetPath))
                        {
                            found = true;
                        }
                    }
                }
            }
            return found;
        }

        public void Install(string assetPath)
        {
            using (var key = RegistryEx.CreateKeyFromRootPath(Constants.UninstallRegistryPath + "\\" + Constants.AppName, InstallMode.CurrentUser))
            {
                key.SetValue("DisplayName", Constants.AppName);
                key.SetValue("Publisher", Constants.PublishingCompany);
                key.SetValue("DisplayIcon", assetPath);
                key.SetValue("DisplayVersion", "∞");
                //key.SetValue("URLInfoAbout", "http://clowd.ca");
                //key.SetValue("Contact", "support@clowd.ca");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("InstallLocation", Path.GetDirectoryName(assetPath));
                key.SetValue("UninstallString", assetPath + " /uninstall");
            }
        }

        public bool NeedsPrivileges()
        {
            return false;
        }

        public void Uninstall(string assetPath)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, RegistryQuery.CurrentUser))
            {
                using (root)
                {
                    root.DeleteSubKey(Constants.AppName, false);
                }
            }
        }
    }
}
