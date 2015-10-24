using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ContextMenu : IFeatureInstaller
    {
        private static readonly string[] ContextMenuInstallLocations = new[]
        {
            @"Software\Classes\*\shell",
            @"Software\Classes\Directory\shell"
        };

        public bool CheckInstalled(string assetPath, RegistryQuery context)
        {
            var found = false;
            foreach (var str in ContextMenuInstallLocations)
            {
                foreach (var root in RegistryEx.OpenKeysFromRootPath(str, context))
                {
                    //can't return here, need to dispose all of the registry keys created by OpenKeysFromRootPath
                    if (root.OpenSubKey(Constants.ContextMenuShellName) != null)
                        found = true;
                    root.Dispose();
                }
            }
            return found;
        }

        public void Install(string assetPath, InstallMode context)
        {
            foreach (var str in ContextMenuInstallLocations)
            {
                using (var root = RegistryEx.CreateKeyFromRootPath(str, context))
                {
                    using (var clowd = root.CreateSubKey(Constants.ContextMenuShellName))
                    {
                        clowd.SetValue("Icon", assetPath, RegistryValueKind.String);
                        using (var command = clowd.CreateSubKey("command"))
                        {
                            command.SetValue("", assetPath);
                        }
                    }
                }
            }
        }

        public void Uninstall(string assetPath, RegistryQuery context)
        {
            foreach (var str in ContextMenuInstallLocations)
            {
                foreach (var root in RegistryEx.OpenKeysFromRootPath(str, context))
                {
                    root.DeleteSubKeyTree(Constants.ContextMenuShellName, false);
                    root.Dispose();
                }
            }
        }
    }
}
