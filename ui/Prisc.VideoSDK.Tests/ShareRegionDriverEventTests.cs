using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Prisc.Ipc;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// What the share driver raises to the page, which it posts to the UI thread: whether an obscure
    /// ack was one we asked for, and the text of a refused command. A wrong "unsolicited" retires
    /// blur for the rest of the share, and a reworded refusal hides the helper's reason from the user.
    /// </summary>
    [Collection(HeadlessApp.Collection)]
    public class ShareRegionDriverEventTests
    {
        private static ShareRegionDriver Driver(List<ShareObscureState> acks, List<string> errors)
        {
            var driver = new ShareRegionDriver();
            driver.ObscureChanged += (_, state) => acks.Add(state);
            driver.CommandError += (_, message) => errors.Add(message);
            return driver;
        }

        [AvaloniaFact]
        public void Two_commands_in_flight_are_answered_in_order()
        {
            var acks = new List<ShareObscureState>();
            var driver = Driver(acks, new List<string>());

            driver.SetObscure(ShareRegionObscureMode.Blur, 50);
            driver.SetObscure(ShareRegionObscureMode.None);
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Blur, 50));
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.None, 0));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, acks.Count);
            Assert.All(acks, a => Assert.False(a.Unsolicited));
            Assert.All(acks, a => Assert.True(a.BlurAvailable));
            Assert.True(driver.BlurAvailable);
        }

        [AvaloniaFact]
        public void An_ack_with_a_command_outstanding_is_solicited()
        {
            var acks = new List<ShareObscureState>();
            var driver = Driver(acks, new List<string>());

            driver.SetObscure(ShareRegionObscureMode.Blur, 50);
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Blur, 50));
            // the outstanding count is consumed by that ack: the next one is unsolicited again
            driver.Handle(new ShareRegionEvent.Obscure(ShareRegionObscureMode.Blur, 50));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, acks.Count);
            Assert.Equal(new ShareObscureState(ShareRegionObscureMode.Blur, 50, false, true), acks[0]);
            Assert.True(acks[1].Unsolicited);
            Assert.True(driver.BlurAvailable);
        }

        [AvaloniaFact]
        public void Command_error_carries_the_helpers_message_verbatim()
        {
            var errors = new List<string>();
            var driver = Driver(new List<ShareObscureState>(), errors);
            var region = new ShareRegionRect(0, 0, 640, 480);

            driver.Handle(new ShareRegionEvent.SharingStarted(region));
            driver.Handle(new ShareRegionEvent.Error("region_too_small", "région trop petite: 幅 < 64 px", false));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("région trop petite: 幅 < 64 px", Assert.Single(errors));
            Assert.Equal(ShareHandshake.Started, driver.Handshake); // not fatal
            Assert.Equal(region, driver.AppliedRegion);

            // a refusal with no message still reports something the user could be shown
            driver.Handle(new ShareRegionEvent.Error("bad_command", "", false));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, errors.Count);
            Assert.False(string.IsNullOrEmpty(errors[1]));
        }
    }
}
