using Clowd.Installer.Features;
using PowerArgs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    internal class Program
    {
        internal static void Elevate(string appDirectory, bool install, Type feature)
        {
            var logFile = PathConstants.GetDatedFilePath("cli_elevated_log", "txt", PathConstants.LogData);

            Log.White($"Starting elevated process, logging to: \"{logFile}\"");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Constants.CurrentExePath;
            var action = install ? nameof(InstallerArgs.AddFeature) : nameof(InstallerArgs.RemoveFeature);
            psi.Arguments = $"{action} {feature.Name} -dir \"{appDirectory}\" -log \"{logFile}\"";
            psi.UseShellExecute = true;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.Verb = "runas";
            var p = Process.Start(psi);
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new OutOfProcessException(Constants.InstallerExeName, p.ExitCode, logFile);
        }

        internal static int Main(string[] args)
        {
            try
            {
                Log.IsConsoleMode = true;

                var t = Args.InvokeAction<InstallerArgs>(args);
                if (t?.Args?.Debug == true)
                {
                    Console.WriteLine();
                    Console.WriteLine("[DEBUG] Press any key to exit.");
                    Console.ReadKey();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Log.Red(ex.ToString());
                Log.Red(ex.Message); // log the message as the last thing before we exit, just makes reading easier.
                Console.WriteLine();
                $"To see help, run '{Path.GetFileName(Constants.CurrentExePath)} -h'".ToYellow().WriteLine();
                //ArgUsage.GenerateUsageFromTemplate<InstallerArgs>().WriteLine();
                return 1;
            }
        }
    }
}
