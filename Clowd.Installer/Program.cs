using Clowd.Installer.Features;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    internal class Program
    {
        internal static bool CanElevate { get; private set; } = true;
        internal static void Elevate(bool install, Type feature, string asset)
        {
            if (!CanElevate)
                throw new Exception("Elevation is required, please restart the process as Administrator");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Assembly.GetExecutingAssembly().Location;
            var inst = install ? "install" : "uninstall";
            psi.Arguments = $"{inst} {feature.Name} \"{asset}\"";
            psi.UseShellExecute = true;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            psi.Verb = "runas";
            var p = Process.Start(psi);
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new Exception("clowd_install.exe exited with non zero exit code: " + p.ExitCode);
        }
        internal static int Main(string[] args)
        {
            // we should only auto-elevate if running inside clowd and not from command line
            CanElevate = false;

            var types = new Type[] {
                typeof(AutoStart),
                typeof(ContextMenu),
                typeof(ControlPanel),
                typeof(DShowFilter),
                typeof(Shortcuts),
            };

            Console.WriteLine("Usage: clowd_install.exe [i|u] feature_name asset_path");
            Console.WriteLine("Valid feature_name:");
            Console.WriteLine("  - all");
            foreach (var t in types)
                Console.WriteLine("  - " + t.Name);

            Console.WriteLine("Example: clowd_install.exe i shortcuts \"C:\\Clowd\\Clowd.exe\"");

            try
            {
                var cmode = args[0];
                var cfeat = args[1];
                var asset = args.Length > 2 ? args[2] : null;

                Type[] features;
                if (cfeat.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    features = types;
                    if (cmode.Equals("i", StringComparison.OrdinalIgnoreCase) || cmode.Equals("install", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("uninstall is the only supported operation for the [all] feature");
                }
                else
                {
                    features = new[] { types.Single(s => s.Name.Equals(cfeat, StringComparison.OrdinalIgnoreCase)) };
                }

                foreach (var f in features)
                {
                    var inst = (IFeature)Activator.CreateInstance(f);

                    if (cmode.Equals("i", StringComparison.OrdinalIgnoreCase) || cmode.Equals("install", StringComparison.OrdinalIgnoreCase))
                    {
                        inst.Install(asset);
                        Console.WriteLine("Success - Installed " + f.Name);
                    }
                    else if (cmode.Equals("u", StringComparison.OrdinalIgnoreCase) || cmode.Equals("uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        inst.Uninstall(asset);
                        Console.WriteLine("Success - Uninstalled " + f.Name);
                    }
                    else
                    {
                        throw new Exception("Unknown mode: " + cmode);
                    }
                }

                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("An error has occurred:");
                Console.WriteLine(e.ToString());
                return 1;
            }
        }
    }
}
