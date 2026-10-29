using System.Linq;
using Prisc.Ipc;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// What the standby supervisor makes of the capturer's <c>settings_status</c> (HELPER_PROTOCOL.md
    /// §4.5). The shell acts on <c>error_code</c>, never on the display text, which prisc_hotkeys is
    /// free to reword.
    /// </summary>
    public class CaptureStandbySupervisorTests
    {
        private static CaptureStandbySupervisor.StandbyHotkeyStatus Status(string line) =>
            CaptureStandbySupervisor.ReadHotkeyStatus(((CaptureEvent.SettingsStatus)CaptureCodec.DecodeEvent(line)).Hotkeys);

        [Fact]
        public void A_permission_failure_is_read_from_its_code_not_its_text()
        {
            var status = Status("""
                {"type":"settings_status","hotkeys":{
                  "main":[{"gesture":"F12","active":false,"error":"reworded","error_code":"permission"}],
                  "window":[],
                  "monitor":[{"gesture":"Alt+F12","active":false,"error":"reworded","error_code":"permission"}]}}
                """);

            Assert.True(status.NeedsAccessibility);
            Assert.Equal(new[] { HotkeyId.CaptureRegion, HotkeyId.CaptureActive, HotkeyId.CaptureFullscreen },
                status.Actions.Select(a => a.Id));
            Assert.Equal(new ExternalGestureStatus("F12", false, "reworded", "permission"), status.Actions[0].Gestures.Single());
            Assert.Empty(status.Actions[1].Gestures);
        }

        [Fact]
        public void Other_failures_and_codeless_statuses_need_no_permission()
        {
            var status = Status("""
                {"type":"settings_status","hotkeys":{
                  "main":[{"gesture":"F12","active":true},
                          {"gesture":"Shift+F12","active":false,"error":"duplicate gesture","error_code":"duplicate"}],
                  "window":[{"gesture":"Alt+F12","active":false,"error":"the Accessibility permission is required for global hotkeys"}],
                  "monitor":[]}}
                """);

            Assert.False(status.NeedsAccessibility);
            Assert.Equal(new ExternalGestureStatus("F12", true, "", null), status.Actions[0].Gestures[0]);
            Assert.Equal("duplicate", status.Actions[0].Gestures[1].ErrorCode);
            // no code: the text still lands, and is never read as a permission failure
            Assert.Equal(new ExternalGestureStatus("Alt+F12", false, "the Accessibility permission is required for global hotkeys", null),
                status.Actions[1].Gestures.Single());
        }
    }
}
