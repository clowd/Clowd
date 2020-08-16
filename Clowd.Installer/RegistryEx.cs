using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management;
using System.IO;

namespace Clowd.Installer
{
    public class RegistryEx
    {
        public static string GetInstallPath(InstallMode mode)
        {
            if (mode == InstallMode.System)
            {
                string path = null;

                // try to get the 64 bit program files directory even if we are running as 32 bit
                if (Environment.Is64BitOperatingSystem)
                    path = Environment.GetEnvironmentVariable("ProgramW6432");

                if (path == null || !Directory.Exists(path))
                    path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                return path;
            }

            if (mode == InstallMode.CurrentUser)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        public static RegistryKey[] GetClassesRoot(RegistryQuery location)
        {
            var path = @"Software\Classes\";
            return OpenKeysFromRootPath(path, location);
        }
        public static string GetRegistryHiveName(InstallMode mode)
        {
            switch (mode)
            {
                case InstallMode.CurrentUser:
                    return "HKEY_CURRENT_USER";
                case InstallMode.System:
                    return "HKEY_LOCAL_MACHINE";
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        //public static RegistryHive GetRegistryHive(InstallMode mode)
        //{
        //    switch (mode)
        //    {
        //        case InstallMode.CurrentUser:
        //            return RegistryHive.CurrentUser;
        //        case InstallMode.System:
        //            return RegistryHive.LocalMachine;
        //        default:
        //            throw new ArgumentOutOfRangeException(nameof(mode));
        //    }
        //}
        public static RegistryKey[] OpenKeysFromRootPath(string path, RegistryQuery location, bool writable = true)
        {
            // see here re. redirection
            // https://docs.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys

            List<RegistryKey> keys = new List<RegistryKey>();
            if (location == RegistryQuery.AllUsers || location == RegistryQuery.AllUsersAndSystem)
            {
                foreach (var user in GetAllSystemUsers())
                {
                    var sid = GetSIDFromUserName(user);
                    using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry32))
                    {
                        var k = b32.OpenSubKey(sid + "\\" + path, writable);
                        if (k != null)
                            keys.Add(k);
                    }

                    // if this is a 64 bit operating system, we want to include the 64 bit keys too..
                    if (Environment.Is64BitOperatingSystem)
                    {
                        using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
                        {
                            var k = b64.OpenSubKey(sid + "\\" + path, writable);
                            if (k != null)
                                keys.Add(k);
                        }
                    }
                }
            }
            else if (location == RegistryQuery.CurrentUser)
            {
                using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
                {
                    var k = b32.OpenSubKey(path, writable);
                    if (k != null)
                        keys.Add(k);
                }

                if (Environment.Is64BitOperatingSystem)
                {
                    using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                    {
                        var k = b64.OpenSubKey(path, writable);
                        if (k != null)
                            keys.Add(k);
                    }
                }
            }
            if (location == RegistryQuery.System || location == RegistryQuery.AllUsersAndSystem)
            {
                using (var b32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                {
                    var k = b32.OpenSubKey(path, writable);
                    if (k != null)
                        keys.Add(k);
                }

                if (Environment.Is64BitOperatingSystem)
                {
                    using (var b64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    {
                        var k = b64.OpenSubKey(path, writable);
                        if (k != null)
                            keys.Add(k);
                    }
                }
            }
            return keys.ToArray();
        }
        public static RegistryKey CreateKeyFromRootPath(string path, InstallMode location, RegistryView view = RegistryView.Default)
        {
            var hive = location == InstallMode.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

            if (view == RegistryView.Default)
                view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;

            using (var b = RegistryKey.OpenBaseKey(hive, view))
            {
                return b.OpenSubKey(path, true) ?? b.CreateSubKey(path);
            }
        }
        private static string GetSIDFromUserName(string userName)
        {
            var account = new System.Security.Principal.NTAccount(userName);
            var identifier = (System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier));
            var sid = identifier.Value;
            return sid;
        }
        private static string[] GetAllSystemUsers()
        {
            List<string> names = new List<string>();
            SelectQuery query = new SelectQuery("Win32_UserAccount");
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject envVar in searcher.Get())
            {
                names.Add((string)envVar["Name"]);
            }
            return names.ToArray();
        }
    }
    public enum RegistryQuery
    {
        System = 0,
        CurrentUser = 1,
        AllUsers,
        AllUsersAndSystem
    }
    public enum InstallMode
    {
        System = 0,
        CurrentUser = 1,
    }
    public enum InstallQuery
    {
        System = 0,
        CurrentUser = 1,
        CurrentAndOtherUsers,
        OtherUsers,
        None,
    }
}
