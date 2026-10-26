using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The recording strip's source rules, pinned one by one. Every one of them used to be a line
    /// inside the floating toolbar that could only be exercised by opening a real recording session
    /// with a particular set of devices attached (or not attached), which is why none of them was ever
    /// covered: they are cheap to read and expensive to reproduce.
    ///
    /// <para>
    /// Two rules carry the weight, and both are the kind that fails silently — the recording comes
    /// back wrong rather than not coming back at all:
    /// </para>
    /// <para>
    /// 1. "default" is a POINTER at another device, never an alternative to them. The enumerator always
    /// offers it, so a machine with no microphone at all still reports one selectable id; taking that at
    /// face value earns the user a track of silence they only discover after the recording. The same
    /// fact from the other side is why one real device shows no picker: "Default" and that microphone
    /// ARE the same microphone, so there is nothing to choose.
    /// </para>
    /// <para>
    /// 2. Mic and system audio are MUTES, so they stay live while frames flow — but only where the
    /// recorder actually built a source to mute. With no device there is nothing to unmute and the
    /// picker that would fix that is frozen too, so the toggle has to lock with it.
    /// </para>
    /// </summary>
    public class RecordingSourceRulesTests
    {
        private const string Default = AudioDeviceManager.DefaultDeviceId;

        // -- HasAudioDevice: "default" is a pointer, not a device --

        [Fact]
        public void The_default_pseudo_device_alone_is_not_a_device()
        {
            // a machine with nothing attached: the enumerator still offers "default", and picking it
            // would turn the source on to record silence.
            Assert.False(RecordingSourceRules.HasAudioDevice(Default, new[] { Default }, Default));
        }

        [Fact]
        public void The_default_pseudo_device_counts_once_something_backs_it()
        {
            // following the system default is a real preference while a real device is behind it.
            Assert.True(RecordingSourceRules.HasAudioDevice(Default, new[] { Default, "mic-1" }, Default));
        }

        [Fact]
        public void A_stored_device_that_is_no_longer_attached_is_no_device()
        {
            // the recorder would open nothing, and the user would find that out after the recording.
            Assert.False(RecordingSourceRules.HasAudioDevice("mic-unplugged", new[] { Default, "mic-1" }, Default));
        }

        [Fact]
        public void A_stored_device_that_is_attached_is_a_device()
        {
            Assert.True(RecordingSourceRules.HasAudioDevice("mic-1", new[] { Default, "mic-1", "mic-2" }, Default));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_stored_device_is_no_device_even_with_devices_attached(string deviceId)
        {
            Assert.False(RecordingSourceRules.HasAudioDevice(deviceId, new[] { Default, "mic-1" }, Default));
        }

        [Fact]
        public void An_absent_device_list_is_no_device()
        {
            Assert.False(RecordingSourceRules.HasAudioDevice("mic-1", null, Default));
            Assert.False(RecordingSourceRules.HasAudioDevice("mic-1", Array.Empty<string>(), Default));
        }

        // -- HasCameraDevice: the camera list's three states --

        [Fact]
        public void A_stored_camera_is_trusted_until_the_enumeration_lands()
        {
            // null is "we cannot say yet", not "no cameras": the strip opens before the ~60 ms native
            // enumeration returns, and a webcam tick refused in that window would be the user's own
            // saved choice silently thrown away. DisableCaptureWithoutDevice runs again when the list
            // lands, which is what makes trusting the id here safe.
            Assert.True(RecordingSourceRules.HasCameraDevice("cam-1", null));
        }

        [Fact]
        public void An_enumerated_empty_camera_list_is_no_device()
        {
            // enumerated and there are none — a tick here would write an empty webcam_device and come
            // back as a recording with a missing track.
            Assert.False(RecordingSourceRules.HasCameraDevice("cam-1", Array.Empty<string>()));
        }

        [Fact]
        public void A_stored_camera_that_is_no_longer_attached_is_no_device()
        {
            Assert.False(RecordingSourceRules.HasCameraDevice("cam-unplugged", new[] { "cam-1", "cam-2" }));
        }

        [Fact]
        public void A_stored_camera_that_is_attached_is_a_device()
        {
            Assert.True(RecordingSourceRules.HasCameraDevice("cam-2", new[] { "cam-1", "cam-2" }));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_stored_camera_is_no_device_whatever_the_list_says(string deviceId)
        {
            // no id means the user has never picked a camera; there is nothing for the trust rule to
            // trust, so even an un-enumerated list does not make this true.
            Assert.False(RecordingSourceRules.HasCameraDevice(deviceId, null));
            Assert.False(RecordingSourceRules.HasCameraDevice(deviceId, new[] { "cam-1" }));
        }

        // -- RealDeviceIds: what there is to choose between --

        [Fact]
        public void RealDeviceIds_drops_the_default_pseudo_device()
        {
            var real = RecordingSourceRules.RealDeviceIds(new[] { Default, "mic-1", "mic-2" }, Default);

            Assert.Equal(new[] { "mic-1", "mic-2" }, real);
        }

        [Fact]
        public void RealDeviceIds_of_a_list_holding_only_the_default_is_empty()
        {
            Assert.Empty(RecordingSourceRules.RealDeviceIds(new[] { Default }, Default));
        }

        [Fact]
        public void RealDeviceIds_of_a_list_that_has_not_been_enumerated_yet_is_empty()
        {
            // null is "we cannot say", which for both callers leads to the same strip as "nothing to
            // choose": no picker, and no shortcut, so a click opens the menu that does the waiting.
            Assert.Empty(RecordingSourceRules.RealDeviceIds(null, Default));
        }

        [Fact]
        public void RealDeviceIds_keeps_the_enumerator_order_so_the_single_device_shortcut_is_stable()
        {
            var real = RecordingSourceRules.RealDeviceIds(new[] { "cam-b", "cam-a" }, Default);

            Assert.Equal(new[] { "cam-b", "cam-a" }, real.ToList());
        }

        // -- ShowsChevron: a picker is only worth offering for a choice --

        [Theory]
        [InlineData(0, false)] // nothing to pick; the toggle's own click (and the menu it opens) says so
        [InlineData(1, false)] // "Default" and that one device are the same device
        [InlineData(2, true)]
        [InlineData(7, true)]
        public void A_picker_appears_only_when_there_is_more_than_one_real_device(int realDeviceCount, bool expected)
        {
            Assert.Equal(expected, RecordingSourceRules.ShowsChevron(realDeviceCount));
        }

        // -- SourceEnabled: the mute rule --

        [Theory]
        [InlineData(false, false, true)] // not recording: every toggle is live, device or not
        [InlineData(false, true, true)]
        [InlineData(true, true, true)] // a mute needs a source to mute — and there is one
        [InlineData(true, false, false)] // …and locks rather than lighting up over silence
        public void A_source_stays_live_while_recording_only_where_there_is_a_device(bool recording, bool hasDevice, bool expected)
        {
            Assert.Equal(expected, RecordingSourceRules.SourceEnabled(recording, hasDevice));
        }

        // -- PrimaryState: the transport --

        [Theory]
        [InlineData(false, false, false, TrayPrimaryState.Idle)] // recorder up, nothing started
        [InlineData(true, false, false, TrayPrimaryState.Waiting)] // still being built: the button cannot act
        [InlineData(false, true, false, TrayPrimaryState.Active)]
        [InlineData(false, true, true, TrayPrimaryState.Paused)]
        // a respawn while frames flow raises the waiting flag again; recording wins, or the strip
        // would turn PAUSE back into a dead WAIT tile mid-recording.
        [InlineData(true, true, false, TrayPrimaryState.Active)]
        [InlineData(true, true, true, TrayPrimaryState.Paused)]
        public void The_primary_button_reads_the_transport_state(bool waiting, bool recording, bool paused, TrayPrimaryState expected)
        {
            Assert.Equal(expected, RecordingSourceRules.PrimaryState(waiting, recording, paused));
        }

        [Fact]
        public void Paused_without_a_recording_is_ignored_rather_than_shown()
        {
            // not reachable through the strip (SetPausedState no-ops unless recording), but the rule
            // has to be total: paused is something only a running recording can be.
            Assert.Equal(TrayPrimaryState.Idle, RecordingSourceRules.PrimaryState(false, false, true));
            Assert.Equal(TrayPrimaryState.Waiting, RecordingSourceRules.PrimaryState(true, false, true));
        }

        // -- FormatElapsed: the label --

        [Fact]
        public void No_status_yet_reads_as_zero_rather_than_an_empty_tile()
        {
            Assert.Equal("00:00", RecordingSourceRules.FormatElapsed(null));
        }

        [Theory]
        [InlineData(0, 0, 0, "00:00")]
        [InlineData(0, 0, 9, "00:09")]
        [InlineData(0, 59, 59, "59:59")]
        // minutes are not wrapped into hours: a 100-minute recording reads "100:00", which is what the
        // old strip did. The label clips rather than growing, so the strip never changes width.
        [InlineData(1, 40, 0, "100:00")]
        [InlineData(2, 0, 5, "120:05")]
        public void Elapsed_reads_as_unbounded_minutes_and_seconds(int hours, int minutes, int seconds, string expected)
        {
            Assert.Equal(expected, RecordingSourceRules.FormatElapsed(new TimeSpan(hours, minutes, seconds)));
        }

        [Fact]
        public void Sub_second_elapsed_truncates_rather_than_rounding_up()
        {
            // statuses arrive at 1 Hz on their own clock; a label that rounded would read 00:01 before
            // the first second of the recording existed.
            Assert.Equal("00:00", RecordingSourceRules.FormatElapsed(TimeSpan.FromMilliseconds(999)));
        }

        [Fact]
        public void The_formatted_label_is_always_the_same_width_for_the_same_digit_count()
        {
            // the primary button's label is centre-less (left aligned beside the glyph) and the timer
            // uses tabular figures, so mm:ss must always be zero-padded or the digits would shift.
            var labels = new List<string>
            {
                RecordingSourceRules.FormatElapsed(TimeSpan.Zero),
                RecordingSourceRules.FormatElapsed(TimeSpan.FromSeconds(5)),
                RecordingSourceRules.FormatElapsed(TimeSpan.FromMinutes(9)),
            };

            Assert.All(labels, l => Assert.Equal(5, l.Length));
        }
    }
}
