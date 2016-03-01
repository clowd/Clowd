using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Clowd.ComServer
{
    [ComVisible(false)]
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 1)
            {
                string input = args[0];
                
                if (String.Equals(input, "install", StringComparison.InvariantCultureIgnoreCase))
                {
                    bool success = AssemblyInstaller.Install();
                    if (!success && !AssemblyInstaller.IsUserAdministrator())
                    {
                        Console.WriteLine("Error: Clowd.ComServer must be ran as administrator");
                        Environment.Exit(1);
                    }
                    Console.WriteLine(success ? "Success" : "Unspecified Error");
                    Environment.Exit(success ? 0 : 1);
                }
                else if (String.Equals(input, "uninstall", StringComparison.InvariantCultureIgnoreCase))
                {
                    bool success = AssemblyInstaller.Uninstall();
                    if (!success && !AssemblyInstaller.IsUserAdministrator())
                    {
                        Console.WriteLine("Error: Clowd.ComServer must be ran as administrator");
                        Environment.Exit(1);
                    }
                    Console.WriteLine(success ? "Success" : "Unspecified Error");
                    Environment.Exit(success ? 0 : 1);
                }
            }

            Console.WriteLine("Error: Clowd.ComServer expects a command when ran from console.");
            Console.WriteLine("Supported Commands are 'install' and 'uninstall'.");
            Environment.Exit(1);
        }
    }
}
