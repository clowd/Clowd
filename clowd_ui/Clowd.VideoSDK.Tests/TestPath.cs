using System;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Rewrites the Windows-shaped absolute paths these tests were written with into the running
    /// OS's own shape: <c>C:\media\clip.mp4</c> becomes <c>/media/clip.mp4</c> off Windows.
    ///
    /// Only the tests whose assertions go *through* path parsing need it. <see
    /// cref="System.IO.Path.GetFileNameWithoutExtension"/> and friends treat <c>\</c> as a
    /// separator on Windows only, so a literal <c>C:\media\clip.mp4</c> names a track "clip" there
    /// and "C:\media\clip" on macOS — a difference in the test's input, not in the code under test.
    /// Paths the tests only carry around opaquely are left as they are.
    /// </summary>
    internal static class TestPath
    {
        /// <summary>The given rooted Windows path, in the running OS's own shape.</summary>
        public static string Native(string windowsPath)
        {
            if (OperatingSystem.IsWindows() || String.IsNullOrEmpty(windowsPath))
                return windowsPath;

            // strip the "C:" drive prefix if present, then swap the separators.
            var rest = windowsPath.Length >= 2 && windowsPath[1] == ':'
                ? windowsPath.Substring(2)
                : windowsPath;
            return rest.Replace('\\', '/');
        }
    }
}
