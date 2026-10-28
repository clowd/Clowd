using System.Collections.Generic;
using Avalonia.Media;
using Prisc.Config;
using Prisc.Ipc;
using Prisc.PlatformUtil;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The share-region rules that live above the wire (HELPER_PROTOCOL.md §4.2), fed event by event
    /// without a process: the one-decision handshake, the permanence of a blur failure, and the
    /// commands the driver writes. The wire shapes themselves are the generated codec's, pinned by
    /// the catalog samples in Prisc.Ipc.Tests. The expensive failures here are all silent (a share
    /// that reports itself cancelled while still mirroring, a blur button that no longer does
    /// anything, a helper crash swallowed as "the user changed their mind"), so this is where they
    /// are caught.
    /// </summary>
    public class ShareRegionDriverTests
    {
        private static ShareRegionRect Rect(int x, int y, uint w, uint h) => new(x, y, w, h);

        private static HelperOutcome<ShareRegionEvent> Ended(ShareRegionEvent terminal, HelperFailure failure, int exitCode = 0) =>
            new(terminal, exitCode, failure, "");

        [Fact]
        public void Initialized_settles_nothing()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.Initialized());

            Assert.Equal(ShareHandshake.Pending, driver.Handshake);
            Assert.Null(driver.AppliedRegion);
        }

        /// <summary>The helper rounds the size, so the region it reports is the truth.</summary>
        [Fact]
        public void Sharing_started_settles_started_with_the_applied_region()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.SharingStarted(Rect(-1920, 0, 1280, 720)));

            Assert.Equal(ShareHandshake.Started, driver.Handshake);
            Assert.Equal(Rect(-1920, 0, 1280, 720), driver.AppliedRegion);
        }

        [Fact]
        public void A_cancelled_done_before_the_decision_settles_cancelled()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.Done(ShareRegionResult.Cancelled));

            Assert.Equal(ShareHandshake.Cancelled, driver.Handshake);
            driver.HandleEnded(Ended(new ShareRegionEvent.Done(ShareRegionResult.Cancelled), HelperFailure.None));
            Assert.Equal(ShareHandshake.Cancelled, driver.Handshake);
        }

        /// <summary>Exactly one decision per session: a second settling event must never turn a live
        /// share into a cancelled one.</summary>
        [Fact]
        public void Only_the_first_decision_counts()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.SharingStarted(Rect(0, 0, 640, 480)));
            driver.Handle(new ShareRegionEvent.Done(ShareRegionResult.Cancelled));
            driver.Handle(new ShareRegionEvent.SharingStarted(Rect(5, 5, 64, 64)));

            Assert.Equal(ShareHandshake.Started, driver.Handshake);
            Assert.Equal(Rect(0, 0, 640, 480), driver.AppliedRegion);
        }

        /// <summary>A helper that died before the user decided is a failure, whatever the exit code
        /// says: the case worth reporting, not "the user changed their mind".</summary>
        [Fact]
        public void A_death_before_the_decision_is_a_failure_even_on_exit_code_zero()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.Initialized());
            driver.HandleEnded(Ended(null, HelperFailure.Crashed, exitCode: 0));

            Assert.Equal(ShareHandshake.Failed, driver.Handshake);
        }

        /// <summary>Asking the helper to quit while the prompt is still up ends cleanly, and that is a
        /// cancel, not a failure.</summary>
        [Fact]
        public void A_clean_end_before_the_decision_is_a_cancel()
        {
            var driver = new ShareRegionDriver();
            driver.HandleEnded(Ended(new ShareRegionEvent.Done(ShareRegionResult.Stopped), HelperFailure.None));

            Assert.Equal(ShareHandshake.Cancelled, driver.Handshake);
        }

        [Fact]
        public void The_end_of_a_running_share_keeps_it_started()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.SharingStarted(Rect(0, 0, 640, 480)));
            driver.HandleEnded(Ended(null, HelperFailure.Crashed, exitCode: 1));

            Assert.Equal(ShareHandshake.Started, driver.Handshake);
        }

        /// <summary>A move is legal during the prompt phase, and its answer says nothing about the
        /// user's decision.</summary>
        [Fact]
        public void Region_changed_updates_the_region_without_settling_the_handshake()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.RegionChanged(Rect(-100, -50, 640, 480)));

            Assert.Equal(ShareHandshake.Pending, driver.Handshake);
            Assert.Equal(Rect(-100, -50, 640, 480), driver.AppliedRegion);
        }

        [Fact]
        public void An_unsolicited_none_permanently_retires_blur()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.None, 0));
            Assert.False(driver.BlurAvailable);

            // and nothing brings it back
            driver.SetObscure(ShareRegionObscureMode.Blur, 50);
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Blur, 50));
            Assert.False(driver.BlurAvailable);
        }

        /// <summary>Every command is answered once and in order, so an ack with a command of ours
        /// outstanding is solicited, even a <c>none</c>.</summary>
        [Fact]
        public void Turning_blur_off_on_purpose_keeps_the_feature()
        {
            var driver = new ShareRegionDriver();
            driver.SetObscure(ShareRegionObscureMode.Blur, 50);
            driver.SetObscure(ShareRegionObscureMode.None);
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Blur, 50));
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.None, 0));

            Assert.True(driver.BlurAvailable);

            // the count is back at zero: the next unprompted retraction is the helper giving up
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.None, 0));
            Assert.False(driver.BlurAvailable);
        }

        [Fact]
        public void An_unknown_obscure_mode_is_ignored()
        {
            var driver = new ShareRegionDriver();
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Unknown, 0));

            Assert.True(driver.BlurAvailable);
        }

        /// <summary>The strength belongs to blur and pixelate only, clamped to the helper's 1..100
        /// before the write rather than refused on the wire (a refusal gets no ack and strands the
        /// pending-ack count).</summary>
        [Fact]
        public void Only_blur_and_pixelate_carry_a_strength_and_it_is_clamped()
        {
            Assert.Equal(new ShareRegionCommand.Obscure(ShareRegionObscureMode.None, null),
                ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.None, 50));
            Assert.Equal(new ShareRegionCommand.Obscure(ShareRegionObscureMode.Blur, 50u),
                ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Blur, 50));
            Assert.Equal(new ShareRegionCommand.Obscure(ShareRegionObscureMode.Pixelate, 80u),
                ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Pixelate, 80));
            Assert.Equal(new ShareRegionCommand.Obscure(ShareRegionObscureMode.Hide, null),
                ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Hide, 50));

            Assert.Equal(1u, ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Blur, -5).Strength);
            Assert.Equal(100u, ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Blur, 1000).Strength);

            Assert.Equal("""{"type":"obscure","mode":"hide"}""",
                ShareRegionCodec.EncodeCommand(ShareRegionDriver.BuildObscureCommand(ShareRegionObscureMode.Hide, 50)));
        }

        /// <summary>The capture API is a spawn-time argument and always emitted, so the helper's own
        /// default can never disagree with what the settings page shows.</summary>
        [Fact]
        public void The_capture_method_is_always_on_the_helper_command_line()
        {
            static string MethodArg(ScreenCaptureMethod method)
            {
                var args = new List<string>(ShareRegionDriver.BuildArguments(
                    new ScreenRect(0, 0, 640, 480), "t", true, 30, method));
                return args[args.IndexOf("--capture-method") + 1];
            }

            Assert.Equal("auto", MethodArg(ScreenCaptureMethod.Auto));
            Assert.Equal("dxgi", MethodArg(ScreenCaptureMethod.Dxgi));
            Assert.Equal("wgc", MethodArg(ScreenCaptureMethod.Wgc));
        }

        /// <summary>The accent goes over as <c>#RRGGBB</c>; no accent omits the flag, so the helper's
        /// own blue stands.</summary>
        [Fact]
        public void The_accent_is_passed_as_rrggbb_or_omitted()
        {
            var args = new List<string>(ShareRegionDriver.BuildArguments(
                new ScreenRect(0, 0, 640, 480), "t", true, 30, ScreenCaptureMethod.Auto, Color.FromArgb(0x80, 0x0A, 0xB1, 0xFF)));
            Assert.Equal("#0AB1FF", args[args.IndexOf("--accent-color") + 1]);

            var none = ShareRegionDriver.BuildArguments(
                new ScreenRect(0, 0, 640, 480), "t", true, 30, ScreenCaptureMethod.Auto);
            Assert.DoesNotContain("--accent-color", none);
        }
    }
}
