using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Shared
{
    public static class RandomEx
    {
        private static System.Random _r = new System.Random();

        public static int GetRandomInteger()
        {
            return _r.Next();
        }
        public static int GetRandomInteger(int max)
        {
            return _r.Next(max);
        }
        public static int GetRandomInteger(int min, int max)
        {
            return _r.Next(min, max);
        }
        public static string GetCryptoUniqueString(int maxSize)
        {
            char[] chars = new char[62];
            chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();
            byte[] data = new byte[1];
            RNGCryptoServiceProvider crypto = new RNGCryptoServiceProvider();
            crypto.GetNonZeroBytes(data);
            data = new byte[maxSize];
            crypto.GetNonZeroBytes(data);
            StringBuilder result = new StringBuilder(maxSize);
            foreach (byte b in data)
            {
                result.Append(chars[b % ((((((((chars.Length))))))))]); //courtesy of Timwi
            }
            return result.ToString();
        }
    }
}
