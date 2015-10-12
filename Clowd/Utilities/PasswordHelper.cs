using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Clowd.Shared;

namespace Clowd.Utilities
{
    public static class PasswordHelper
    {
        private const string ClientSalt = "29AcyQyeqJsQJLCt";
        public static string GetHashFromPassword(string password)
        {
            return MD5.Compute(password, ClientSalt);
        }
    }
}
