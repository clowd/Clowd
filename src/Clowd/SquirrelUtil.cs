using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Clowd.PlatformUtil.Windows;
using Squirrel;

[assembly: AssemblyMetadata("SquirrelAwareVersion", "1")]

namespace Clowd
{
    internal static class SquirrelUtil
    {
        public static string UniqueAppKey => "Clowd";

        public static void Startup(string[] args)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            SquirrelAwareApp.HandleEvents(
                onInitialInstall: OnInstall,
                onAppUpdate: OnUpdate,
                onAppUninstall: OnUninstall,
                onFirstRun: OnFirstRun,
                arguments: args);
        }

        private static void OnInstall(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath, "--upload");
            srv.ExplorerAllFilesMenu = menu;
            srv.ExplorerDirectoryMenu = menu;
            srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUpdate(Version obj)
        {
            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.CreateUninstallerRegistryEntry();
            mgr.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);

            // only update registry during update if they have not been removed by user
            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            var menu = new ExplorerMenuLaunchItem("Upload with Clowd", AssemblyRuntimeInfo.EntryExePath, AssemblyRuntimeInfo.EntryExePath, "--upload");
            if (srv.ExplorerAllFilesMenu != null)
                srv.ExplorerAllFilesMenu = menu;
            if (srv.ExplorerDirectoryMenu != null)
                srv.ExplorerDirectoryMenu = menu;
            if (srv.AutoStartLaunchPath != null)
                srv.AutoStartLaunchPath = AssemblyRuntimeInfo.EntryExePath;
        }

        private static void OnUninstall(Version obj)
        {
            var srv = new InstallerServices(UniqueAppKey, InstallerLocation.CurrentUser);
            srv.RemoveAll();

            using var mgr = new UpdateManager(Constants.ReleaseFeedUrl, UniqueAppKey);
            mgr.RemoveShortcutForThisExe(ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop);
            mgr.RemoveUninstallerRegistryEntry();
        }

        private static void OnFirstRun()
        {
            MessageBox.Show("Thanks for installing clowd, it is running in the system tray!");
        }
    }
}
