using System;
using System.Collections.Generic;
using Clowd;
using Clowd.UI;
using Xunit;
using static Clowd.UI.HotkeysAbi;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The decisions <see cref="NativeHotkeyHost"/> makes around the native library, tested without
    /// it: a host that is never started never loads the library or touches a handle, so these drive
    /// the host's own bookkeeping directly — the parts its event thread and the UI-thread callbacks
    /// it posts run.
    /// </summary>
    public class NativeHotkeyHostTests
    {
        private static readonly HotkeyGesture CtrlShift1 = new(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        [Fact]
        public void Only_minus_one_from_next_event_is_a_clean_close()
        {
            Assert.Equal(NativeHotkeyHost.NextEvent.Event, NativeHotkeyHost.ClassifyNextEvent(1));
            Assert.Equal(NativeHotkeyHost.NextEvent.Timeout, NativeHotkeyHost.ClassifyNextEvent(0));
            Assert.Equal(NativeHotkeyHost.NextEvent.Closed, NativeHotkeyHost.ClassifyNextEvent(-1));

            // anything else leaves the hook running with nobody draining it: every registered
            // gesture swallowed system-wide, none firing, every status still Active
            foreach (var result in new[] { CH_ERR_NULL, CH_ERR_PANIC, CH_ERR_STOPPED, 2 })
                Assert.Equal(NativeHotkeyHost.NextEvent.Failed, NativeHotkeyHost.ClassifyNextEvent(result));
        }

        [Fact]
        public void A_hotkey_posted_before_its_registration_was_disposed_does_not_run()
        {
            using var host = new NativeHotkeyHost();
            var fired = 0;
            var registration = host.AddRegistration(7, () => fired++, fireOnRepeat: false);

            host.RunHotkey(registration);
            Assert.Equal(1, fired);

            registration.Dispose();
            host.RunHotkey(registration);
            Assert.Equal(1, fired);

            // nor does one whose id now belongs to a newer registration
            var newer = host.AddRegistration(7, () => { }, fireOnRepeat: false);
            host.RunHotkey(registration);
            Assert.Equal(1, fired);
            newer.Dispose();
        }

        [Fact]
        public void A_hook_failure_ends_the_live_recording_with_the_error_and_fails_every_registration()
        {
            using var host = new NativeHotkeyHost();
            var first = host.AddRegistration(1, () => { }, fireOnRepeat: false);
            var second = host.AddRegistration(2, () => { }, fireOnRepeat: false);
            var raised = new List<object>();
            first.StatusChanged += (s, _) => raised.Add(s);
            second.StatusChanged += (s, _) => raised.Add(s);

            HotkeyGesture recorded = null;
            string failure = null;
            var session = host.BeginSession(5, _ => { }, g => recorded = g, e => failure = e);

            host.ApplyHookFailure("The global hotkey listener stopped.", new[] { first, second });

            Assert.Equal("The global hotkey listener stopped.", failure);
            Assert.Null(recorded);
            Assert.False(first.IsRegistered);
            Assert.Equal("The global hotkey listener stopped.", first.Error);
            Assert.False(second.IsRegistered);
            Assert.Equal(new object[] { first, second }, raised);

            // the session is over: a late Recorded for its token, or its disposal, does nothing more
            host.DeliverRecorded(5, CtrlShift1);
            session.Dispose();
            Assert.Null(recorded);
        }

        [Fact]
        public void A_hook_failure_with_no_recording_only_fails_the_registrations()
        {
            using var host = new NativeHotkeyHost();
            var registration = host.AddRegistration(1, () => { }, fireOnRepeat: false);

            host.ApplyHookFailure("stopped", new[] { registration });

            Assert.False(registration.IsRegistered);
            Assert.Equal("stopped", registration.Error);
        }

        [Fact]
        public void The_permission_message_points_at_the_System_page()
        {
            var message = NativeHotkeyHost.StartError(CH_ERR_PERMISSION, "");

            Assert.Contains("Settings → System → Permissions", message);
            Assert.DoesNotContain("General", message);
        }

        [Fact]
        public void A_start_that_throws_reports_why()
        {
            var message = NativeHotkeyHost.StartError(new EntryPointNotFoundException("clowd_hotkeys_start"));

            Assert.Contains("clowd_hotkeys_start", message);
            Assert.DoesNotContain("-98", message);
        }

        [Fact]
        public void The_stopped_message_says_how_to_get_hotkeys_back()
        {
            Assert.Contains("Restart Clowd", NativeHotkeyHost.StoppedError("the event tap was invalidated"));
            Assert.Contains("the event tap was invalidated", NativeHotkeyHost.StoppedError("the event tap was invalidated"));
            Assert.Contains("Restart Clowd", NativeHotkeyHost.StoppedError(""));
        }
    }
}
