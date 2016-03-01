using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Utilities
{
    public static class RegistryOptions
    {
        private const string ContextMenuShellName = "Upload with Clowd";
        private const string AppName = "Clowd";
        private static readonly string[] ContextMenuInstallLocations = new[]
        {
            @"*\shell",
            @"Directory\shell"
        };

        public static bool GetAutoStart()
        {
            RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            return registryKey.GetValue(AppName) != null;
        }
        public static void SetAutoStart(bool startWithWindows)
        {
            RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (startWithWindows)
            {
                registryKey.SetValue(AppName, Assembly.GetExecutingAssembly().Location);
            }
            else
            {
                registryKey.DeleteValue(AppName);
            }
        }
        public static bool GetContextMenu()
        {
            using (var view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var clsid32 = view32.OpenSubKey(@"Software\Classes\", true))
            {
                foreach (var location in ContextMenuInstallLocations)
                {
                    using (var subkey = clsid32.CreateSubKey(location))
                    {
                        if (CheckInstalledContextMenu(subkey))
                            return true;
                    }
                }
            }
            return false;
        }
        public static void SetContextMenu(bool enabled)
        {
            using (var view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var clsid32 = view32.OpenSubKey(@"Software\Classes\", true))
            {
                foreach (var location in ContextMenuInstallLocations)
                {
                    using (var subkey = clsid32.CreateSubKey(location))
                    {
                        if (enabled)
                            InstallContextMenu(subkey);
                        else
                            UninstallContextMenu(subkey);
                    }
                }
            }
        }

        private static void InstallContextMenu(RegistryKey shellRoot)
        {
            var file = Assembly.GetExecutingAssembly().Location;
            using (var clowd = shellRoot.CreateSubKey(ContextMenuShellName))
            {
                clowd.SetValue("Icon", file, RegistryValueKind.String);
                using (var command = clowd.CreateSubKey("command"))
                {
                    command.SetValue("", file);
                }
            }
        }
        private static bool CheckInstalledContextMenu(RegistryKey shellRoot)
        {
            return shellRoot.OpenSubKey(ContextMenuShellName) != null;
        }
        private static void UninstallContextMenu(RegistryKey shellRoot)
        {
            shellRoot.DeleteSubKeyTree(ContextMenuShellName, false);
        }
    }
}
