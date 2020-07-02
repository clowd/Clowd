using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class AutoStart : IFeatureInstaller
    {
        public bool CheckInstalled(string assetPath, RegistryQuery context)
        {
            bool found = false;
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, context))
            {
                using (root)
                {
                    if (root.GetValue(Constants.AppName) != null)
                        found = true;
                }
            }
            return found;
        }

        public void Install(string assetPath, InstallMode context)
        {
            using (var root = RegistryEx.CreateKeyFromRootPath(Constants.RunRegistryPath, context))
            {
                root.SetValue(Constants.AppName, assetPath);
            }
        }

        public void Uninstall(string assetPath, RegistryQuery context)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, context))
            {
                using (root)
                {
                    root.DeleteValue(Constants.AppName);
                }
            }
        }
    }
}
