using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Clowd.PlatformUtil.Windows
{
    public enum InstallerLocation
    {
        CurrentUser,
        LocalMachine,
    }

    [Flags]
    public enum ExplorerMenuCommandFlags
    {
        None = 0,
        HasSubCommands = 0x1,
        HasSplitButton = 0x2,
        HideLabel = 0x4,
        IsSeparator = 0x8,
        HasUacShield = 0x10,
        SeparatorBefore = 0x20,
        SeparatorAfter = 0x40,
        IsDropDown = 0x80,
        Toggleable = 0x100,
        AutoMenuIcons = 0x200,
    }

    public abstract class ExplorerMenuBase
    {
        public string Title { get; init; }
        public string IconPath { get; init; }
        public ExplorerMenuCommandFlags CommandFlags { get; init; }

        protected ExplorerMenuBase(string title, string iconPath, ExplorerMenuCommandFlags flags)
        {
            Title = title;
            IconPath = iconPath;
            CommandFlags = flags;
        }

        protected virtual RegistryKey WriteCommonKey(string name, RegistryKey rootKey, bool muiverb)
        {
            var me = rootKey.CreateOrOpenExisting(name);

            // if this entry is part of a menu group, the title must be in the "MUIVerb" instead of "(Default)"
            me.SetValue(muiverb ? "MUIVerb" : "", Title);

            if (!String.IsNullOrWhiteSpace(IconPath))
                me.SetValue("Icon", $"\"{IconPath}\"");

            if (CommandFlags != ExplorerMenuCommandFlags.None)
                me.SetValue("CommandFlags", CommandFlags, RegistryValueKind.DWord);

            return me;
        }

        internal abstract void SaveToKey(string name, RegistryKey rootKey, bool muiverb);

        internal static ExplorerMenuBase ReadFromKey(string name, RegistryKey rootKey)
        {
            using var me = rootKey.OpenSubKey(name);
            if (me == null)
                return null;

            var title = (me.GetValue("MUIVerb") as string) ?? (me.GetValue("") as string);
            var icon = me.GetValue("Icon") as string;

            using var subShell = me.OpenSubKey("shell");

            if (subShell == null) // this is a single command item
            {
                var command = me.OpenSubKey("command")?.GetValue("") as string;
                return new ExplorerMenuLaunchItem(title, icon, command, "");
            }
            else // this is a group
            {
                List<ExplorerMenuBase> children = new List<ExplorerMenuBase>();
                foreach (var c in subShell.GetSubKeyNames())
                    children.Add(ReadFromKey(c, subShell));
                return new ExplorerMenuGroup(title, icon, children.ToArray());
            }
        }
    }

    public class ExplorerMenuLaunchItem : ExplorerMenuBase
    {
        public string LaunchApplicationPath { get; init; }
        public string LaunchArgument { get; init; }

        public ExplorerMenuLaunchItem(string title, string iconPath, string launchPath)
            : this(title, iconPath, launchPath, null, ExplorerMenuCommandFlags.None)
        { }

        public ExplorerMenuLaunchItem(string title, string iconPath, string launchPath, string launchArgument)
            : this(title, iconPath, launchPath, launchArgument, ExplorerMenuCommandFlags.None)
        { }

        public ExplorerMenuLaunchItem(string title, string iconPath, string launchPath, string launchArgument, ExplorerMenuCommandFlags flags)
            : base(title, iconPath, flags)
        {
            LaunchApplicationPath = launchPath;
            LaunchArgument = launchArgument;
        }

        internal override void SaveToKey(string name, RegistryKey rootKey, bool muiverb)
        {
            using var me = WriteCommonKey(name, rootKey, muiverb);
            using var cmd = me.CreateOrOpenExisting("command");
            var argument = String.IsNullOrWhiteSpace(LaunchArgument) ? "" : $"{LaunchArgument} ";
            cmd.SetValue("", $"\"{LaunchApplicationPath}\" {argument}\"%1\"");
        }
    }

    public class ExplorerMenuGroup : ExplorerMenuBase
    {
        public ExplorerMenuBase[] Children { get; init; }

        public ExplorerMenuGroup(string title, string iconPath, ExplorerMenuBase[] children)
            : this(title, iconPath, children, ExplorerMenuCommandFlags.None)
        { }

        public ExplorerMenuGroup(string title, string iconPath, ExplorerMenuBase[] children, ExplorerMenuCommandFlags flags)
            : base(title, iconPath, flags)
        {
            Children = children;
        }

        internal override void SaveToKey(string name, RegistryKey rootKey, bool muiverb)
        {
            muiverb = true; // this is always true for groups

            using var me = WriteCommonKey(name, rootKey, muiverb);

            var searchTxt = @"Software\Classes\";
            var searchStart = me.Name.IndexOf(searchTxt, StringComparison.OrdinalIgnoreCase);
            var extendedName = me.Name.Substring(searchStart + searchTxt.Length);
            me.SetValue("ExtendedSubCommandsKey", extendedName);

            using var subShell = me.CreateOrOpenExisting("shell");
            for (int i = 0; i < Children.Length; i++)
                Children[i].SaveToKey("subCmd_" + i, subShell, true);
        }
    }

    public class AppsFeaturesEntry
    {
        // Required
        public string DisplayName { get; set; }
        public string UninstallString { get; set; }

        // Optional
        public string Publisher { get; set; }
        public string InstallDirectory { get; set; }
        public string DisplayIconPath { get; set; }
        public string DisplayVersion { get; set; }
        public int? EstimatedSizeInKB { get; set; }
        public string HelpLink { get; set; }

        // other possible registry entries -
        // from https://nsis.sourceforge.io/Add_uninstall_information_to_Add/Remove_Programs

        // ModifyPath 
        // InstallSource
        // ProductID
        // Readme
        // RegOwner
        // RegCompany
        // VersionMajor (dword)
        // VersionMinor (dword)
        // NoModify (dword)
        // NoRepair (dword)
        // SystemComponent (dword)  - prevents display of the application in the Programs List of the Add/Remove Programs 
        // Comments
        // URLUpdateInfo
        // URLInfoAbout

        internal void SaveToKey(string name, RegistryKey rootKey)
        {
            if (String.IsNullOrEmpty(DisplayName))
                throw new ArgumentNullException(nameof(DisplayName));

            if (String.IsNullOrEmpty(UninstallString))
                throw new ArgumentNullException(nameof(UninstallString));

            if (EstimatedSizeInKB == null && Directory.Exists(InstallDirectory))
            {
                int sizeInKb = 0;
                foreach (var p in Directory.EnumerateFiles(InstallDirectory, "*", SearchOption.AllDirectories))
                {
                    var f = new FileInfo(p);
                    sizeInKb += (int)(f.Length / 1000);
                }

                if (sizeInKb > 0)
                {
                    EstimatedSizeInKB = sizeInKb;
                }
            }

            using var key = rootKey.CreateOrOpenExisting(name);

            key.SetValue("DisplayName", DisplayName);
            key.SetValue("UninstallString", "\"" + UninstallString + "\"");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

            if (!String.IsNullOrWhiteSpace(Publisher))
                key.SetValue("Publisher", Publisher);

            if (!String.IsNullOrWhiteSpace(InstallDirectory))
                key.SetValue("InstallLocation", "\"" + InstallDirectory + "\"");

            if (!String.IsNullOrWhiteSpace(DisplayIconPath))
                key.SetValue("DisplayIcon", "\"" + DisplayIconPath + "\"");

            if (!String.IsNullOrWhiteSpace(DisplayVersion))
                key.SetValue("DisplayVersion", DisplayVersion);

            if (EstimatedSizeInKB != null)
                key.SetValue("EstimatedSize", EstimatedSizeInKB.Value);

            if (!String.IsNullOrWhiteSpace(HelpLink))
                key.SetValue("HelpLink", HelpLink);
        }

        internal static AppsFeaturesEntry ReadFromKey(string name, RegistryKey rootKey)
        {
            using var appkey = rootKey.OpenSubKey(name);
            if (appkey == null) return null;
            return new AppsFeaturesEntry()
            {
                DisplayName = appkey.GetValue("DisplayName") as string,
                UninstallString = (appkey.GetValue("UninstallString") as string)?.Trim('"'),
                Publisher = appkey.GetValue("Publisher") as string,
                InstallDirectory = (appkey.GetValue("InstallLocation") as string)?.Trim('"'),
                DisplayIconPath = (appkey.GetValue("DisplayIcon") as string)?.Trim('"'),
                DisplayVersion = appkey.GetValue("DisplayVersion") as string,
                EstimatedSizeInKB = appkey.GetValue("EstimatedSize") as int?,
                HelpLink = appkey.GetValue("HelpLink") as string,
            };
        }
    }

    public class InstallerServices
    {
        private const string RegistryRunPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RegistryUninstallPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
        private const string RegistryContextMenuFilesPath = @"Software\Classes\*\shell";
        private const string RegistryContextMenuDirectoryPath = @"Software\Classes\Directory\shell";

        public string ApplicationUniqueKey { get; }
        public InstallerLocation Location { get; }

        public InstallerServices(string appUniqueKey, InstallerLocation location)
        {
            ApplicationUniqueKey = appUniqueKey;
            Location = location;
        }

        public string AutoStartLaunchPath
        {
            get => OpenRegistryPath(RegistryRunPath, k => k.GetValue(ApplicationUniqueKey) as string);
            set
            {
                OpenRegistryPath(RegistryRunPath, k => k.DeleteValue(ApplicationUniqueKey, false));
                if (!String.IsNullOrWhiteSpace(value))
                    OpenRegistryPath(RegistryRunPath, k => k.SetValue(ApplicationUniqueKey, value));
            }
        }

        public ExplorerMenuBase ExplorerDirectoryMenu
        {
            get => OpenRegistryPath(RegistryContextMenuDirectoryPath, k => ExplorerMenuBase.ReadFromKey(ApplicationUniqueKey, k));
            set
            {
                OpenRegistryPath(RegistryContextMenuDirectoryPath, k => k.DeleteSubKeyTree(ApplicationUniqueKey, false));
                if (value != null)
                    OpenRegistryPath(RegistryContextMenuDirectoryPath, k => value.SaveToKey(ApplicationUniqueKey, k, false));
            }
        }

        public ExplorerMenuBase ExplorerAllFilesMenu
        {
            get => OpenRegistryPath(RegistryContextMenuFilesPath, k => ExplorerMenuBase.ReadFromKey(ApplicationUniqueKey, k));
            set
            {
                OpenRegistryPath(RegistryContextMenuFilesPath, k => k.DeleteSubKeyTree(ApplicationUniqueKey, false));
                if (value != null)
                    OpenRegistryPath(RegistryContextMenuFilesPath, k => value.SaveToKey(ApplicationUniqueKey, k, false));
            }
        }

        public AppsFeaturesEntry AppsAndFeaturesEntry
        {
            get => OpenRegistryPath(RegistryUninstallPath, k => AppsFeaturesEntry.ReadFromKey(ApplicationUniqueKey, k));
            set
            {
                OpenRegistryPath(RegistryUninstallPath, k => k.DeleteSubKeyTree(ApplicationUniqueKey, false));
                if (value != null)
                    OpenRegistryPath(RegistryUninstallPath, k => value.SaveToKey(ApplicationUniqueKey, k));
            }
        }

        public void RemoveAll()
        {
            AutoStartLaunchPath = null;
            ExplorerDirectoryMenu = null;
            ExplorerAllFilesMenu = null;
            AppsAndFeaturesEntry = null;
        }

        private void OpenRegistryPath(string regPath, Action<RegistryKey> work)
        {
            OpenRegistryPath(regPath, (k) =>
            {
                work(k);
                return true;
            });
        }

        private T OpenRegistryPath<T>(string regPath, Func<RegistryKey, T> work)
        {
            var hive = Location == InstallerLocation.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

            // we open both the 32 view and the 64 view. depending on the path, this may or may not be required.
            // see https://docs.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys
            // the performance implications of performing the same operation twice when it's not required is minor, 
            // but the consequences can be large if it's skipped when it is required (such as a context menu entry not appearing at the appropriate time)

            using var view32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
            using var key32 = view32.OpenSubKey(regPath, true) ?? view32.CreateSubKey(regPath);
            T result = work(key32);

            // we should add in the 64 bit key even if the current process is 32 bit, because we want 
            // both 32 bit and 64 bit applications to see these keys. If that is not the case (eg, registering a COM server)
            // then this method should not be used.
            if (Environment.Is64BitOperatingSystem)
            {
                using var view64 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key64 = view64.OpenSubKey(regPath, true) ?? view64.CreateSubKey(regPath);
                var newResult = work(key64);

                if (!EqualityComparer<T>.Default.Equals(newResult, default(T)))
                    result = newResult; // we prefer the 64 result, if it exists, and there is a difference
            }

            return result;
        }
    }
}
