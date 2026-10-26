using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Clowd.Config;
using Clowd.UI;
using Xunit;
using static Clowd.UI.HotkeysAbi;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="NativeHotkeyHost"/>'s lifecycle over a fake clowd_hotkeys: a hook that cannot
    /// start for want of Accessibility asks for it (once per run, and only on macOS), a hook that
    /// is down comes back once the permission is there — taking its registrations and observers
    /// along, freeing the dead handle exactly once — and a hook that dies mid-recording fails the
    /// recording. The UI thread is a queue the test drains; the event thread is real. The host's
    /// own bookkeeping — the parts its event thread and the UI-thread callbacks it posts run — is
    /// also driven directly, on a host that is never started.
    /// </summary>
    public class NativeHotkeyHostTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly HotkeyGesture ShiftA = new(HotkeyKey.A, HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlB = new(HotkeyKey.B, HotkeyModifiers.Control);
        private static readonly HotkeyGesture CtrlShift1 = new(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        private sealed class Rig : IDisposable
        {
            public readonly FakeNative Native = new();
            public readonly ConcurrentQueue<Action> Ui = new();
            public int Prompts;
            public bool Trusted;

            /// <summary>What the host filed as handled errors, by operation.</summary>
            public readonly ConcurrentQueue<string> Reports = new();
            public readonly AccessibilityPrompt Prompt;
            public readonly NativeHotkeyHost Host;

            /// <param name="retryInterval">Infinite by default: the test calls RetryStart itself.</param>
            public Rig(bool isMacOS = true, AccessibilityPrompt prompt = null, TimeSpan? retryInterval = null)
            {
                Prompt = prompt ?? new AccessibilityPrompt(isMacOS, () => Prompts++, Ui.Enqueue);
                Host = new NativeHotkeyHost(Native, Ui.Enqueue, isMacOS, () => Trusted, Prompt,
                                            retryInterval ?? System.Threading.Timeout.InfiniteTimeSpan,
                                            (_, operation) => Reports.Enqueue(operation));
            }

            public void RunUi()
            {
                while (Ui.TryDequeue(out var action))
                    action();
            }

            /// <summary>Drains the UI queue until <paramref name="done"/> holds, failing rather
            /// than hanging when the event thread never gets there.</summary>
            public void RunUiUntil(Func<bool> done)
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    RunUi();
                    if (done())
                        return;
                    Assert.True(clock.Elapsed < Timeout, "timed out waiting for the event thread");
                    Thread.Sleep(5);
                }
            }

            public void Dispose() => Host.Dispose();
        }

        // ---- the prompt (issue #4) ----

        [Fact]
        public void A_missing_permission_prompts_for_accessibility_once()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;

            var a = rig.Host.RegisterTrigger(ShiftA, () => { });
            var b = rig.Host.RegisterTrigger(CtrlB, () => { });
            var session = rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out var error);
            using var observer = rig.Host.ObserveKeys(_ => { });
            rig.RunUi();

            Assert.Equal(1, rig.Prompts);
            Assert.False(a.IsRegistered);
            Assert.False(b.IsRegistered);
            Assert.Null(session);
            Assert.Equal(a.Error, error);
        }

        [Fact]
        public void The_shell_and_the_standby_capturer_share_one_prompt()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var permission = CaptureStandbySupervisor.ParseHotkeyStatus("""
                {"hotkeys":{"main":[{"gesture":"F12","active":false,"error":"x","error_code":"permission"}],
                            "window":[],"monitor":[]}}
                """);

            // warm capture with no shell-owned gesture: the capturer's report is the only cue
            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt, () => rig.Trusted);
            rig.RunUi();
            Assert.Equal(1, rig.Prompts);

            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt, () => rig.Trusted);
            rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();
            Assert.Equal(1, rig.Prompts);
        }

        [Fact]
        public void Only_a_permission_status_from_the_capturer_prompts()
        {
            using var rig = new Rig();
            var other = CaptureStandbySupervisor.ParseHotkeyStatus("""
                {"hotkeys":{"main":[{"gesture":"F12","active":false,"error":"x","error_code":"duplicate"}],
                            "window":[],"monitor":[]}}
                """);

            CaptureStandbySupervisor.PromptIfNeeded(other, rig.Prompt, () => rig.Trusted);
            rig.RunUi();

            Assert.Equal(0, rig.Prompts);
        }

        [Fact]
        public void A_capturer_permission_status_under_a_trusted_grant_keeps_the_prompt_for_a_missing_one()
        {
            using var rig = new Rig();
            var permission = CaptureStandbySupervisor.ParseHotkeyStatus("""
                {"hotkeys":{"main":[{"gesture":"F12","active":false,"error":"x","error_code":"permission"}],
                            "window":[],"monitor":[]}}
                """);

            // a stale grant: the prompt would show nothing, yet spend this run's only one
            rig.Trusted = true;
            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt, () => rig.Trusted);
            rig.RunUi();
            Assert.Equal(0, rig.Prompts);

            // the user removed Clowd from the list as the stale-grant text says: now it is missing
            rig.Trusted = false;
            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt, () => rig.Trusted);
            rig.RunUi();
            Assert.Equal(1, rig.Prompts);
        }

        [Fact]
        public void Nothing_prompts_off_macos()
        {
            using var rig = new Rig(isMacOS: false);
            rig.Native.StartResult = CH_ERR_PERMISSION;

            rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.Prompt.RequestOnce();
            rig.RunUi();

            Assert.Equal(0, rig.Prompts);
        }

        [Fact]
        public void The_permission_message_points_to_the_system_page_and_asks_for_no_restart()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;

            var error = rig.Host.RegisterTrigger(ShiftA, () => { }).Error;

            Assert.Equal(NativeHotkeyHost.PermissionMessage, error);
            Assert.DoesNotContain("General", error);
            Assert.DoesNotContain("restart", error);
        }

        [Fact]
        public void The_permission_message_points_at_the_System_page()
        {
            var message = NativeHotkeyHost.StartError(CH_ERR_PERMISSION, "");

            Assert.Equal(NativeHotkeyHost.PermissionMessage, message);
            Assert.Contains("System", message);
            Assert.DoesNotContain("General", message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_permission_texts_name_the_page_as_the_nav_shows_it(bool permissionMissing)
        {
            // the nav renames the System entry "Permission Needed" while a permission is missing —
            // which is when these texts show — but not when only the hook's own check says no
            var label = MacPermissions.NavLabel(permissionMissing);

            Assert.Contains(label, NativeHotkeyHost.PermissionMessage);
            Assert.Contains(label, Clowd.UI.Dialogs.ColorPicker.EyedropperButton.PermissionTip);
        }

        // ---- recovery ----

        [Fact]
        public void Granting_accessibility_brings_the_hotkeys_back_without_a_restart()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;
            // the Windows defaults, where Capture Region's only gesture is PrtScr
            var manager = new HotkeyManager(rig.Host, new SettingsHotkey(OSPlatform.Windows));
            int fired = 0;
            manager.SetAction(HotkeyId.CaptureRegion, () => fired++);
            var shortcut = Assert.Single(manager.GetEntry(HotkeyId.CaptureRegion).Shortcuts);
            var media = rig.Host.RegisterTrigger(ShiftA, () => { }, fireOnRepeat: true);
            var observed = new ConcurrentQueue<GlobalKeyEvent>();
            using var observer = rig.Host.ObserveKeys(observed.Enqueue);
            rig.RunUi();
            Assert.False(shortcut.IsRegistered);
            Assert.False(media.IsRegistered);

            // still not trusted: no attempt is made
            rig.Native.StartResult = CH_OK;
            rig.Host.RetryStart();
            Assert.Equal(1, rig.Native.StartCalls);

            rig.Trusted = true;
            rig.Host.RetryStart();
            rig.RunUi();

            Assert.Equal(2, rig.Native.StartCalls);
            Assert.True(shortcut.IsRegistered);
            Assert.Equal("", shortcut.Error);
            Assert.True(media.IsRegistered);
            var hook = rig.Native.Current;
            Assert.True(hook.Observing);
            Assert.Equal(2, hook.Registered.Count);

            // and the new hook's events arrive: the hotkey fires, the observer hears keys
            hook.Push(new HotkeyEventNative { Kind = CH_EVENT_HOTKEY, Id = hook.IdOf(PrintScreen()) });
            hook.Push(new HotkeyEventNative { Kind = CH_EVENT_KEY, RawCode = 7, Flags = 1 });
            rig.RunUiUntil(() => fired == 1 && observed.Count == 1);

            // a later retry has nothing to do
            rig.Host.RetryStart();
            Assert.Equal(2, rig.Native.StartCalls);

            static HotkeyGesture PrintScreen() => new(HotkeyKey.PrintScreen);
        }

        [Fact]
        public void The_hook_coming_back_says_recording_works_again()
        {
            // what the Hotkeys page drops its "grant Accessibility" text on
            using var rig = new Rig();
            int available = 0;
            rig.Host.RecordingAvailable += (_, _) => available++;
            rig.Native.StartResult = CH_ERR_PERMISSION;
            Assert.Null(rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out _));

            rig.Trusted = true;
            rig.Native.StartResult = CH_ERR_HOOK_FAILED;
            rig.Host.RetryStart();
            Assert.Equal(0, available);

            rig.Native.StartResult = CH_OK;
            rig.Host.RetryStart();
            Assert.Equal(1, available);
            using var session = rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out var error);
            Assert.NotNull(session);
            Assert.Null(error);
        }

        [Fact]
        public void A_retry_that_fails_differently_tells_the_waiting_registrations()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();
            Assert.Equal(NativeHotkeyHost.PermissionMessage, registration.Error);

            // granted, but the tap still cannot be made: the rows must stop asking for the grant
            rig.Trusted = true;
            rig.Native.StartResult = CH_ERR_HOOK_FAILED;
            rig.Native.StartError = "could not create the event tap";
            rig.Host.RetryStart();
            rig.RunUi();

            Assert.False(registration.IsRegistered);
            Assert.Equal("Could not start the global hotkey listener: could not create the event tap", registration.Error);
            // and a registration made now says the same
            Assert.Equal(registration.Error, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);

            // still waiting: the next good retry takes it along
            rig.Native.StartResult = CH_OK;
            rig.Host.RetryStart();
            rig.RunUi();
            Assert.True(registration.IsRegistered);
        }

        [Fact]
        public void A_retry_that_fails_for_good_stops_retrying_and_says_why()
        {
            // a finite interval that never ticks within the test, so the timer exists
            using var rig = new Rig(retryInterval: TimeSpan.FromHours(1));
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();
            Assert.True(rig.Host.IsRetrying);

            rig.Trusted = true;
            rig.Native.StartResult = CH_ERR_PANIC;
            rig.Native.StartError = "the hook panicked";
            rig.Host.RetryStart();
            rig.RunUi();

            Assert.False(registration.IsRegistered);
            Assert.Equal("Could not start the global hotkey listener: the hook panicked", registration.Error);
            Assert.False(rig.Host.IsRetrying);

            // nothing comes back from a failure that cannot heal
            rig.Native.StartResult = CH_OK;
            rig.Host.RetryStart();
            rig.RunUi();
            Assert.Equal(2, rig.Native.StartCalls);
            Assert.False(registration.IsRegistered);
        }

        [Fact]
        public void A_registration_disposed_while_the_hook_is_down_does_not_come_back()
        {
            using var rig = new Rig();
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var kept = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.Host.RegisterTrigger(CtrlB, () => { }).Dispose();

            rig.Native.StartResult = CH_OK;
            rig.Trusted = true;
            rig.Host.RetryStart();
            rig.RunUi();

            Assert.True(kept.IsRegistered);
            Assert.Single(rig.Native.Current.Registered);
        }

        [Fact]
        public void A_hook_that_stops_comes_back_and_its_dead_handle_is_freed_once()
        {
            using var rig = new Rig { Trusted = true };
            // Closed is set before the pump has left NextEvent; a slow exit outlasts RetryStart's
            // short join, which then leaves the retry to the next tick
            rig.Native.AfterClosed = () => Thread.Sleep(300);
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            var first = rig.Native.Current;
            Assert.True(registration.IsRegistered);

            first.Kill("the Accessibility permission was revoked");
            Assert.True(first.Closed.Wait(Timeout));
            rig.RunUiUntil(() => !registration.IsRegistered);
            Assert.Contains("revoked", registration.Error);

            // as the retry timer does: a RetryStart that cannot join the dead pump yet does nothing
            rig.RunUiUntil(() =>
            {
                rig.Host.RetryStart();
                return rig.Native.StartCalls == 2;
            });
            rig.RunUi();

            Assert.Equal(2, rig.Native.StartCalls);
            Assert.NotSame(first, rig.Native.Current);
            Assert.True(registration.IsRegistered);
            Assert.Equal(new[] { first.Handle }, rig.Native.Freed);

            // the registration now lives on the new hook, and leaves it when disposed
            registration.Dispose();
            Assert.Empty(rig.Native.Current.Registered);

            rig.Host.Dispose();
            Assert.Equal(new[] { first.Handle, rig.Native.Current.Handle }, rig.Native.Freed);
            Assert.Empty(rig.Native.Misuse);
        }

        [Fact]
        public void A_hook_that_stops_off_macos_stays_down()
        {
            using var rig = new Rig(isMacOS: false) { Trusted = true };
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            var first = rig.Native.Current;

            first.Kill("the message loop failed");
            Assert.True(first.Closed.Wait(Timeout));
            rig.RunUiUntil(() => !registration.IsRegistered);
            rig.Host.RetryStart();

            Assert.Equal(1, rig.Native.StartCalls);
            Assert.False(rig.Host.IsRetrying);
            Assert.Contains("the message loop failed", registration.Error);
            Assert.Contains("Restart Clowd", registration.Error);
        }

        [Fact]
        public void A_failed_event_queue_stops_the_hook_for_good_even_on_macos()
        {
            // not the grant: the queue itself broke, with the hook possibly still swallowing
            using var rig = new Rig { Trusted = true };
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            var hook = rig.Native.Current;

            hook.NextEventResult = CH_ERR_PANIC;
            rig.RunUiUntil(() => !registration.IsRegistered);

            Assert.True(hook.Dead);
            Assert.Contains("Restart Clowd", registration.Error);
            Assert.False(rig.Host.IsRetrying);
            rig.Host.RetryStart();
            Assert.Equal(1, rig.Native.StartCalls);
            Assert.Equal(registration.Error, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            Assert.Single(rig.Reports, r => r == "hotkey.events");
        }

        // ---- a stale grant ----

        [Fact]
        public void A_stale_grant_says_how_to_fix_it_and_backs_off_without_giving_up()
        {
            // AXIsProcessTrusted reads true, yet the crate finds the tap stripped of key events:
            // a grant macOS still lists but no longer honours, which no prompt or poll fixes
            using var rig = new Rig { Trusted = true };
            int available = 0;
            rig.Host.RecordingAvailable += (_, _) => available++;
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();

            // not "grant Accessibility": the System page shows it as granted
            Assert.Equal(NativeHotkeyHost.StaleGrantMessage, registration.Error);
            Assert.Contains("Privacy & Security → Accessibility", registration.Error);
            Assert.Contains("remove Clowd", registration.Error);
            Assert.Contains("add it again", registration.Error);
            Assert.Equal(0, rig.Prompts);

            // 20 timer ticks: a few attempts, each a keyboard tap made and torn down on the UI thread
            for (int tick = 0; tick < 20; tick++)
                rig.Host.RetryStart();
            rig.RunUi();
            Assert.InRange(rig.Native.StartCalls - 1, 1, 5);
            Assert.Equal(NativeHotkeyHost.StaleGrantMessage, registration.Error);

            // the user removes and re-adds Clowd while it runs: a later attempt picks that up
            rig.Native.StartResult = CH_OK;
            for (int tick = 0; tick < 20 && !registration.IsRegistered; tick++)
                rig.Host.RetryStart();
            rig.RunUi();
            Assert.True(registration.IsRegistered);
            Assert.Equal(1, available);
        }

        [Fact]
        public void A_permission_that_goes_missing_ends_the_back_off()
        {
            using var rig = new Rig { Trusted = true };
            rig.Native.StartResult = CH_ERR_PERMISSION;
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.Host.RetryStart(); // an attempt now and then...
            rig.Host.RetryStart();
            int attempts = rig.Native.StartCalls;

            // ...until the grant really goes (Clowd removed from the list) and comes back
            rig.Trusted = false;
            rig.Host.RetryStart();
            rig.Trusted = true;
            rig.Native.StartResult = CH_OK;
            rig.Host.RetryStart();
            rig.RunUi();

            Assert.Equal(attempts + 1, rig.Native.StartCalls);
            Assert.True(registration.IsRegistered);
        }

        // ---- error reports ----

        [Fact]
        public void A_hook_that_keeps_stopping_is_reported_once()
        {
            using var rig = new Rig { Trusted = true };
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            for (int round = 0; round < 3; round++)
            {
                KillAndRecover(rig, registration, "the event tap was invalidated");
                Assert.Equal(round + 2, rig.Native.StartCalls);
            }

            Assert.Single(rig.Reports, r => r == "hotkey.stopped");
        }

        [Fact]
        public void A_revoked_permission_is_not_reported_as_an_error()
        {
            // the user switching Accessibility off: expected, and not a defect
            using var rig = new Rig { Trusted = true };
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            rig.Trusted = false;
            rig.Native.Current.Kill("the Accessibility permission was revoked");
            rig.RunUiUntil(() => !registration.IsRegistered);
            Assert.DoesNotContain("hotkey.stopped", rig.Reports);

            // and it does not use up the report a real failure gets
            rig.Trusted = true;
            rig.RunUiUntil(() =>
            {
                rig.Host.RetryStart();
                return registration.IsRegistered;
            });
            KillAndRecover(rig, registration, "the event tap was invalidated");
            Assert.Single(rig.Reports, r => r == "hotkey.stopped");
        }

        private static void KillAndRecover(Rig rig, IGlobalTriggerRegistration registration, string reason)
        {
            var hook = rig.Native.Current;
            hook.Kill(reason);
            Assert.True(hook.Closed.Wait(Timeout));
            rig.RunUiUntil(() => !registration.IsRegistered);
            rig.RunUiUntil(() =>
            {
                rig.Host.RetryStart();
                return registration.IsRegistered;
            });
        }

        // ---- recording ----

        [Fact]
        public void A_recording_in_progress_fails_when_the_hook_stops()
        {
            using var rig = new Rig { Trusted = true };
            var manager = new HotkeyManager(rig.Host, new SettingsHotkey());
            HotkeyGesture recorded = null;
            string failure = null;
            int changes = 0;
            manager.RecordingChanged += (_, _) => changes++;
            var session = manager.StartRecording(_ => { }, g => recorded = g, e => failure = e, out var error);
            Assert.NotNull(session);
            Assert.Null(error);
            Assert.True(manager.IsRecording);

            var hook = rig.Native.Current;
            hook.Kill("the event tap was invalidated");
            rig.RunUiUntil(() => !manager.IsRecording);

            Assert.Null(recorded);
            Assert.Contains("the event tap was invalidated", failure);
            // on macOS the hook comes back by itself
            Assert.DoesNotContain("Restart", failure);
            Assert.Equal(2, changes);
            // an end that came from the hook does not try to stop it again
            session.Dispose();
            Assert.Equal(2, changes);
            Assert.Equal(0, hook.RecordingsStopped);
        }

        [Fact]
        public void A_hook_failure_ends_the_live_recording_with_the_error_and_fails_every_registration()
        {
            using var rig = new Rig();
            var first = rig.Host.AddRegistration(1, ShiftA, () => { }, fireOnRepeat: false);
            var second = rig.Host.AddRegistration(2, CtrlB, () => { }, fireOnRepeat: false);
            var raised = new List<object>();
            first.StatusChanged += (s, _) => raised.Add(s);
            second.StatusChanged += (s, _) => raised.Add(s);

            HotkeyGesture recorded = null;
            string failure = null;
            var session = rig.Host.BeginSession(5, 0, _ => { }, g => recorded = g, e => failure = e);

            rig.Host.ApplyHookFailure("The global hotkey listener stopped.", new[] { first, second }, 0);

            Assert.Equal("The global hotkey listener stopped.", failure);
            Assert.Null(recorded);
            Assert.False(first.IsRegistered);
            Assert.Equal("The global hotkey listener stopped.", first.Error);
            Assert.False(second.IsRegistered);
            Assert.Equal(new object[] { first, second }, raised);

            // the session is over: a late Recorded for its token, or its disposal, does nothing more
            rig.Host.DeliverRecorded(5, CtrlShift1);
            session.Dispose();
            Assert.Null(recorded);
        }

        [Fact]
        public void A_hook_failure_with_no_recording_only_fails_the_registrations()
        {
            using var rig = new Rig();
            var registration = rig.Host.AddRegistration(1, ShiftA, () => { }, fireOnRepeat: false);

            rig.Host.ApplyHookFailure("stopped", new[] { registration }, 0);

            Assert.False(registration.IsRegistered);
            Assert.Equal("stopped", registration.Error);
        }

        // ---- the event thread's decisions ----

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
            using var rig = new Rig();
            var fired = 0;
            var registration = rig.Host.AddRegistration(7, ShiftA, () => fired++, fireOnRepeat: false);

            rig.Host.RunHotkey(registration);
            Assert.Equal(1, fired);

            registration.Dispose();
            rig.Host.RunHotkey(registration);
            Assert.Equal(1, fired);

            // nor does one whose id now belongs to a newer registration
            var newer = rig.Host.AddRegistration(7, ShiftA, () => { }, fireOnRepeat: false);
            rig.Host.RunHotkey(registration);
            Assert.Equal(1, fired);
            newer.Dispose();
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

            // a hook that comes back by itself (macOS) has nothing to restart
            var macOS = NativeHotkeyHost.StoppedError("the event tap was invalidated", restartsItself: true);
            Assert.Contains("the event tap was invalidated", macOS);
            Assert.DoesNotContain("Restart", macOS);
        }

        /// <summary>
        /// clowd_hotkeys as ffi.rs documents it: each started hook gets its own handle and queue;
        /// a hook that dies queues Stopped and closes its queue, so next_event reports -1 once
        /// drained; free ends the handle, and any later call on it is recorded as misuse.
        /// </summary>
        private sealed class FakeNative : INativeHotkeys
        {
            private readonly object _lock = new();
            private readonly Dictionary<IntPtr, FakeHook> _hooks = new();
            private int _nextHandle = 1;

            /// <summary>What every later <see cref="Start"/> returns.</summary>
            public volatile int StartResult = CH_OK;

            /// <summary>The message a failed <see cref="Start"/> writes to its error buffer.</summary>
            public volatile string StartError = "";
            public int StartCalls;
            public FakeHook Current;

            /// <summary>Run on the event thread after a hook signals <c>Closed</c>, before its last
            /// <see cref="NextEvent"/> returns: a slow pump exit, as on a loaded machine.</summary>
            public volatile Action AfterClosed;
            public readonly List<IntPtr> Freed = new();
            public readonly List<string> Misuse = new();

            public string EnsureLoaded() => null;

            public int Start(out IntPtr handle, byte[] errBuf)
            {
                lock (_lock)
                {
                    StartCalls++;
                    handle = IntPtr.Zero;
                    if (StartResult != CH_OK)
                    {
                        var bytes = Encoding.UTF8.GetBytes(StartError ?? "");
                        int length = Math.Min(bytes.Length, errBuf.Length - 1);
                        Array.Copy(bytes, errBuf, length);
                        errBuf[length] = 0;
                        return StartResult;
                    }

                    handle = new IntPtr(_nextHandle++);
                    Current = _hooks[handle] = new FakeHook(handle);
                    return CH_OK;
                }
            }

            private FakeHook Use(IntPtr handle, string call)
            {
                lock (_lock)
                {
                    if (_hooks.TryGetValue(handle, out var hook))
                        return hook;
                    Misuse.Add(call + " on " + handle);
                    throw new InvalidOperationException(call + " on a freed or unknown handle");
                }
            }

            public int Register(IntPtr handle, int key, uint modifiers, out uint id)
            {
                var hook = Use(handle, "register");
                lock (hook)
                {
                    id = 0;
                    if (hook.Dead)
                        return CH_ERR_STOPPED;
                    if (hook.Registered.ContainsValue((key, modifiers)))
                        return CH_ERR_DUPLICATE;
                    id = hook.NextId++;
                    hook.Registered[id] = (key, modifiers);
                    return CH_OK;
                }
            }

            public void Unregister(IntPtr handle, uint id)
            {
                var hook = Use(handle, "unregister");
                lock (hook)
                    hook.Registered.Remove(id);
            }

            public void SetObserve(IntPtr handle, bool observe) => Use(handle, "set_observe").Observing = observe;

            public int StartRecording(IntPtr handle, out uint session)
            {
                var hook = Use(handle, "start_recording");
                lock (hook)
                {
                    session = 0;
                    if (hook.Dead)
                        return CH_ERR_STOPPED;
                    session = ++hook.Sessions;
                    return CH_OK;
                }
            }

            public void StopRecording(IntPtr handle, uint session)
            {
                var hook = Use(handle, "stop_recording");
                Interlocked.Increment(ref hook.RecordingsStopped);
            }

            public int NextEvent(IntPtr handle, out HotkeyEventNative ev, uint timeoutMs)
            {
                var hook = Use(handle, "next_event");
                ev = default;
                if (hook.NextEventResult != 0)
                    return hook.NextEventResult;
                if (hook.Events.TryTake(out ev, (int)timeoutMs))
                    return 1;
                if (!hook.Events.IsCompleted)
                    return 0;
                hook.Closed.Set();
                AfterClosed?.Invoke();
                return -1;
            }

            public int StopReason(IntPtr handle, byte[] buf)
            {
                var bytes = Encoding.UTF8.GetBytes(Use(handle, "stop_reason").StopReason ?? "");
                int length = Math.Min(bytes.Length, buf.Length - 1);
                Array.Copy(bytes, buf, length);
                buf[length] = 0;
                return length;
            }

            public void Stop(IntPtr handle) => Use(handle, "stop").Die(null);

            public void Free(IntPtr handle)
            {
                lock (_lock)
                {
                    if (!_hooks.Remove(handle, out var hook))
                    {
                        Misuse.Add("free on " + handle);
                        return;
                    }
                    hook.Die(null);
                    Freed.Add(handle);
                }
            }
        }

        private sealed class FakeHook(IntPtr handle)
        {
            public readonly IntPtr Handle = handle;
            public readonly BlockingCollection<HotkeyEventNative> Events = new();
            public readonly Dictionary<uint, (int Key, uint Modifiers)> Registered = new();
            public readonly ManualResetEventSlim Closed = new();
            public uint NextId = 1;
            public uint Sessions;
            public int RecordingsStopped;
            public volatile bool Observing;
            public volatile bool Dead;
            public volatile string StopReason;

            /// <summary>When set, what every later <see cref="FakeNative.NextEvent"/> returns: the
            /// queue failing (a panic) rather than closing.</summary>
            public volatile int NextEventResult;

            public uint IdOf(HotkeyGesture gesture)
            {
                lock (this)
                {
                    foreach (var (id, value) in Registered)
                    {
                        if (value == ((int)gesture.Key, (uint)gesture.Modifiers))
                            return id;
                    }
                    throw new InvalidOperationException(gesture + " is not registered");
                }
            }

            public void Push(HotkeyEventNative ev) => Events.Add(ev);

            /// <summary>The hook dying on its own, as a revoked grant ends a tap.</summary>
            public void Kill(string reason) => Die(reason);

            public void Die(string reason)
            {
                lock (this)
                {
                    if (Dead)
                        return;
                    Dead = true;
                    if (reason != null)
                    {
                        StopReason = reason;
                        Events.Add(new HotkeyEventNative { Kind = CH_EVENT_STOPPED });
                    }
                    Events.CompleteAdding();
                }
            }
        }
    }
}
