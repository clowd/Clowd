using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    //public class ClowdInstallManager
    //{
    //    public static InstallQuery CheckInstalled()
    //    {
    //        using (var root = Registry.LocalMachine.OpenSubKey(Constants.UninstallRegistryPath, false))
    //        {
    //            if (root.OpenSubKey(Constants.AppName) != null)
    //                return InstallQuery.System;
    //        }
    //        int usersFound = 0;
    //        foreach (var root in RegistryEx.OpenKeysFromRootPath(Constants.UninstallRegistryPath, RegistryQuery.AllUsers, false))
    //        {
    //            using (root)
    //            {
    //                if (root.OpenSubKey(Constants.AppName) != null)
    //                    usersFound++;
    //            }
    //        }
    //        bool currentUser = false;
    //        using (var root = Registry.CurrentUser.OpenSubKey(Constants.UninstallRegistryPath, false))
    //        {
    //            if (root.OpenSubKey(Constants.AppName) != null)
    //                currentUser = true;
    //        }

    //        if (currentUser && usersFound == 1)
    //            return InstallQuery.CurrentUser;
    //        if (currentUser && usersFound > 1)
    //            return InstallQuery.CurrentAndOtherUsers;
    //        if (usersFound > 1)
    //            return InstallQuery.OtherUsers;
    //        return InstallQuery.None;
    //    }

    //}
}
