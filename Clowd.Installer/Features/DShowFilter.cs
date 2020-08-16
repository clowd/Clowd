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
        public static string InstallDirectory => Path.Combine(RegistryEx.GetInstallPath(InstallMode.System), Constants.DirectShowAppName);

        public static DirectShowFilterInfo[] Filters => new DirectShowFilterInfo[]
        {
            new DirectShowFilterInfo(DirectShowFilterType.Audio, "clowd-audio-capturer", "loopback-audio-x86.dll", true),
            new DirectShowFilterInfo(DirectShowFilterType.Audio, "clowd-audio-capturer", "loopback-audio-x64.dll", true),
            new DirectShowFilterInfo(DirectShowFilterType.Video, "UScreenCapture", "UScreenCapture-x86.ax", false),
            new DirectShowFilterInfo(DirectShowFilterType.Video, "UScreenCapture", "UScreenCapture-x64.ax", false),
        };

        public static DirectShowFilterInfo DefaultVideo => Filters.OrderBy(f => f.IsLatest).FirstOrDefault(f => f.FilterType == DirectShowFilterType.Video && f.IsInstalled);

        public static DirectShowFilterInfo DefaultAudio => Filters.OrderBy(f => f.IsLatest).FirstOrDefault(f => f.FilterType == DirectShowFilterType.Audio && f.IsInstalled);

        public bool CheckInstalled(string assetPath)
        {
            return Directory.Exists(InstallDirectory) && Directory.EnumerateFiles(InstallDirectory).Any();
        }

        public void Install(string assetPath)
        {
            if (!SystemEx.IsProcessElevated)
            {
                Program.Elevate(true, this.GetType(), assetPath);
                return;
            }

            if (!Directory.Exists(InstallDirectory))
                Directory.CreateDirectory(InstallDirectory);

            StringBuilder revsvrUninstallCommands = new StringBuilder();

            foreach (var filterInfo in Filters)
            {
                // check if we need to uninstall anything
                if (!filterInfo.IsLatest)
                {
                    if (filterInfo.FileExists)
                    {
                        if (filterInfo.IsInstalled)
                        {
                            regsvr32(false, filterInfo.FilePath);
                        }

                        File.Delete(filterInfo.FilePath);
                    }
                    continue;
                }

                var f = filterInfo.ResourceName;

                if (f.Contains("x64") && !Environment.Is64BitOperatingSystem)
                    continue; // skip 64 assy on 32 bit systems

                var filterFilePath = ResourcesEx.WriteResourceToFile(f, InstallDirectory);
                var code = regsvr32(true, filterFilePath);
                if (code != 0)
                {
                    Uninstall(assetPath);
                    throw new Exception("regsvr32 returned non-zero exit code: " + code);
                }

                revsvrUninstallCommands.AppendLine(regsvr32_command(false, filterFilePath));
            }

            //This works great, but it might be a good idea to move the cd command to the start (this insures that the path is also available to the elevated script -
            // otherwise the elevated script just runs from system32). You should also redirect the net command to nul to hide it's output: net session >nul 2>&1

            var unstallFileText = $@"
NET SESSION
IF %ERRORLEVEL% NEQ 0 GOTO ELEVATE
GOTO ADMINTASKS

:ELEVATE
CD /d %~dp0
MSHTA ""javascript: var shell = new ActiveXObject('shell.application'); shell.ShellExecute('%~nx0', '', '', 'runas', 1);close();""
EXIT

:ADMINTASKS
{revsvrUninstallCommands.ToString()}

reg delete ""{RegistryEx.GetRegistryHiveName(InstallMode.System)}\{Constants.UninstallRegistryPath}\{Constants.DirectShowAppName}"" /f /reg:32
reg delete ""{RegistryEx.GetRegistryHiveName(InstallMode.System)}\{Constants.UninstallRegistryPath}\{Constants.DirectShowAppName}"" /f /reg:64

reg delete ""{RegistryEx.GetRegistryHiveName(InstallMode.System)}\SOFTWARE\UNREAL"" /f /reg:32
reg delete ""{RegistryEx.GetRegistryHiveName(InstallMode.System)}\SOFTWARE\UNREAL"" /f /reg:64

start /b """" cmd /c rd /s /q ""%~dp0"" & msg * /self /w ""Uninstallation of {Constants.DirectShowAppName} has been successful""";

            var uninstallFilePath = Path.Combine(InstallDirectory, "uninstall.bat");

            File.WriteAllText(uninstallFilePath, unstallFileText);
            File.WriteAllText(Path.Combine(InstallDirectory, "readme.txt"), "Do not delete the files in this directory without first running the 'uninstall.bat' file." +
                "\r\nIt will unregister the assemblies from COM and then this folder can be safely deleted.");

            // write icon for uninstall programs list
            var programIcon = ResourcesEx.WriteResourceToFile("default.ico", InstallDirectory);

            var info = new ControlPanelInfo()
            {
                DisplayName = Constants.DirectShowAppName,
                UninstallString = uninstallFilePath,
                InstallDirectory = InstallDirectory,
                DisplayIconPath = programIcon,
            };

            ControlPanelInfo.Install(Constants.DirectShowAppName, info, InstallMode.System);
        }

        public bool NeedsPrivileges()
        {
            return true;
        }

        public void Uninstall(string assetPath)
        {
            if (!CheckInstalled(assetPath))
                return;

            if (!SystemEx.IsProcessElevated)
            {
                Program.Elevate(false, this.GetType(), assetPath);
                return;
            }

            string[] getBinaries() => Directory.GetFiles(InstallDirectory).Where(f => f.EndsWith("dll") || f.EndsWith("ax")).ToArray();

            if (Directory.Exists(InstallDirectory))
                foreach (var f in getBinaries())
                    if (0 == regsvr32(false, f))
                        File.Delete(f);

            var files = getBinaries();
            if (!files.Any())
                Directory.Delete(InstallDirectory, true);
            else
                throw new Exception("regsvr32 unable to uninstall files: \n" + String.Join("\n", files));

            ControlPanelInfo.Uninstall(Constants.DirectShowAppName, RegistryQuery.System);
            UScreen.DeleteProperties();
        }

        private int regsvr32(bool install, string filepath)
        {
            var uflag = install ? "" : "/u ";
            var p = Process.Start("regsvr32", $"/s {uflag}\"{filepath}\"");
            p.WaitForExit();
            return p.ExitCode;
        }

        private string regsvr32_command(bool install, string filepath)
        {
            var uflag = install ? "" : "/u ";
            return $"regsvr32 /s {uflag}\"{filepath}\"";
        }
    }
}
