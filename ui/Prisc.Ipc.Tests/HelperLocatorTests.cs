using System;
using System.IO;
using System.Runtime.Versioning;
using Xunit;

namespace Prisc.Ipc.Tests
{
    /// <summary>
    /// Where the app looks for its helpers (HELPER_PROTOCOL.md §7.1). Every tree lives under the temp
    /// dir, never under the repo, or the walk-up would find the real target/ dir. Only the pure
    /// overload is called: nothing reads or sets process environment.
    /// </summary>
    public sealed class HelperLocatorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PriscLocatorTests", Guid.NewGuid().ToString("N"));

        public HelperLocatorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch { }
        }

        private static readonly string Recorder = Platform.ExeName("prisc_recorder");
        private static readonly string Capture = Platform.ExeName("prisc_capture");
        private static readonly string VideoRender = Platform.ExeName("Prisc.VideoRender");
        private static readonly string Hotkeys = Platform.LibName("prisc_hotkeys");

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            return path;
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Stamp(string path, int minutesAgo)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
            return path;
        }

        [Fact]
        public void File_names_match_the_shipped_payload()
        {
            Assert.Equal(OperatingSystem.IsWindows() ? "prisc_recorder.exe" : "prisc_recorder", Recorder);
            Assert.Equal(OperatingSystem.IsWindows() ? "prisc_hotkeys.dll"
                : OperatingSystem.IsMacOS() ? "libprisc_hotkeys.dylib" : "libprisc_hotkeys.so", Hotkeys);
        }

        [Fact]
        public void Release_layout_is_beside_the_app_then_its_recorder_folder()
        {
            var app = Dir("app");
            var recorder = Touch("app", "recorder", Recorder);
            var capture = Touch("app", Capture);

            Assert.Equal(recorder, HelperLocator.Find(Recorder, null, app));
            Assert.Equal(capture, HelperLocator.Find(Capture, null, app));

            // beside the app wins over recorder/
            var beside = Touch("app", Recorder);
            Assert.Equal(beside, HelperLocator.Find(Recorder, null, app));
        }

        [Fact]
        public void Other_layouts_are_not_found()
        {
            // a differently named payload folder beside the app, and a separate sibling checkout
            Touch("Cargo.toml");
            var app = Dir("app");
            Touch("app", "old-payload", Recorder);
            Touch("sibling-checkout", "target", "debug", Recorder);

            Assert.Null(HelperLocator.Find(Recorder, null, app));
        }

        [Fact]
        public void Dev_layout_takes_the_newer_profile_and_debug_on_a_tie()
        {
            // <repo>/Cargo.toml with the build output; the nested crate manifest (no target/) is
            // walked past
            Touch("repo", "Cargo.toml");
            Touch("repo", "crates", "foo", "Cargo.toml");
            var debug = Stamp(Touch("repo", "target", "debug", Recorder), 10);
            var release = Stamp(Touch("repo", "target", "release", Recorder), 20);
            var app = Dir("repo", "crates", "foo", "bin", "Debug", "net10.0");

            Assert.Equal(debug, HelperLocator.Find(Recorder, null, app));

            Stamp(release, 5);
            Assert.Equal(release, HelperLocator.Find(Recorder, null, app));

            File.SetLastWriteTimeUtc(debug, File.GetLastWriteTimeUtc(release));
            Assert.Equal(debug, HelperLocator.Find(Recorder, null, app));

            File.Delete(debug);
            Assert.Equal(release, HelperLocator.Find(Recorder, null, app));

            File.Delete(release);
            Assert.Null(HelperLocator.Find(Recorder, null, app));
        }

        [Fact]
        public void The_hotkeys_library_is_also_found_in_deps()
        {
            Touch("repo", "Cargo.toml");
            Stamp(Touch("repo", "target", "debug", Hotkeys), 30);
            var fresh = Stamp(Touch("repo", "target", "debug", "deps", Hotkeys), 1);
            var app = Dir("repo", "ui", "bin");

            Assert.Equal(fresh, HelperLocator.Find(Hotkeys, null, app, library: true));
            // an executable is never looked for in deps/
            Touch("repo", "target", "debug", "deps", Recorder);
            Assert.Null(HelperLocator.Find(Recorder, null, app));
        }

        [Fact]
        public void VideoRender_is_found_in_its_project_output_without_a_cargo_manifest()
        {
            var debug = Stamp(Touch("repo", "ui", "Prisc.VideoRender", "bin", "Debug", "net10.0", VideoRender), 10);
            var release = Stamp(Touch("repo", "ui", "Prisc.VideoRender", "bin", "Release", "net10.0", VideoRender), 20);
            var app = Dir("repo", "ui", "Prisc.Ui", "bin", "Debug", "net10.0");

            Assert.Equal(debug, HelperLocator.Find(VideoRender, null, app));
            Stamp(release, 1);
            Assert.Equal(release, HelperLocator.Find(VideoRender, null, app));
        }

        [Fact]
        public void Release_layout_wins_over_a_dev_workspace()
        {
            Touch("repo", "Cargo.toml");
            Touch("repo", "target", "debug", Recorder);
            var app = Dir("repo", "ui", "bin");
            var shipped = Touch("repo", "ui", "bin", "recorder", Recorder);

            Assert.Equal(shipped, HelperLocator.Find(Recorder, null, app));
        }

        [Fact]
        public void The_helpers_dir_is_a_path_list_probed_in_order_before_everything_else()
        {
            var app = Dir("app");
            var shipped = Touch("app", "recorder", Recorder);
            var first = Dir("first");
            var second = Dir("second");
            var inSecond = Touch("second", "recorder", Recorder);
            var renderInFirst = Touch("first", VideoRender);
            var list = String.Join(Path.PathSeparator, first, Path.Combine(_root, "missing"), "", second);

            // the recorder/ subdirectory of a listed directory counts, and a directory without the
            // file is skipped
            Assert.Equal(inSecond, HelperLocator.Find(Recorder, list, app));
            Assert.Equal(renderInFirst, HelperLocator.Find(VideoRender, list, app));

            var inFirst = Touch("first", Recorder);
            Assert.Equal(inFirst, HelperLocator.Find(Recorder, list, app));

            // nothing usable in the list falls through to the app's own layout
            Assert.Equal(shipped, HelperLocator.Find(Recorder, "  ", app));
            Assert.Equal(shipped, HelperLocator.Find(Recorder, Path.Combine(_root, "missing"), app));
        }
    }

    /// <summary>
    /// Restoring the execute bit on the helpers we ship and spawn: the macOS recorder zip has carried
    /// mode 644 on its executables, so unpacking it gave executables that could not be executed.
    /// </summary>
    /// <remarks>The file-mode APIs these use are Unix-only, and <c>Assert.SkipWhen</c> is a call the
    /// platform analyzer cannot read as a guard; the attribute keeps CA1416 quiet, and the skips keep
    /// the tests off Windows.</remarks>
    [UnsupportedOSPlatform("windows")]
    public class EnsureExecutableTests
    {
        private static string WriteTempFile(UnixFileMode mode)
        {
            var path = Path.Combine(Path.GetTempPath(), $"prisc-helper-{Guid.NewGuid():N}");
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, mode);
            return path;
        }

        [Fact]
        public void A_readable_but_non_executable_file_becomes_executable()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bit on Windows");

            // 644, exactly as the recorder zip unpacked.
            var path = WriteTempFile(UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                     UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            try
            {
                Assert.Equal(path, HelperLocator.EnsureExecutable(path));

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

        /// <summary>Execute follows read, so a file only its owner may read becomes one only its owner
        /// may run: restoring the bit the archive dropped, not widening access.</summary>
        [Fact]
        public void Execute_is_granted_only_where_read_already_was()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "no execute bit on Windows");

            var path = WriteTempFile(UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600
            try
            {
                HelperLocator.EnsureExecutable(path);

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
                HelperLocator.EnsureExecutable(path);
                Assert.Equal(SevenFiveFive, File.GetUnixFileMode(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A missing path and a null one are returned as they came: a lookup that found
        /// nothing must not turn into an exception here.</summary>
        [Fact]
        public void Nothing_to_do_is_never_an_error()
        {
            Assert.Null(HelperLocator.EnsureExecutable(null));
            Assert.Equal("", HelperLocator.EnsureExecutable(""));

            var missing = Path.Combine(Path.GetTempPath(), $"prisc-missing-{Guid.NewGuid():N}");
            Assert.Equal(missing, HelperLocator.EnsureExecutable(missing));
        }
    }
}
