using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class ContextMenu : IFeature
    {
        private static readonly string[] ContextMenuInstallLocations = new[]
        {
            @"Software\Classes\*\shell",
            @"Software\Classes\Directory\shell"
        };

        public bool CheckInstalled(string assetPath)
        {
            var found = false;
            foreach (var str in ContextMenuInstallLocations)
            {
                foreach (var root in RegistryEx.OpenKeysFromRootPath(str, RegistryQuery.CurrentUser))
                {
                    //can't return here, need to dispose all of the registry keys created by OpenKeysFromRootPath
                    var appkey = root.OpenSubKey(Constants.ContextMenuShellName);
                    if (appkey != null)
                    {
                        var location = appkey.GetValue("Icon") as string;
                        if (SystemEx.AreFileSystemObjectsEqual(location, assetPath))
                            found = true;
                    }

                    root.Dispose();
                }
            }
            return found;
        }

        public void Install(string assetPath)
        {
            foreach (var str in ContextMenuInstallLocations)
            {
                using (var root = RegistryEx.CreateKeyFromRootPath(str, InstallMode.CurrentUser))
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

        public bool NeedsPrivileges()
        {
            return false;
        }

        public void Uninstall(string assetPath)
        {
            foreach (var str in ContextMenuInstallLocations)
            {
                foreach (var root in RegistryEx.OpenKeysFromRootPath(str, RegistryQuery.CurrentUser))
                {
                    root.DeleteSubKeyTree(Constants.ContextMenuShellName, false);
                    root.Dispose();
                }
            }
        }
    }
}
