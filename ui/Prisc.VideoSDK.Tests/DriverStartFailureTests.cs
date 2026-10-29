using System;
using System.IO;
using System.Threading.Tasks;
using Prisc.Config;
using Prisc.PlatformUtil;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// A helper that never starts (missing binary, a stale build that fails the hello check) leaves
    /// nothing to wait for. The page's failure path disposes the driver before showing its error and
    /// closing, so a dispose that hangs here swallows the error dialog and leaves the page open.
    /// </summary>
    public class DriverStartFailureTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private static string MissingExe() =>
            Path.Combine(Path.GetTempPath(), "prisc-missing-" + Guid.NewGuid().ToString("N"), "prisc_missing");

        [Fact]
        public async Task A_recorder_whose_start_failed_disposes_at_once()
        {
            var driver = new RecorderDriver();
            await Assert.ThrowsAnyAsync<Exception>(() => driver.InitializeAsync(Array.Empty<string>(), MissingExe()));

            await driver.DisposeAsync().WaitAsync(Bound);
        }

        [Fact]
        public async Task A_share_whose_start_failed_disposes_at_once()
        {
            var driver = new ShareRegionDriver();
            await Assert.ThrowsAnyAsync<Exception>(() => driver.InitializeAsync(new ScreenRect(0, 0, 640, 480), "t", true, 30,
                ScreenCaptureMethod.Auto, null, MissingExe()));

            await driver.DisposeAsync().AsTask().WaitAsync(Bound);
            await driver.ShutdownAsync().WaitAsync(Bound);
        }
    }
}
