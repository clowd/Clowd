using System;

namespace Prisc.Ipc
{
    /// <summary>How a helper's stem becomes a file name on this platform.</summary>
    public static class Platform
    {
        /// <summary>The executable for <paramref name="stem"/>: <c>stem.exe</c> on Windows, the bare
        /// stem elsewhere.</summary>
        public static string ExeName(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

        /// <summary>The native library for <paramref name="stem"/>: <c>stem.dll</c>,
        /// <c>libstem.dylib</c> or <c>libstem.so</c>.</summary>
        public static string LibName(string stem) =>
            OperatingSystem.IsWindows() ? stem + ".dll"
            : OperatingSystem.IsMacOS() ? "lib" + stem + ".dylib"
            : "lib" + stem + ".so";
    }
}
