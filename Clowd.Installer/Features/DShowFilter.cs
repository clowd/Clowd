using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class DShowFilter : IFeature
    {
        private string[] _filters = new string[]
        {
            "loopback-audio-x86.dll",
            "loopback-audio-x64.dll"
        };

        private string _installDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Constants.AppName + "DirectShow");

        public bool CheckInstalled(string assetPath)
        {
            return Directory.Exists(_installDirectory) && Directory.EnumerateFiles(_installDirectory).Any();
        }

        public void Install(string assetPath)
        {
            if (!SystemEx.IsProcessElevated)
            {
                Program.Elevate(true, this.GetType(), assetPath);
                return;
            }

            if (!Directory.Exists(_installDirectory))
                Directory.CreateDirectory(_installDirectory);

            foreach (var f in _filters)
            {
                if (f.EndsWith("x64.dll") && !Environment.Is64BitOperatingSystem)
                    continue; // skip 64 assy on 32 bit systems

                var file = ResourcesEx.WriteResourceToFile(f, _installDirectory);
                var code = regsvr32(true, file);
                if (code != 0)
                {
                    Uninstall(assetPath);
                    throw new Exception("regsvr32 returned non-zero exit code: " + code);
                }
            }
        }

        public bool NeedsPrivileges()
        {
            return true;
        }

        public void Uninstall(string assetPath)
        {
            if (!SystemEx.IsProcessElevated)
            {
                Program.Elevate(false, this.GetType(), assetPath);
                return;
            }

            if (Directory.Exists(_installDirectory))
                foreach (var f in Directory.EnumerateFiles(_installDirectory))
                    if (0 == regsvr32(false, f))
                        File.Delete(f);

            var files = Directory.GetFiles(_installDirectory);
            if (!files.Any())
                Directory.Delete(_installDirectory);
            else
                throw new Exception("regsvr32 unable to uninstall files: \n" + String.Join("\n", files));
        }

        private int regsvr32(bool install, string filepath)
        {
            var uflag = install ? "" : "/u ";
            var p = Process.Start("regsvr32", $"/s {uflag}\"{filepath}\"");
            p.WaitForExit();
            return p.ExitCode;
        }
    }
}
