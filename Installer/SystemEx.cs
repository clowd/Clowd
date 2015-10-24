using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

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
