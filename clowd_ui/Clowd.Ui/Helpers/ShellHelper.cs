using System;
using System.Diagnostics;
using System.IO;

namespace Clowd.UI.Helpers
{
    /// <summary>Small shell helpers for OS interactions Avalonia does not expose directly.</summary>
    public static class ShellHelper
    {
        /// <summary>Reveals a file in the OS file manager with the file selected
        /// (Explorer <c>/select</c> on Windows, Finder <c>open -R</c> on macOS). Falls back to
        /// opening the containing directory where per-file selection is unsupported. All failures
        /// are swallowed (logged) — revealing a file is never critical.</summary>
        public static void RevealFileInFolder(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // explorer requires the "/select,<path>" comma form as a single raw argument;
                    // ArgumentList would insert a space after the comma and break selection.
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-R");
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
                else
                {
                    var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                    if (!String.IsNullOrEmpty(dir))
                        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RevealFileInFolder failed: " + ex);
            }
        }
    }
}
