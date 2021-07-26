using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Clowd.Installer
{
    public static class SystemEx
    {
        private readonly static bool _isWindowsNT = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsWindowsVistaOrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 0, 0); }
        }
        public static bool IsWindows7OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 0, 7600); }
        }
        public static bool IsWindows8OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(6, 2, 0); }
        }
        //this will only return true if the correct manifest is included (which it is not right now 2015-10-12)
        public static bool IsWindows10OrLater
        {
            get { return _isWindowsNT && Environment.OSVersion.Version >= new Version(10, 0, 0); }
        }

        public static bool IsProcessElevated
        {
            get
            {
                return WindowsIdentity.GetCurrent().Owner
                  .IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            }
        }

        public static bool IsEnvironmentUserInteractive => Environment.UserInteractive;

        public static bool IsConsoleAttached => GetConsoleWindow() != IntPtr.Zero;

        public static bool CheckClowdConnection()
        {
            int desc;
            var igc = InternetGetConnectedState(out desc, 0);
            if (!igc)
                return false;
            return CheckPingHost(Constants.ServiceDomain);
        }
        public static bool CheckInternetConnected()
        {
            int desc;
            var igc = InternetGetConnectedState(out desc, 0);
            if (!igc)
                return false;

            return CheckPingHost("google.com");
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, int dwDesiredAccess, int dwShareMode, IntPtr SecurityAttributes, int dwCreationDisposition, int dwFlagsAndAttributes, IntPtr hTemplateFile);

        private static SafeFileHandle GetFileHandle(string dirName)
        {
            const int FILE_ACCESS_NEITHER = 0;
            const int FILE_SHARE_READ = 1;
            const int FILE_SHARE_WRITE = 2;
            const int CREATION_DISPOSITION_OPEN_EXISTING = 3;
            const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            return CreateFile(dirName, FILE_ACCESS_NEITHER, (FILE_SHARE_READ | FILE_SHARE_WRITE), System.IntPtr.Zero, CREATION_DISPOSITION_OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, System.IntPtr.Zero);
        }

        private static BY_HANDLE_FILE_INFORMATION? GetFileInfo(SafeFileHandle directoryHandle)
        {
            BY_HANDLE_FILE_INFORMATION objectFileInfo;
            if ((directoryHandle == null) || (!GetFileInformationByHandle(directoryHandle.DangerousGetHandle(), out objectFileInfo)))
            {
                return null;
            }
            return objectFileInfo;
        }

        //public static bool IsDirectoryEqual(string dirName1, string dirName2)
        //{
        //    if (dirName1 == null && dirName2 == null) // both null
        //        return true;

        //    if (dirName1 == null || dirName2 == null) // only one of them is null
        //        return false;

        //    return string.Equals(dirName1.Trim('\\'), dirName2.Trim('\\'), StringComparison.OrdinalIgnoreCase);
        //}

        public static bool AreFileSystemObjectsEqual(string dirName1, string dirName2)
        {
            if (dirName1 == null && dirName2 == null) // both null
                return true;

            if (dirName1 == null || dirName2 == null) // only one of them is null
                return false;

            //return string.Equals(dirName1.Trim('\\'), dirName2.Trim('\\'), StringComparison.OrdinalIgnoreCase);

            // string comparison first to eliminate simple cases
            bool bRet = string.Equals(dirName1, dirName2, StringComparison.OrdinalIgnoreCase);
            if (bRet)
                return true;

            // NOTE: we cannot lift the call to GetFileHandle out of this routine, because we _must_
            // have both file handles open simultaneously in order for the objectFileInfo comparison
            // to be guaranteed as valid.
            using (SafeFileHandle directoryHandle1 = GetFileHandle(dirName1), directoryHandle2 = GetFileHandle(dirName2))
            {
                BY_HANDLE_FILE_INFORMATION? objectFileInfo1 = GetFileInfo(directoryHandle1);
                BY_HANDLE_FILE_INFORMATION? objectFileInfo2 = GetFileInfo(directoryHandle2);
                bRet = objectFileInfo1 != null
                       && objectFileInfo2 != null
                       && (objectFileInfo1.Value.FileIndexHigh == objectFileInfo2.Value.FileIndexHigh)
                       && (objectFileInfo1.Value.FileIndexLow == objectFileInfo2.Value.FileIndexLow)
                       && (objectFileInfo1.Value.VolumeSerialNumber == objectFileInfo2.Value.VolumeSerialNumber);
            }

            return bRet;
        }

        internal static bool CheckPingHost(string host)
        {
            try
            {
                var myPing = new System.Net.NetworkInformation.Ping();
                byte[] buffer = new byte[32];
                int timeout = 1000;
                var pingOptions = new System.Net.NetworkInformation.PingOptions();
                var reply = myPing.Send(host, timeout, buffer, pingOptions);
                return (reply.Status == System.Net.NetworkInformation.IPStatus.Success);
            }
            catch (Exception)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("wininet.dll")]
        private extern static bool InternetGetConnectedState(out int Description, int ReservedValue);
    }
}
