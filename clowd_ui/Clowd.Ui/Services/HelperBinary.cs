using System;
using System.IO;

namespace Clowd.UI
{
    /// <summary>
    /// Shared handling for the helper executables Clowd ships beside itself and spawns —
    /// obs-express, vid2gif, clowd_ai, Clowd.VideoRender.
    /// </summary>
    public static class HelperBinary
    {
        /// <summary>
        /// Makes sure <paramref name="path"/> can actually be executed, and returns it unchanged so
        /// it can wrap a resolver's result. No-op on Windows, which has no execute bit.
        /// <para>
        /// The obs-express payload arrives as a zip, and the macOS one carries mode 644 on all
        /// three of its executables — the archive was built somewhere the bit was already lost, so
        /// `obs-express`, `vid2gif` and `obs-ffmpeg-mux` unpack un-runnable and every recording and
        /// GIF export dies at Process.Start with "permission denied". Restoring it here rather than
        /// waiting on the packaging fix costs one stat, keeps working once the payload is fixed, and
        /// covers any other archive that loses the bit on the way to a user's disk.
        /// </para>
        /// <para>
        /// Failure is deliberately swallowed: on a read-only volume, or a file owned by someone
        /// else, there is nothing useful to do here and the spawn that follows reports the real
        /// problem with the real context.
        /// </para>
        /// </summary>
        public static string EnsureExecutable(string path)
        {
            // Two separate guards, not one `||`: the platform analyzer recognizes a bare
            // `if (OperatingSystem.IsWindows()) return;` as proof that the Unix-only file-mode APIs
            // below are unreachable there, and folding it into a compound condition costs that.
            if (OperatingSystem.IsWindows())
                return path;
            if (String.IsNullOrEmpty(path))
                return path;

            try
            {
                var mode = File.GetUnixFileMode(path);

                // whoever can read it can run it — matching how the bit would have survived the
                // archive, rather than granting anything the file did not already offer.
                var wanted = mode;
                if ((mode & UnixFileMode.UserRead) != 0) wanted |= UnixFileMode.UserExecute;
                if ((mode & UnixFileMode.GroupRead) != 0) wanted |= UnixFileMode.GroupExecute;
                if ((mode & UnixFileMode.OtherRead) != 0) wanted |= UnixFileMode.OtherExecute;

                if (wanted != mode)
                    File.SetUnixFileMode(path, wanted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not mark '{path}' executable: {ex.Message}");
            }

            return path;
        }
    }
}
