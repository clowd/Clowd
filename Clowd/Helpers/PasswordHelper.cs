using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Clowd.Shared;

namespace Clowd.Helpers
{
    public static class PasswordHelper
    {
        private const string ClientSalt = "29AcyQyeqJsQJLCt";
        private const string RegistryKey = @"SOFTWARE\Clowd";
        public static Tuple<string, string> GetSavedUserAndHash()
        {

            var clowd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey);
            var user = clowd.GetValue("Username")?.ToString();
            var pass = clowd.GetValue("PasswordHash")?.ToString();
            clowd.Close();
            if (String.IsNullOrWhiteSpace(user) || String.IsNullOrWhiteSpace(pass))
                return null;
            return new Tuple<string, string>(user, pass);
        }
        public static void SaveUserAndHash(string username, string hash)
        {
            var clowd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey);
            clowd.SetValue("Username", username);
            clowd.SetValue("PasswordHash", hash);
        }
        public static string GetHashFromPassword(string password)
        {
            return MD5.Compute(password, ClientSalt);
        }
        public static void DeleteSavedLoginDetails()
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryKey);
        }
    }
}
