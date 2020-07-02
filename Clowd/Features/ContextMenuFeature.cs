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
            foreach (var str in ContextMenuInstallLocations)
            {
                var disposableKeys = RegistryEx.OpenKeysFromRootPath(str, context);
                try
                {
                    foreach (var root in disposableKeys)
                    {
                        var subKey = root.OpenSubKey(Constants.ContextMenuShellName);
                        if (subKey != null)
                        {
                            subKey.Dispose();
                            return true;
                        }
                    }
                }
                finally
                {
                    foreach (var root in disposableKeys)
                    {
                        root.Dispose();
                    }
                }
            }
            return false;
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
                            command.SetValue("", assetPath + " \"%1\"");
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
