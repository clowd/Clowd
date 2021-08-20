using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Clowd.PlatformUtil.Windows;

namespace Clowd.Aot
{
    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var verb = args.FirstOrDefault()?.ToLower();
                var rest = args.Skip(1).ToArray();

                switch (verb)
                {
                    case "watch":
                        Watch.Run(rest);
                        break;
                    default:
                        throw new InvalidOperationException($"Verb not recognized '{verb}'.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                var clr = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: " + ex.Message);
                Console.ForegroundColor = clr;
                PrintHelp();
                return 1;
            }
        }

        static void PrintHelp()
        {
            var info = PeVersionInfo.ReadFromFile(Process.GetCurrentProcess().MainModule.FileName);

            Console.WriteLine($@"
{info.FileName} {info.FileVersion}
{info.LegalCopyright}

  Monitor parent process, and kill any children if the parent exits
    watch <parentPid> <childPid> [otherChildPid...]
    
");
        }
    }



}
