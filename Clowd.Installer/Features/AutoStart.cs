using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class AutoStart : IFeature
    {
        public bool CheckInstalled(string assetPath)
        {
            bool found = false;
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, RegistryQuery.CurrentUser))
            {
                var applocation = root.GetValue(Constants.AppName) as string;
                if (applocation != null && SystemEx.AreFileSystemObjectsEqual(applocation, assetPath))
                    found = true;

                root.Dispose();
            }
            return found;
        }

        public void Install(string assetPath)
        {
            using (var root = RegistryEx.CreateKeyFromRootPath(Constants.RunRegistryPath, InstallMode.CurrentUser))
            {
                root.SetValue(Constants.AppName, assetPath);
            }
        }

        public bool NeedsPrivileges()
        {
            return false;
        }

        public void Uninstall(string assetPath)
        {
            foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.RunRegistryPath, RegistryQuery.CurrentUser))
            {
                root.DeleteValue(Constants.AppName);
                root.Dispose();
            }
        }
    }
}
