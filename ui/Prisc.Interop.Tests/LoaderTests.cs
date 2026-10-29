using System;
using System.IO;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    public sealed class LoaderTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "prisc-interop-loader-" + Guid.NewGuid().ToString("N"));

        public LoaderTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }

        [Fact]
        public void The_library_loads_and_speaks_these_bindings()
        {
            InteropTestGate.Require();
            Assert.True(InteropLibrary.IsAvailable);
            Assert.Equal("", InteropLibrary.UnavailableReason);
            Assert.True(File.Exists(InteropLibrary.LoadedPath));
            Assert.False(String.IsNullOrEmpty(InteropLibrary.Version));
            InteropLibrary.Require();
        }

        [Fact]
        public void An_override_is_authoritative()
        {
            var lib = Touch("custom", InteropLibrary.LibraryFileName);
            Assert.Equal(lib, InteropLibrary.ResolvePath(lib, null, _root).Path);

            var (path, failure) = InteropLibrary.ResolvePath("/nonexistent/" + InteropLibrary.LibraryFileName, null, AppContext.BaseDirectory);
            Assert.Null(path);
            Assert.Contains(EnvVars.InteropPath, failure);
        }

        [Fact]
        public void A_missing_override_is_unavailable_and_require_throws()
        {
            var state = InteropLibrary.Load("/nonexistent", null, AppContext.BaseDirectory, InteropAbi.Hash);
            Assert.Equal(IntPtr.Zero, state.Handle);
            Assert.Contains("/nonexistent", state.Reason);
            var e = Assert.Throws<InteropUnavailableException>(state.Require);
            Assert.Contains("/nonexistent", e.Message);
        }

        [Fact]
        public void A_file_that_is_not_a_library_is_refused()
        {
            var fake = Touch("fake", InteropLibrary.LibraryFileName);
            var state = InteropLibrary.Load(fake, null, _root, InteropAbi.Hash);
            Assert.Equal(IntPtr.Zero, state.Handle);
            Assert.Contains("could not be loaded", state.Reason);
        }

        [Fact]
        public void A_library_with_another_abi_is_refused_as_stale()
        {
            InteropTestGate.Require();
            var state = InteropLibrary.Load(InteropLibrary.LoadedPath, null, AppContext.BaseDirectory, InteropAbi.Hash ^ 1);
            Assert.Equal(IntPtr.Zero, state.Handle);
            Assert.Contains("stale prisc_interop at", state.Reason);
            Assert.Contains("run cargo codegen, then cargo build -p prisc_interop", state.Reason);
            // the process-wide library is untouched by the rejected probe
            Assert.True(InteropLibrary.IsAvailable);
        }

        [Fact]
        public void The_probe_walks_up_to_the_newer_cargo_build_including_deps()
        {
            var app = Path.Combine(_root, "ui", "App", "bin");
            Directory.CreateDirectory(app);
            Touch("Cargo.toml");
            var name = NativeProbe.LibName("prisc_interop");
            Assert.Null(NativeProbe.Find(name, null, app, library: true));

            var debug = Touch("target", "debug", name);
            File.SetLastWriteTimeUtc(debug, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(debug, NativeProbe.Find(name, null, app, library: true));

            var deps = Touch("target", "debug", "deps", name);
            File.SetLastWriteTimeUtc(deps, DateTime.UtcNow);
            Assert.Equal(deps, NativeProbe.Find(name, null, app, library: true));
            Assert.Equal(debug, NativeProbe.Find(name, null, app, library: false));
        }

        [Fact]
        public void Helper_dirs_and_the_app_directory_come_before_the_walk_up()
        {
            var name = NativeProbe.LibName("prisc_interop");
            Touch("Cargo.toml");
            Touch("target", "debug", name);
            var app = Path.Combine(_root, "app");
            var shipped = Touch("app", name);
            Assert.Equal(shipped, NativeProbe.Find(name, null, app, library: true));
            var helpers = Touch("helpers", "recorder", name);
            Assert.Equal(helpers, NativeProbe.Find(name, Path.Combine(_root, "missing") + Path.PathSeparator + Path.Combine(_root, "helpers"), app, library: true));
        }

        [Fact]
        public void File_names_follow_the_platform()
        {
            var lib = NativeProbe.LibName("prisc_interop");
            if (OperatingSystem.IsWindows())
                Assert.Equal("prisc_interop.dll", lib);
            else if (OperatingSystem.IsMacOS())
                Assert.Equal("libprisc_interop.dylib", lib);
            else
                Assert.Equal("libprisc_interop.so", lib);
            Assert.Equal(OperatingSystem.IsWindows() ? "prisc_capture.exe" : "prisc_capture", NativeProbe.ExeName("prisc_capture"));
        }
    }
}
