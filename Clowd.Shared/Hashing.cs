using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Shared
{
    public static class MD5
    {
        public static string Compute(byte[] input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.MD5Cng(), input, salt);
        }
        public static string Compute(string input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.MD5Cng(), input, salt);
        }
        public static string Compute(SecureString input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.MD5Cng(), input, salt);
        }
    }
    public static class SHA1
    {
        public static string Compute(byte[] input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA1Cng(), input, salt);
        }
        public static string Compute(string input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA1Cng(), input, salt);
        }
        public static string Compute(SecureString input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA1Cng(), input, salt);
        }
    }
    public static class SHA256
    {
        public static string Compute(byte[] input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA256Cng(), input, salt);
        }
        public static string Compute(string input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA256Cng(), input, salt);
        }
        public static string Compute(SecureString input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA256Cng(), input, salt);
        }
    }
    public static class SHA512
    {
        public static string Compute(byte[] input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA512Cng(), input, salt);
        }
        public static string Compute(string input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA512Cng(), input, salt);
        }
        public static string Compute(SecureString input, string salt = "")
        {
            return HashEx.ComputeHash(new System.Security.Cryptography.SHA512Cng(), input, salt);
        }
    }
    internal class HashEx
    {
        internal static string ComputeHash<T>(T alg, byte[] input, string salt = "")
            where T : System.Security.Cryptography.HashAlgorithm
        {
            byte[] saltArray = System.Text.Encoding.UTF8.GetBytes(salt);
            byte[] combine = new byte[input.Length + saltArray.Length];
            System.Buffer.BlockCopy(saltArray, 0, combine, 0, saltArray.Length);
            System.Buffer.BlockCopy(input, 0, combine, saltArray.Length, input.Length);
            byte[] hash = alg.ComputeHash(combine);
            return HashEx.GetHashHex(hash);
        }
        internal static string ComputeHash<T>(T alg, string input, string salt = "")
            where T : System.Security.Cryptography.HashAlgorithm
        {
            byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(salt + input);
            byte[] hash = alg.ComputeHash(inputBytes);
            return HashEx.GetHashHex(hash);
        }
        internal static string ComputeHash<T>(T alg, SecureString input, string salt = "")
            where T : System.Security.Cryptography.HashAlgorithm
        {
            try
            {
                byte[] hash = UnmanagedSecureHash(alg, input, Encoding.UTF8.GetBytes(salt), Encoding.UTF8);
                return GetHashHex(hash);
            }
            catch (Exception e)
            {
                // fallback to unsafe/managed method.
                System.Diagnostics.Debug.WriteLine("Error in UnmanagedSecureHash: " + e.Message);
                return GetHashHex(alg.ComputeHash(Encoding.UTF8.GetBytes(GetUnSafeString(input) + salt)));
            }
        }
        internal unsafe static string GetUnSafeString(SecureString secure)
        {
            IntPtr ptr = System.Runtime.InteropServices.Marshal.SecureStringToBSTR(secure);
            try
            {
                return new string((char*)ptr);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(ptr);
            }
        }
        internal static string GetHashHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2"));
            }
            return sb.ToString();
        }
        internal unsafe static byte[] UnmanagedSecureHash<T>(T impl, SecureString secure, byte[] salt, Encoding encoding)
            where T : System.Security.Cryptography.HashAlgorithm
        {
            if (impl == null) throw new ArgumentNullException("implementation");
            if (secure == null) throw new ArgumentNullException("secureString");

            var alg = typeof(T).GetField("m_hashAlgorithm", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(impl);
            var hwnd = (SafeHandle)alg.GetType().GetField("m_hashHandle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(alg);

            IntPtr clearText = Marshal.SecureStringToBSTR(secure);
            try
            {
                var clearTextBytes = encoding.GetByteCount((char*)clearText, secure.Length);
                var clearTextWithSalt = Marshal.AllocHGlobal(clearTextBytes + salt.Length);
                try
                {
                    //switched the order to salt+pass instead of pass+salt for consistency..
                    //Marshal.Copy(salt, 0, clearTextWithSalt + clearTextBytes, salt.Length);
                    //encoding.GetBytes((char*)clearText, secure.Length, (byte*)clearTextWithSalt, clearTextBytes);
                    Marshal.Copy(salt, 0, clearTextWithSalt, salt.Length);
                    encoding.GetBytes((char*)clearText, secure.Length, (byte*)(clearTextWithSalt + salt.Length), clearTextBytes);
                    Marshal.ZeroFreeBSTR(clearText);
                    clearText = IntPtr.Zero;

                    var error = BCryptHashData(hwnd, clearTextWithSalt, clearTextBytes + salt.Length, 0);
                    if (error != 0)
                        throw new System.Security.Cryptography.CryptographicException(error);
                }
                finally
                {
                    ZeroMemory(clearTextWithSalt, (IntPtr)clearTextBytes);
                    Marshal.FreeHGlobal(clearTextWithSalt);
                }
            }
            finally
            {
                if (clearText != IntPtr.Zero)
                    Marshal.ZeroFreeBSTR(clearText);
            }

            var hash = (byte[])((byte[])typeof(T).GetMethod("HashFinal", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(impl, null)).Clone();
            impl.Initialize();
            return hash;
        }

        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(SafeHandle hHash, IntPtr pbInput, int cbInput, int dwFlags);
        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
        private static extern void ZeroMemory(IntPtr dest, IntPtr size);
    }
}
