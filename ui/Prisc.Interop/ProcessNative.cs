using System;

namespace Prisc.Interop
{
    /// <summary>Process-wide native state: the FFmpeg library directory and the package identity
    /// (<c>prisc_process_*</c>).</summary>
    public static unsafe class ProcessNative
    {
        /// <summary>
        /// Makes the FFmpeg libraries in <paramref name="directory"/> loadable by absolute path, on
        /// every OS (<c>prisc_process_prepare_ffmpeg_dir</c>): Windows adds the directory to the DLL
        /// search path (process-wide, last caller wins; empty restores the default) so avcodec's own
        /// imports resolve beside it; macOS loads the closure of the FFmpeg dylibs over their
        /// <c>@rpath</c> dependencies first, since the payload carries no rpath of its own. Returns
        /// the number of images loaded (0 on Windows and Linux, and for a directory without FFmpeg).
        /// Throws <see cref="InteropException"/> for a path with a NUL or when Windows refuses the
        /// directory. Any thread.
        /// </summary>
        public static int PrepareFfmpegDirectory(string directory)
        {
            InteropLibrary.Require();
            byte[] utf8 = Utf8.Bytes(directory ?? String.Empty);
            uint loaded;
            fixed (byte* p = utf8)
            {
                Native.Check(NativeMethods.prisc_process_prepare_ffmpeg_dir(p, (nuint)utf8.Length, &loaded),
                    "prisc_process_prepare_ffmpeg_dir");
            }
            return (int)loaded;
        }

        /// <summary>
        /// The MSIX package full name this process was created under, or null when it has no package
        /// identity (a normal launch). Windows only: elsewhere, and when Windows refuses to answer,
        /// throws <see cref="InteropException"/> (<see cref="InteropStatus.Unsupported"/>,
        /// <see cref="InteropStatus.Os"/> with the code). Any thread.
        /// </summary>
        public static string? PackageFullName()
        {
            InteropLibrary.Require();
            PriscString name = default;
            var status = Native.Check(NativeMethods.prisc_process_package_full_name_windows(&name),
                "prisc_process_package_full_name_windows");
            string value = Utf8.Take(ref name);
            return status == InteropStatus.Ok ? value : null;
        }
    }
}
