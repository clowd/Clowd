using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management;

namespace Clowd.Installer
{
    public class RegistryEx
    {
        public static string GetInstallPath(InstallMode mode)
        {
            if (mode == InstallMode.System)
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (mode == InstallMode.System)
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            throw new InvalidOperationException();
        }
        public static RegistryKey[] GetClassesRoot(RegistryQuery location)
        {
            var path = @"Software\Classes\";
            return OpenKeysFromRootPath(path, location);
        }
        public static RegistryKey[] OpenKeysFromRootPath(string path, RegistryQuery location, bool writable = true)
        {
            List<RegistryKey> keys = new List<RegistryKey>();
            if (location == RegistryQuery.AllUsers || location == RegistryQuery.AllUsersAndSystem)
            {
                foreach (var user in GetAllSystemUsers())
                {
                    var sid = GetSIDFromUserName(user);
                    var k = Registry.Users.OpenSubKey(sid + "\\" + path, writable);
                    if (k != null)
                        keys.Add(k);
                }
            }
            else if (location == RegistryQuery.CurrentUser)
            {
                var k = Registry.CurrentUser.OpenSubKey(path, writable);
                if (k != null)
                    keys.Add(k);
            }
            if (location == RegistryQuery.System || location == RegistryQuery.AllUsersAndSystem)
            {
                var k = Registry.LocalMachine.OpenSubKey(path, writable);
                if (k != null)
                    keys.Add(k);
            }
            return keys.ToArray();
        }
        public static RegistryKey CreateKeyFromRootPath(string path, InstallMode location)
        {
            if (location == InstallMode.CurrentUser)
            {
                var k = Registry.CurrentUser.OpenSubKey(path, true) ?? Registry.CurrentUser.CreateSubKey(path);
                return k;
            }
            else if (location == InstallMode.System)
            {
                var k = Registry.LocalMachine.OpenSubKey(path, true) ?? Registry.LocalMachine.CreateSubKey(path);
                return k;
            }
            throw new InvalidOperationException();
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
