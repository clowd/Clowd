using System;
using System.IO;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The shell's side of the clowd_capture_wayland contract (the module docs of
    /// clowd_capture_wayland/src/main.rs): the one flag it is spawned with, and where the binary is
    /// found. Pure logic only — nothing here asks what platform it is running on, so the fake
    /// binaries are named with <see cref="CaptureBinaryLocator.WaylandFileName"/> and the same
    /// cases hold on Windows, where that is <c>clowd_capture_wayland.exe</c>.
    /// </summary>
    public class WaylandCaptureTests : IDisposable
    {
        private readonly string _root;

        public WaylandCaptureTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"clowd-wayland-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            return path;
        }

        // ------------------------------------------------------------------ arguments

        [Fact]
        public void BuildWayland_passes_only_the_session_dir()
        {
            var dir = Path.Combine(_root, "session");
            Assert.Equal(new[] { "--session-dir", dir }, CaptureArguments.BuildWayland(dir));
        }

        // ------------------------------------------------------------------ locator

        [Fact]
        public void ResolveWayland_finds_the_binary_beside_the_overlay()
        {
            var overlay = Touch("bin", CaptureBinaryLocator.BinaryFileName);
            var wayland = Touch("bin", CaptureBinaryLocator.WaylandFileName);

            var resolved = CaptureBinaryLocator.ResolveWayland(overlay, Path.Combine(_root, "app"));

            Assert.Equal(Path.GetFullPath(wayland), resolved);
        }

        [Fact]
        public void ResolveWayland_returns_null_when_no_layout_has_it()
        {
            var overlay = Touch("bin", CaptureBinaryLocator.BinaryFileName);
            var app = Path.Combine(_root, "app");
            Directory.CreateDirectory(app);

            Assert.Null(CaptureBinaryLocator.ResolveWayland(overlay, app));
            Assert.Null(CaptureBinaryLocator.ResolveWayland(null, app));
        }

        [Fact]
        public void ResolveWayland_without_an_overlay_falls_back_to_the_cargo_target_dir()
        {
            // a Wayland-only dev build: `cargo build -p clowd_capture_wayland` and nothing else.
            Touch("Cargo.toml");
            var wayland = Touch("target", "debug", CaptureBinaryLocator.WaylandFileName);
            var app = Path.Combine(_root, "clowd_ui", "Clowd.Ui", "bin", "Debug");
            Directory.CreateDirectory(app);

            var resolved = CaptureBinaryLocator.ResolveWayland(null, app);

            Assert.Equal(Path.GetFullPath(wayland), Path.GetFullPath(resolved));
        }

        [Fact]
        public void ResolveWayland_prefers_the_overlay_sibling_over_the_layout_probe()
        {
            // the CLOWD_CAPTURE_PATH override must keep pointing both binaries at the same build,
            // even with another build sitting in the probed layout.
            var overlay = Touch("override", CaptureBinaryLocator.BinaryFileName);
            var sibling = Touch("override", CaptureBinaryLocator.WaylandFileName);
            Touch("Cargo.toml");
            Touch("target", "debug", CaptureBinaryLocator.WaylandFileName);
            var app = Path.Combine(_root, "clowd_ui", "Clowd.Ui", "bin", "Debug");
            Directory.CreateDirectory(app);

            var resolved = CaptureBinaryLocator.ResolveWayland(overlay, app);

            Assert.Equal(Path.GetFullPath(sibling), resolved);
        }
    }
}
