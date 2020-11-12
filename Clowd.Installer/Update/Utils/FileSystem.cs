using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace NAppUpdate.Framework.Utils
{
	public static class FileSystem
	{
		public static void CreateDirectoryStructure(string path)
		{
			CreateDirectoryStructure(path, true);
		}

		public static void CreateDirectoryStructure(string path, bool pathIncludeFile)
		{
			string[] paths = path.Split(Path.DirectorySeparatorChar);

			// ignore the last split because its the filename
			int loopCount = paths.Length;
			if (pathIncludeFile)
				loopCount--;

			for (int ix = 0; ix < loopCount; ix++)
			{
				string newPath = paths[0] + @"\";
				for (int add = 1; add <= ix; add++)
					newPath = Path.Combine(newPath, paths[add]);
				if (!Directory.Exists(newPath))
					Directory.CreateDirectory(newPath);
			}
		}

		/// <summary>
		/// Safely delete a folder recuresively
		/// See http://stackoverflow.com/questions/329355/cannot-delete-directory-with-directory-deletepath-true/329502#329502
		/// </summary>
		/// <param name="targetDir">Folder path to delete</param>
		public static void DeleteDirectory(string targetDir)
		{
			string[] files = Directory.GetFiles(targetDir);
			string[] dirs = Directory.GetDirectories(targetDir);

			foreach (string file in files)
			{
				File.SetAttributes(file, FileAttributes.Normal);
				File.Delete(file);
			}

			foreach (string dir in dirs)
			{
				DeleteDirectory(dir);
			}

			File.SetAttributes(targetDir, FileAttributes.Normal);
			Directory.Delete(targetDir, false);
		}

		public static IEnumerable<string> GetFiles(string path, string searchPattern, SearchOption searchOption)
		{
			string[] searchPatterns = searchPattern.Split('|');
			var files = new List<string>();
			foreach (string sp in searchPatterns)
				files.AddRange(System.IO.Directory.GetFiles(path, sp, searchOption));
			return files;
		}

		public static bool IsExeRunning(string path)
		{
			var processes = Process.GetProcesses();
			foreach (Process p in processes)
			{
				if (p.MainModule.FileName.StartsWith(path, StringComparison.InvariantCultureIgnoreCase))
					return true;
			}
			return false;
		}

	}
}
