using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Squirrel;

[assembly: AssemblyMetadata("SquirrelAwareVersion", "1")]

namespace Clowd
{
    internal static class SquirrelUtil
    {
        public static void Startup(string[] args)
        {
            using var mgr = Get();
            SquirrelAwareApp.HandleEvents(
                onInitialInstall: OnInstall,
                onAppUpdate: OnUpdate,
                onAppUninstall: OnUninstall,
                onFirstRun: OnFirstRun,
                arguments: args);
        }

        private static void OnInstall(Version obj)
        {
            MessageBox.Show("Thanks for install clowd");
        }

        private static void OnUpdate(Version obj)
        {
            MessageBox.Show("Thanks for updating clowd");
        }

        private static void OnUninstall(Version obj)
        {
            MessageBox.Show("Thanks for uninstalling clowd");
        }

        private static void OnFirstRun()
        {
            MessageBox.Show("Thanks for first run clowd");
        }

        private static UpdateManager Get()
        {
            return new UpdateManager(
                Constants.ReleaseFeedUrl,
                "Clowd",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clowd")
            );
        }
    }
}
