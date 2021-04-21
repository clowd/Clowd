using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer
{
    public class OutOfProcessException : Exception
    {
        public override string Message
        {
            get
            {
                try
                {
                    return $"{ProcessName} failed. (exit code {ExitCode}){Environment.NewLine}Last error:{File.ReadLines(LogPath).Last()}{Environment.NewLine}For more information: \"{LogPath}\"";
                }
                catch
                {
                    return $"{ProcessName} failed. (exit code {ExitCode}){Environment.NewLine}For more information: \"{LogPath}\"";
                }
            }
        }

        public string ProcessName { get; }
        public int ExitCode { get; }
        public string LogPath { get; }

        public OutOfProcessException(string processName, int exitCode, string logPath) : base()
        {
            ProcessName = processName;
            ExitCode = exitCode;
            LogPath = logPath;
        }
    }
}
