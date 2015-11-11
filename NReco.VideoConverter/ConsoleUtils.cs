using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NReco.VideoConverter
{
	internal class ConsoleUtils
	{
		internal const int CTRL_C_EVENT = 0;

		public ConsoleUtils()
		{
		}

		[DllImport("kernel32.dll", CharSet=CharSet.None, ExactSpelling=false, SetLastError=true)]
		internal static extern bool AttachConsole(uint dwProcessId);

		[DllImport("kernel32.dll", CharSet=CharSet.None, ExactSpelling=true, SetLastError=true)]
		internal static extern bool FreeConsole();

		[DllImport("kernel32.dll", CharSet=CharSet.None, ExactSpelling=false)]
		internal static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

		internal static bool SendConsoleCtrlC(Process p, string ffmpegToolPath)
		{
			bool flag;
			uint id = (uint)p.Id;
			if (ConsoleUtils.AttachConsole(id))
			{
				ConsoleUtils.SetConsoleCtrlHandler(null, true);
				try
				{
					if (ConsoleUtils.GenerateConsoleCtrlEvent(0, 0))
					{
						return true;
					}
					else
					{
						flag = false;
					}
				}
				finally
				{
					ConsoleUtils.FreeConsole();
					ConsoleUtils.SetConsoleCtrlHandler(null, false);
				}
				return flag;
			}
			bool windowWidth = false;
			try
			{
				windowWidth = Console.WindowWidth > 0;
			}
			catch
			{
			}
			if (!windowWidth)
			{
				return false;
			}
			ProcessStartInfo processStartInfo = new ProcessStartInfo(Path.Combine(ffmpegToolPath, "NReco.VideoConverter.SendCtrlC.exe"), id.ToString())
			{
				WindowStyle = ProcessWindowStyle.Hidden,
				CreateNoWindow = true,
				UseShellExecute = false,
				WorkingDirectory = ffmpegToolPath
			};
			Process process = Process.Start(processStartInfo);
			process.WaitForExit();
			return process.ExitCode == 0;
		}

		[DllImport("kernel32.dll", CharSet=CharSet.None, ExactSpelling=false)]
		private static extern bool SetConsoleCtrlHandler(ConsoleUtils.ConsoleCtrlDelegate HandlerRoutine, bool Add);

		private delegate bool ConsoleCtrlDelegate(uint CtrlType);
	}
}