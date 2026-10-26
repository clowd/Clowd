using System.Linq;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The standby capturer's PRISC_SETTINGS_STATUS (CAPTURE_PROTOCOL.md §1.2). The shell acts on
    /// <c>error_code</c>, never on the display text, which prisc_hotkeys is free to reword.
    /// </summary>
    public class CaptureStandbyStatusTests
    {
        [Fact]
        public void A_permission_failure_is_read_from_its_code_not_its_text()
        {
            var status = CaptureStandbySupervisor.ParseHotkeyStatus("""
                {"hotkeys":{
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
            var status = CaptureStandbySupervisor.ParseHotkeyStatus("""
                {"hotkeys":{
                  "main":[{"gesture":"F12","active":true,"error":null,"error_code":null},
                          {"gesture":"Shift+F12","active":false,"error":"duplicate gesture","error_code":"duplicate"}],
                  "window":[{"gesture":"Alt+F12","active":false,"error":"the Accessibility permission is required for global hotkeys"}],
                  "monitor":[]}}
                """);

            Assert.False(status.NeedsAccessibility);
            Assert.Equal(new ExternalGestureStatus("F12", true, "", null), status.Actions[0].Gestures[0]);
            Assert.Equal("duplicate", status.Actions[0].Gestures[1].ErrorCode);
            // a capturer that predates error_code: the text still lands, with no code
            Assert.Equal(new ExternalGestureStatus("Alt+F12", false, "the Accessibility permission is required for global hotkeys", null),
                status.Actions[1].Gestures.Single());
        }
    }
}
