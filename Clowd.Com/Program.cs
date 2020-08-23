using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Clowd.Com
{
    [ComVisible(false)]
    public class Program
    {
        internal static int Main(string[] args)
        {
            bool install = true;

            if (install)
            {
                var success = InstallCOMTypes();
                Console.WriteLine("Installed: " + success); ;
            }
            else
            {
                var asuccess = UninstallCOMTypes();
                Console.WriteLine("Uninstalled: " + asuccess);
            }

            Console.WriteLine();
            Console.WriteLine();

            //var filter = new AudioCaptureFilter();
            //DirectShow.AMMediaType mt = null;
            //filter.Pins[0].GetMediaType(0, ref mt);

            Console.WriteLine("Video - ");
            try
            {
                DSCategory cat = new DSCategory(new Guid(AMovieSetup.CLSID_VideoInputDeviceCategory));
                foreach (var inputDevice in cat)
                {
                    Console.WriteLine($"{inputDevice.Name}");
                    Console.WriteLine($"{inputDevice.DevicePath}");
                    Console.WriteLine("------");
                }
            }
            catch
            {
                Console.WriteLine("Could not enumerate video devices (or none found)");
            }
            Console.WriteLine();
            Console.WriteLine("Audio - ");
            try
            {
                var cat = new DSCategory(new Guid(AMovieSetup.CLSID_AudioInputDeviceCategory));
                foreach (var inputDevice in cat)
                {
                    Console.WriteLine($"{inputDevice.Name}");
                    Console.WriteLine($"{inputDevice.DevicePath}");
                    Console.WriteLine("------");
                }
            }
            catch
            {
                Console.WriteLine("Could not enumerate audio devices (or none found)");
            }

            Console.ReadKey();

            return 0;
            //return 0;
        }

        //[DllExport("DllRegisterServer", CallingConvention.StdCall)]
        //public static int DllRegisterServer()
        //{
        //    return InstallCOMTypes() ? 0 : -1;
        //}

        //[DllExport("DllUnregisterServer", CallingConvention.StdCall)]
        //public static int DllUnregisterServer()
        //{
        //    return UninstallCOMTypes() ? 0 : -1;
        //}

        public static bool InstallCOMTypes()
        {
            RegistrationServices regService = new RegistrationServices();
            return regService.RegisterAssembly(typeof(Program).Assembly, AssemblyRegistrationFlags.SetCodeBase);
        }

        public static bool UninstallCOMTypes()
        {
            RegistrationServices regService = new RegistrationServices();
            return regService.UnregisterAssembly(typeof(Program).Assembly);
        }

        public static bool InstallCOMTypesRegAsm()
        {
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = @"C:\Windows\Microsoft.NET\Framework\v2.0.50727\RegAsm.exe",
                UseShellExecute = true,
                CreateNoWindow = false,
                Arguments = $"\"{Assembly.GetExecutingAssembly().Location}\" /nologo /codebase /tlb: {Assembly.GetExecutingAssembly().GetName().Name}.tlb",
            };
            if (System.Environment.OSVersion.Version.Major >= 6)
            {
                psi.Verb = "runas";
            }
            var process = Process.Start(psi);
            process.WaitForExit();
            return CheckCOMRegistered();
        }

        public static bool UninstallCOMTypesRegAsm()
        {
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = @"C:\Windows\Microsoft.NET\Framework\v2.0.50727\RegAsm.exe",
                UseShellExecute = true,
                CreateNoWindow = false,
                Arguments = $"/unregister /nologo \"{Assembly.GetExecutingAssembly().Location}\"",
            };
            if (System.Environment.OSVersion.Version.Major >= 6)
            {
                psi.Verb = "runas";
            }
            var process = Process.Start(psi);
            process.WaitForExit();
            string tlb = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".tlb";
            if (File.Exists(tlb))
                File.Delete(tlb);
            return !CheckCOMRegistered();
        }


        public static bool CheckCOMRegistered()
        {
            DSCategory cat = new DSCategory(new Guid(AMovieSetup.CLSID_VideoInputDeviceCategory));
            foreach (var inputDevice in cat)
            {
                if (inputDevice.Filter != null)
                {
                    if (inputDevice.Filter.Name.Equals(VideoCaptureFilter.FRIENDLY_NAME, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        public static bool IsUserAdministrator()
        {
            //bool value to hold our return value
            bool isAdmin;
            try
            {
                //get the currently logged in user
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException ex)
            {
                isAdmin = false;
            }
            catch (Exception ex)
            {
                isAdmin = false;
            }
            return isAdmin;
        }
    }
}
