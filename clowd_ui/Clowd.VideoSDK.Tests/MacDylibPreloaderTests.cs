using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Clowd.VideoSDK;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The Mach-O reader behind the macOS dylib preload, checked against <c>otool</c> — the only
    /// oracle worth having here, since the whole point of the reader is to agree with what dyld
    /// will do. Every test skips off macOS, and the ones that need <c>otool</c> skip without it.
    /// </summary>
    /// <remarks><see cref="SupportedOSPlatform"/> keeps CA1416 off the macOS-only calls; xUnit
    /// reaches these by reflection, so the skips are what actually guard them elsewhere.</remarks>
    [SupportedOSPlatform("macos")]
    public class MacDylibPreloaderTests
    {
        /// <summary>Every dylib shipped beside the test binary. Avalonia's and SkiaSharp's natives
        /// are real, signed, universal Mach-O files that genuinely use <c>@rpath</c>, which makes
        /// them a far better corpus than anything worth hand-building.</summary>
        private static string[] NativeLibraries() =>
            Directory.Exists(AppContext.BaseDirectory)
                ? Directory.GetFiles(AppContext.BaseDirectory, "*.dylib", SearchOption.AllDirectories)
                : Array.Empty<string>();

        /// <summary>
        /// Every library <c>otool -l</c> says the file links against, or null when otool is not
        /// usable here.
        /// <para>
        /// Deliberately <c>-l</c> and not the friendlier <c>-L</c>: <c>-L</c>'s first line is the
        /// file's own <c>LC_ID_DYLIB</c> install name, not a dependency, and it is indistinguishable
        /// from one in that output. Several of the dylibs shipped here are copies of a versioned
        /// original and so name themselves <c>@rpath/…</c> — reading <c>-L</c> naively makes a
        /// library look like it depends on itself, which is exactly the entry a preloader must not
        /// act on. <c>-l</c> names the load command, so the distinction survives.
        /// </para>
        /// </summary>
        private static List<string> OtoolDependencies(string path)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("/usr/bin/otool", $"-l \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (proc == null)
                    return null;

                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30_000);
                if (proc.ExitCode != 0)
                    return null;

                var names = new List<string>();
                bool inLoadCommand = false;
                foreach (var raw in stdout.Split('\n'))
                {
                    var line = raw.Trim();

                    if (line.StartsWith("cmd ", StringComparison.Ordinal))
                    {
                        var cmd = line.Substring(4).Trim();
                        inLoadCommand = cmd is "LC_LOAD_DYLIB" or "LC_LOAD_WEAK_DYLIB" or "LC_REEXPORT_DYLIB";
                        continue;
                    }

                    if (!inLoadCommand || !line.StartsWith("name ", StringComparison.Ordinal))
                        continue;

                    inLoadCommand = false;

                    // "name @rpath/libFoo.dylib (offset 24)"
                    var value = line.Substring(5).Trim();
                    int paren = value.LastIndexOf(" (offset ", StringComparison.Ordinal);
                    if (paren > 0)
                        value = value.Substring(0, paren);

                    names.Add(value);
                }
                return names;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The reader agrees with otool about what every native library we ship links against.
        ///
        /// Compared on the whole dependency list rather than the <c>@rpath</c> subset, because none
        /// of these libraries happens to have an <c>@rpath</c> dependency — a comparison narrowed to
        /// those would be two empty lists agreeing, and would pass just as well against a reader
        /// that never returned anything. The full list is 4 to 30 entries per file and exercises the
        /// part that can actually be wrong: walking the load commands, and picking the right slice
        /// of a universal binary. The <c>@rpath</c> filtering on top of it is checked below.
        ///
        /// otool prints the commands of every architecture slice while the reader looks at one, so
        /// the comparison is on the distinct set.
        /// </summary>
        [Fact]
        public void Reader_agrees_with_otool_on_every_native_library_we_ship()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");

            var libraries = NativeLibraries();
            Assert.SkipWhen(libraries.Length == 0, "no native dylibs beside the test binary");

            int compared = 0, totalDependencies = 0;
            foreach (var library in libraries)
            {
                var expected = OtoolDependencies(library);
                if (expected == null)
                    continue; // otool unavailable or refused the file; nothing to compare against

                var actual = MacDylibPreloader.ReadDependencies(library);

                Assert.Equal(
                    expected.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    actual.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());

                compared++;
                totalDependencies += expected.Count;
            }

            Assert.SkipWhen(compared == 0, "otool is not available to compare against");
            Assert.True(totalDependencies > 0,
                $"compared {compared} libraries and none had any dependency — the comparison proved nothing");
        }

        /// <summary>
        /// The <c>@rpath</c> view is the full list filtered and unprefixed, and it drops the file's
        /// own <c>LC_ID_DYLIB</c> install name. That last part is the one worth a test: several of
        /// the dylibs here are copies of a versioned original, so their install name <i>is</i> an
        /// <c>@rpath</c> string, and a reader that counted it would have the preloader load a
        /// library in order to satisfy that same library.
        /// </summary>
        [Fact]
        public void The_rpath_view_filters_the_full_list_and_never_includes_the_files_own_name()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");

            var libraries = NativeLibraries();
            Assert.SkipWhen(libraries.Length == 0, "no native dylibs beside the test binary");

            const string prefix = "@rpath/";
            bool sawSelfNamingLibrary = false;

            foreach (var library in libraries)
            {
                var all = MacDylibPreloader.ReadDependencies(library);
                var rpath = MacDylibPreloader.ReadRpathDependencies(library).ToList();

                Assert.Equal(
                    all.Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
                       .Select(d => d.Substring(prefix.Length)).ToList(),
                    rpath);

                // libuiohook.dylib is a copy of libuiohook.1.dylib and names itself that way.
                var installName = OtoolInstallName(library);
                if (installName == null || !installName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                sawSelfNamingLibrary = true;
                Assert.DoesNotContain(installName.Substring(prefix.Length), rpath);
            }

            Assert.True(sawSelfNamingLibrary,
                "no library here names itself @rpath/…, so the LC_ID_DYLIB exclusion went untested");
        }

        /// <summary>The file's own <c>LC_ID_DYLIB</c> name, or null when it has none.</summary>
        private static string OtoolInstallName(string path)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("/usr/bin/otool", $"-D \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (proc == null)
                    return null;

                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30_000);
                if (proc.ExitCode != 0)
                    return null;

                // "<path>:\n<install name>", with a header line per architecture slice.
                foreach (var raw in stdout.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length > 0 && !line.EndsWith(":", StringComparison.Ordinal))
                        return line;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A file that is not a Mach-O, and a path that is not a file, are both simply
        /// "no dependencies" — the preload runs against whatever directory it was pointed at and
        /// must never throw its way out of FFmpeg initialization.</summary>
        [Fact]
        public void Unreadable_and_non_macho_inputs_report_nothing_instead_of_throwing()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");

            var text = Path.Combine(Path.GetTempPath(), $"clowd-macho-{Guid.NewGuid():N}.txt");
            File.WriteAllText(text, "this is certainly not a Mach-O file");
            try
            {
                Assert.Empty(MacDylibPreloader.ReadRpathDependencies(text));
                Assert.Empty(MacDylibPreloader.ReadRpathDependencies(text + ".missing"));
                Assert.Empty(MacDylibPreloader.ReadRpathDependencies(Path.GetTempPath()));
            }
            finally
            {
                File.Delete(text);
            }
        }

        /// <summary>A directory with none of the FFmpeg roots in it yields an empty closure, and
        /// preloading it does nothing — the case that happens on every machine whose FFmpeg came
        /// from somewhere that never needed the preload at all.</summary>
        [Fact]
        public void A_directory_without_ffmpeg_has_an_empty_closure()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");

            var empty = Path.Combine(Path.GetTempPath(), $"clowd-noffmpeg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(empty);
            try
            {
                Assert.Empty(MacDylibPreloader.BuildClosure(empty));
                MacDylibPreloader.Preload(empty);                 // must not throw
                MacDylibPreloader.Preload(empty + "-missing");    // nor for a directory that is not there
                MacDylibPreloader.Preload(null);
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }

        /// <summary>
        /// The closure over a real FFmpeg directory reaches past the roots. Runs against whatever
        /// FFmpeg this machine resolved for the rest of the suite, so it exercises the layout that
        /// is actually in use rather than a fixture.
        /// </summary>
        [Fact]
        public void The_closure_over_a_real_ffmpeg_directory_includes_avcodec_and_its_dependencies()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only");
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

            var closure = MacDylibPreloader.BuildClosure(FFmpegLoader.LibrariesDirectory);
            Assert.SkipWhen(closure.Count == 0,
                $"no unversioned FFmpeg dylibs in {FFmpegLoader.LibrariesDirectory}");

            var names = closure.Select(Path.GetFileName).ToList();
            Assert.Contains("libavcodec.dylib", names);
            Assert.Contains("libavformat.dylib", names);

            // no file appears twice: loading one library under two of its names would put the same
            // code in the process twice, which is the reason the roots are spelled unversioned.
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

            // and the versioned aliases of the same libraries stay out of it.
            Assert.DoesNotContain(names, n => n.StartsWith("libavcodec.6", StringComparison.Ordinal));
        }
    }
}
