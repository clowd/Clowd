using System;
using Xunit;

namespace Clowd.Shared.Tests
{
    public class ClowdPlatformTests
    {
        [Theory]
        [InlineData("wayland", null, true)]
        [InlineData("Wayland", "", true)]
        [InlineData("x11", "wayland-0", true)] // WAYLAND_DISPLAY wins
        [InlineData(null, "wayland-0", true)]
        [InlineData("x11", null, false)]
        [InlineData("x11", "", false)]
        [InlineData(null, null, false)]
        [InlineData("tty", null, false)]
        public void IsWaylandSession_MatchesTheSessionRule(string xdgSessionType, string waylandDisplay, bool expected)
        {
            Assert.Equal(expected, ClowdPlatform.IsWaylandSession(xdgSessionType, waylandDisplay));
        }

        [Fact]
        public void EverythingIsSupported_OffLinux()
        {
            // Windows and macOS must behave exactly as before Linux support existed.
            if (OperatingSystem.IsLinux())
                return;

            Assert.False(ClowdPlatform.IsLinux);
            Assert.False(ClowdPlatform.IsWayland);
            Assert.False(ClowdPlatform.IsX11);
            Assert.False(ClowdPlatform.RecorderShowsSourcePicker);
            Assert.True(ClowdPlatform.OverlayPicksRecordingRegion);

            Assert.True(ClowdPlatform.SupportsCaptureOverlay);
            Assert.True(ClowdPlatform.SupportsGlobalHotkeys);
            Assert.True(ClowdPlatform.SupportsWarmCapturer);
            Assert.True(ClowdPlatform.SupportsScrollCapture);
            Assert.True(ClowdPlatform.SupportsShareRegion);
            Assert.True(ClowdPlatform.SupportsImageSearch);
            Assert.True(ClowdPlatform.SupportsActiveWindowCapture);
            Assert.True(ClowdPlatform.SupportsRecordingSidecars);
            Assert.True(ClowdPlatform.SupportsClickTracker);
        }
    }
}
