using System;
using System.IO;
using System.Runtime.Versioning;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Restoring the execute bit on the helper binaries we ship and spawn. The case that motivated
    /// it is real and current: the macOS obs-express zip carries mode 644 on <c>obs-express</c>,
    /// <c>vid2gif</c> and <c>obs-ffmpeg-mux</c>, so unpacking it gives you three executables that
    /// cannot be executed.
    /// </summary>
    /// <remarks>The file-mode APIs these use are Unix-only, and <c>Assert.SkipWhen</c> is a call the
    /// platform analyzer cannot read as a guard; the attribute is what keeps CA1416 quiet, and the
    /// skips are what actually keep the tests off Windows.</remarks>
    [UnsupportedOSPlatform("windows")]
    public class HelperBinaryTests
    {
        private static string WriteTempFile(UnixFileMode mode)
        {
            var path = Path.Combine(Path.GetTempPath(), $"clowd-helper-{Guid.NewGuid():N}");
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, mode);
            return path;
        }

        [Fact]
        public void A_readable_but_non_executable_file_becomes_executable()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bit on Windows");

            // 644, exactly as the obs-express zip unpacks.
            var path = WriteTempFile(UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                     UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            try
            {
                Assert.Equal(path, HelperBinary.EnsureExecutable(path));

                var mode = File.GetUnixFileMode(path);
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
                Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
                Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));

                // and nothing else moved: write permission is not ours to grant or take.
                Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
                Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
                Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Execute follows read, so a file only its owner may read becomes one only its
        /// owner may run — restoring the bit the archive dropped, not widening access.</summary>
        [Fact]
        public void Execute_is_granted_only_where_read_already_was()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bit on Windows");

            var path = WriteTempFile(UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600
            try
            {
                HelperBinary.EnsureExecutable(path);

                var mode = File.GetUnixFileMode(path);
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
                Assert.False(mode.HasFlag(UnixFileMode.GroupExecute));
                Assert.False(mode.HasFlag(UnixFileMode.OtherExecute));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void An_already_executable_file_is_left_alone()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bit on Windows");

            const UnixFileMode SevenFiveFive =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            var path = WriteTempFile(SevenFiveFive);
            try
            {
                HelperBinary.EnsureExecutable(path);
                Assert.Equal(SevenFiveFive, File.GetUnixFileMode(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A missing path and a null one are returned as they came: a locator that found
        /// nothing must not turn into an exception here, and the caller's own "not found" message
        /// is the one worth showing.</summary>
        [Fact]
        public void Nothing_to_do_is_never_an_error()
        {
            Assert.Null(HelperBinary.EnsureExecutable(null));
            Assert.Equal("", HelperBinary.EnsureExecutable(""));

            var missing = Path.Combine(Path.GetTempPath(), $"clowd-missing-{Guid.NewGuid():N}");
            Assert.Equal(missing, HelperBinary.EnsureExecutable(missing));
        }
    }
}
