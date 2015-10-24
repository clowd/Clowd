using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ControlPanel : IFeatureInstaller
    {
        public bool CheckInstalled(string assetPath, RegistryQuery context)
        {
            bool found = false;
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, context))
            {
                using (root)
                {
                    if (root.OpenSubKey(Constants.AppName) != null)
                        found = true;
                }
            }
            return found;
        }

        public void Install(string assetPath, InstallMode context)
        {
            using (var key = RegistryEx.CreateKeyFromRootPath(Constants.UninstallRegistryPath + "\\" + Constants.AppName, context))
            {
                key.SetValue("DisplayName", Constants.AppName);
                key.SetValue("Publisher", Constants.PublishingCompany);
                key.SetValue("DisplayIcon", assetPath);
                key.SetValue("DisplayVersion", "∞");
                key.SetValue("URLInfoAbout", "http://clowd.ca");
                key.SetValue("Contact", "support@clowd.ca");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("InstallLocation", Path.GetDirectoryName(assetPath));
                key.SetValue("UninstallString", assetPath + " /uninstall");
            }
        }

        public void Uninstall(string assetPath, RegistryQuery context)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, context))
            {
                using (root)
                {
                    root.DeleteSubKey(Constants.AppName, false);
                }
            }
        }
    }
}
