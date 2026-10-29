using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Prisc.Config;
using Prisc.Interop;
using Prisc.UI;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="NativeHotkeyHost"/> over a fake supervised hook (the re-typed
    /// <see cref="INativeHotkeys"/> seam). The hook retries and re-registers by itself
    /// (prisc_hotkeys' supervisor, tested in Rust); what the host owns is the mapping of its Status
    /// events onto registrations, recordings, the prompt, the reports and the user text. A hook that
    /// cannot start for want of Accessibility asks for it (once per run, only on macOS, never for a
    /// stale grant or a revocation); a Running after a down state marks the waiting rows registered
    /// and raises RecordingAvailable; a hook that goes down fails a live recording. The UI thread is a
    /// queue the test drains; the event thread is real.
    /// </summary>
    public class NativeHotkeyHostTests
    {
        /// <summary>A capturer <c>settings_status</c> line, decoded as the supervisor receives it.</summary>
        private static CaptureStandbySupervisor.StandbyHotkeyStatus StandbyStatus(string line) =>
            CaptureStandbySupervisor.ReadHotkeyStatus(((Prisc.Ipc.CaptureEvent.SettingsStatus)Prisc.Ipc.CaptureCodec.DecodeEvent(line)).Hotkeys);

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly HotkeyGesture ShiftA = new(HotkeyKey.A, HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlB = new(HotkeyKey.B, HotkeyModifiers.Control);
        private static readonly HotkeyGesture CtrlShift1 = new(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        private static HotkeyEvent Status(HookState state, bool recoverable = true, string reason = "") =>
            new(HotkeysEventKind.Status, 0, 0, 0, 0, null,
                recoverable && state != HookState.Running ? HotkeysEventFlags.Recoverable : HotkeysEventFlags.None, state, reason);

        private static HotkeyEvent Event(HotkeysEventKind kind, uint id = 0, int key = 0, uint modifiers = 0, int rawCode = 0,
            string character = null, HotkeysEventFlags flags = HotkeysEventFlags.None) =>
            new(kind, id, key, modifiers, rawCode, character, flags, HookState.Running, "");

        private sealed class Rig : IDisposable
        {
            public readonly FakeHook Native = new();
            public readonly ConcurrentQueue<Action> Ui = new();
            public int Prompts;
            public int Creates;

            /// <summary>When set, what the hook's create throws.</summary>
            public Exception CreateThrows;

            /// <summary>What the host filed as handled errors, by operation.</summary>
            public readonly ConcurrentQueue<string> Reports = new();
            public readonly AccessibilityPrompt Prompt;
            public readonly NativeHotkeyHost Host;

            /// <param name="initial">The state the hook's first start left (its first event).</param>
            public Rig(HookState initial = HookState.Running, bool isMacOS = true, AccessibilityPrompt prompt = null)
            {
                Native.Push(Status(initial));
                Prompt = prompt ?? new AccessibilityPrompt(isMacOS, () => Prompts++, Ui.Enqueue);
                Host = new NativeHotkeyHost(() =>
                {
                    Creates++;
                    if (CreateThrows != null)
                        throw CreateThrows;
                    return Native;
                }, Ui.Enqueue, Prompt, (_, operation) => Reports.Enqueue(operation));
            }

            /// <summary>The hook reports a new state, as its supervisor does.</summary>
            public void Report(HookState state, bool recoverable = true, string reason = "") =>
                Native.Push(Status(state, recoverable, reason));

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
            using var rig = new Rig(HookState.PermissionMissing);

            var a = rig.Host.RegisterTrigger(ShiftA, () => { });
            var b = rig.Host.RegisterTrigger(CtrlB, () => { });
            var session = rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out var error);
            using var observer = rig.Host.ObserveKeys(_ => { });
            rig.RunUi();

            Assert.Equal(1, rig.Prompts);
            Assert.False(a.IsRegistered);
            Assert.False(b.IsRegistered);
            Assert.Equal(HotkeyStatusText.PermissionMessage, a.Error);
            Assert.Null(session);
            Assert.Equal(a.Error, error);
            // held by the hook for when it comes back
            Assert.Equal(2, rig.Native.Registered.Count);
        }

        [Fact]
        public void The_shell_and_the_standby_capturer_share_one_prompt()
        {
            using var rig = new Rig(HookState.PermissionMissing);
            var permission = StandbyStatus("""
                {"type":"settings_status","hotkeys":{"main":[{"gesture":"F12","active":false,"error":"x","error_code":"permission"}],
                            "window":[],"monitor":[]}}
                """);

            // warm capture with no shell-owned gesture: the capturer's report is the only cue
            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt);
            rig.RunUi();
            Assert.Equal(1, rig.Prompts);

            CaptureStandbySupervisor.PromptIfNeeded(permission, rig.Prompt);
            rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();
            Assert.Equal(1, rig.Prompts);
        }

        [Theory]
        [InlineData("duplicate")]
        [InlineData("stale_grant")]
        public void Only_a_permission_status_from_the_capturer_prompts(string code)
        {
            // a stale grant included: the prompt would show nothing, yet spend this run's only one,
            // which the grant missing for real (once the user removes Prisc from the list) still needs
            using var rig = new Rig();
            var other = StandbyStatus($$$"""
                {"type":"settings_status","hotkeys":{"main":[{"gesture":"F12","active":false,"error":"x","error_code":"{{{code}}}"}],
                            "window":[],"monitor":[]}}
                """);

            CaptureStandbySupervisor.PromptIfNeeded(other, rig.Prompt);
            rig.RunUi();

            Assert.Equal(0, rig.Prompts);
        }

        [Fact]
        public void Nothing_prompts_off_macos()
        {
            using var rig = new Rig(HookState.PermissionMissing, isMacOS: false);

            rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.Prompt.RequestOnce();
            rig.RunUi();

            Assert.Equal(0, rig.Prompts);
        }

        [Fact]
        public void A_revoked_grant_asks_for_nothing_and_files_no_report()
        {
            // the user switching Accessibility off under a running hook: expected, and not a defect
            using var rig = new Rig();
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            Assert.True(registration.IsRegistered);

            rig.Report(HookState.PermissionMissing, reason: "the Accessibility permission was revoked");
            rig.RunUiUntil(() => !registration.IsRegistered);

            Assert.Equal(HotkeyStatusText.PermissionMessage, registration.Error);
            Assert.Equal(0, rig.Prompts);
            Assert.Empty(rig.Reports);
        }

        [Fact]
        public void A_stale_grant_says_how_to_fix_it_and_asks_for_nothing_until_the_grant_is_really_gone()
        {
            // the permission reads as granted, yet the crate found the tap stripped of key events: a
            // grant macOS still lists but no longer honours, which no prompt fixes
            using var rig = new Rig(HookState.PermissionStale);
            int available = 0;
            rig.Host.RecordingAvailable += (_, _) => available++;
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();

            // not "grant Accessibility": the System page shows it as granted
            Assert.Equal(HotkeyStatusText.StaleGrantMessage, registration.Error);
            Assert.Contains("Privacy & Security → Accessibility", registration.Error);
            Assert.Contains("remove Prisc", registration.Error);
            Assert.Contains("add it again", registration.Error);
            Assert.Equal(0, rig.Prompts);

            // the user removes Prisc from the list, as the text says: now it is missing for real
            rig.Report(HookState.PermissionMissing);
            rig.RunUiUntil(() => registration.Error == HotkeyStatusText.PermissionMessage);
            Assert.Equal(1, rig.Prompts);

            // and adds it again: the hook comes back by itself
            rig.Report(HookState.Running);
            rig.RunUiUntil(() => registration.IsRegistered);
            Assert.Equal(1, available);
        }

        [Fact]
        public void The_permission_message_points_to_the_system_page_and_asks_for_no_restart()
        {
            using var rig = new Rig(HookState.PermissionMissing);

            var error = rig.Host.RegisterTrigger(ShiftA, () => { }).Error;

            Assert.Equal(HotkeyStatusText.PermissionMessage, error);
            Assert.Contains("System", error);
            Assert.DoesNotContain("General", error);
            Assert.DoesNotContain("restart", error);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_permission_texts_name_the_page_as_the_nav_shows_it(bool permissionMissing)
        {
            // the nav renames the System entry "Permission Needed" while a permission is missing,
            // which is when these texts show, but not when only the hook's own check says no
            var label = MacPermissions.NavLabel(permissionMissing);

            Assert.Contains(label, HotkeyStatusText.PermissionMessage);
            Assert.Contains(label, Prisc.UI.Dialogs.ColorPicker.EyedropperButton.PermissionTip);
        }

        // ---- recovery ----

        [Fact]
        public void Granting_accessibility_brings_the_hotkeys_back_without_a_restart()
        {
            using var rig = new Rig(HookState.PermissionMissing);
            int available = 0;
            rig.Host.RecordingAvailable += (_, _) => available++;
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
            // the observe switch is the hook's, kept for the hook that comes back
            Assert.True(rig.Native.Observing);

            rig.Report(HookState.Running);
            rig.RunUiUntil(() => shortcut.IsRegistered && media.IsRegistered);

            Assert.Equal("", shortcut.Error);
            Assert.Equal(1, available);
            Assert.Equal(1, rig.Creates);

            // and the hook's events arrive: the hotkey fires, the observer hears keys
            rig.Native.Push(Event(HotkeysEventKind.Hotkey, id: rig.Native.IdOf(new HotkeyGesture(HotkeyKey.PrintScreen))));
            rig.Native.Push(Event(HotkeysEventKind.Key, rawCode: 7, character: "a", flags: HotkeysEventFlags.Down));
            rig.RunUiUntil(() => fired == 1 && observed.Count == 1);
            Assert.True(observed.TryPeek(out var key) && key.IsDown && key.RawCode == 7 && key.Character == "a");
        }

        [Fact]
        public void The_hook_coming_back_says_recording_works_again()
        {
            // what the Hotkeys page drops its "grant Accessibility" text on
            using var rig = new Rig(HookState.PermissionMissing);
            int available = 0;
            rig.Host.RecordingAvailable += (_, _) => available++;
            Assert.Null(rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out _));

            rig.Report(HookState.Failed, reason: "could not install the keyboard hook: could not create the event tap");
            rig.Report(HookState.Running);
            rig.RunUiUntil(() => available == 1);

            using var session = rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out var error);
            Assert.NotNull(session);
            Assert.Null(error);
        }

        [Fact]
        public void A_retry_that_fails_differently_tells_the_waiting_registrations()
        {
            using var rig = new Rig(HookState.PermissionMissing);
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.RunUi();
            Assert.Equal(HotkeyStatusText.PermissionMessage, registration.Error);

            // granted, but the tap still cannot be made: the rows must stop asking for the grant
            rig.Report(HookState.Failed, reason: "could not install the keyboard hook: could not create the event tap");
            rig.RunUiUntil(() => registration.Error != HotkeyStatusText.PermissionMessage);

            Assert.False(registration.IsRegistered);
            Assert.Equal("Could not start the global hotkey listener: could not install the keyboard hook: could not create the event tap",
                registration.Error);
            // and a registration made now says the same
            Assert.Equal(registration.Error, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            Assert.Single(rig.Reports, r => r == "hotkey.hook-start");

            // still held: the next good start takes it along
            rig.Report(HookState.Running);
            rig.RunUiUntil(() => registration.IsRegistered);
        }

        [Fact]
        public void A_registration_disposed_while_the_hook_is_down_does_not_come_back()
        {
            using var rig = new Rig(HookState.PermissionMissing);
            var kept = rig.Host.RegisterTrigger(ShiftA, () => { });
            rig.Host.RegisterTrigger(CtrlB, () => { }).Dispose();
            Assert.Single(rig.Native.Registered);

            rig.Report(HookState.Running);
            rig.RunUiUntil(() => kept.IsRegistered);
        }

        [Fact]
        public void A_hook_that_stops_comes_back_with_its_registrations()
        {
            using var rig = new Rig();
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });
            Assert.True(registration.IsRegistered);

            rig.Report(HookState.Stopped, reason: "the event tap was invalidated");
            rig.RunUiUntil(() => !registration.IsRegistered);
            Assert.Contains("the event tap was invalidated", registration.Error);
            // it comes back by itself: no restart asked for
            Assert.DoesNotContain("Restart", registration.Error);

            rig.Report(HookState.Running);
            rig.RunUiUntil(() => registration.IsRegistered);

            // the registration still lives on the hook under its id, and leaves it when disposed
            registration.Dispose();
            Assert.Empty(rig.Native.Registered);

            rig.Host.Dispose();
            Assert.Equal(1, rig.Native.Disposed);
            Assert.Empty(rig.Native.Misuse);
        }

        [Fact]
        public void A_hook_that_stops_for_good_says_to_restart()
        {
            // Windows: a lost hook is this process's own, so the supervisor does not retry it
            using var rig = new Rig();
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            rig.Report(HookState.Stopped, recoverable: false, reason: "the message loop failed");
            rig.RunUiUntil(() => !registration.IsRegistered);

            Assert.Contains("the message loop failed", registration.Error);
            Assert.Contains("Restart Prisc", registration.Error);
            Assert.Equal(registration.Error, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
        }

        [Fact]
        public void A_failed_event_queue_stops_the_hook_for_good()
        {
            // not the grant: the queue itself broke, with the hook possibly still swallowing
            using var rig = new Rig();
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            rig.Native.NextEventThrows = new InteropException(InteropStatus.Panic, 0, "prisc_hotkeys_next_event", "boom");
            rig.RunUiUntil(() => !registration.IsRegistered);

            Assert.True(rig.Native.Stopped);
            Assert.Contains("Restart Prisc", registration.Error);
            Assert.Equal(registration.Error, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            Assert.Single(rig.Reports, r => r == "hotkey.events");
            // nothing brings it back: the queue is gone
            Assert.Equal(1, rig.Creates);
        }

        [Fact]
        public void A_refused_gesture_says_why_at_once()
        {
            using var rig = new Rig();
            rig.Native.Refuse = GestureRefusal.UnsupportedKey;
            var unsupported = rig.Host.RegisterTrigger(ShiftA, () => { });
            Assert.False(unsupported.IsRegistered);
            Assert.Equal($"The key '{ShiftA}' cannot be used as a global hotkey.", unsupported.Error);

            rig.Native.Refuse = GestureRefusal.Duplicate;
            Assert.Equal(HotkeyStatusText.DuplicateMessage, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            rig.Native.Refuse = GestureRefusal.ModifierOnly;
            Assert.Equal(HotkeyStatusText.ModifierKeyMessage, rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            Assert.Empty(rig.Native.Registered);
        }

        // ---- error reports ----

        [Fact]
        public void A_hook_that_keeps_stopping_is_reported_once()
        {
            using var rig = new Rig();
            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            for (int round = 0; round < 3; round++)
            {
                rig.Report(HookState.Stopped, reason: "the event tap was invalidated");
                rig.RunUiUntil(() => !registration.IsRegistered);
                rig.Report(HookState.Running);
                rig.RunUiUntil(() => registration.IsRegistered);
            }

            Assert.Single(rig.Reports, r => r == "hotkey.stopped");
        }

        [Fact]
        public void A_start_that_throws_reports_why()
        {
            using var rig = new Rig();
            rig.CreateThrows = new EntryPointNotFoundException("prisc_hotkeys_create");

            var registration = rig.Host.RegisterTrigger(ShiftA, () => { });

            Assert.Contains("prisc_hotkeys_create", registration.Error);
            Assert.Single(rig.Reports, r => r == "hotkey.hook-start");
            // tried once: a hook restarts itself, so a failed create has nothing to retry
            rig.Host.RegisterTrigger(CtrlB, () => { });
            Assert.Equal(1, rig.Creates);
        }

        [Fact]
        public void No_backend_says_so_without_a_report()
        {
            using var rig = new Rig();
            rig.CreateThrows = new InteropException(InteropStatus.Unsupported, 0, "prisc_hotkeys_create", "");

            Assert.Equal(HotkeyStatusText.UnsupportedMessage, rig.Host.RegisterTrigger(ShiftA, () => { }).Error);
            Assert.Empty(rig.Reports);
        }

        // ---- recording ----

        [Fact]
        public void A_recording_in_progress_fails_when_the_hook_stops()
        {
            using var rig = new Rig();
            var manager = new HotkeyManager(rig.Host, new SettingsHotkey());
            HotkeyGesture recorded = null;
            string failure = null;
            int changes = 0;
            manager.RecordingChanged += (_, _) => changes++;
            var session = manager.StartRecording(_ => { }, g => recorded = g, e => failure = e, out var error);
            Assert.NotNull(session);
            Assert.Null(error);
            Assert.True(manager.IsRecording);

            rig.Report(HookState.Stopped, reason: "the event tap was invalidated");
            rig.RunUiUntil(() => !manager.IsRecording);

            Assert.Null(recorded);
            Assert.Contains("the event tap was invalidated", failure);
            // on macOS the hook comes back by itself
            Assert.DoesNotContain("Restart", failure);
            Assert.Equal(2, changes);
            // the failure cancels the arm on the hook once (in case the hook is already back);
            // disposing the ended session does nothing more
            Assert.Equal(1, rig.Native.RecordingsStopped);
            session.Dispose();
            Assert.Equal(2, changes);
            Assert.Equal(1, rig.Native.RecordingsStopped);
        }

        [Fact]
        public void A_recording_armed_on_a_hook_that_already_came_back_is_cancelled_when_its_down_state_arrives()
        {
            // the hook died and its supervisor restarted it at once: both Statuses are still queued
            // when the recording starts, so the C# state says Running and the arm lands on the new hook
            using var rig = new Rig();
            string failure = null;
            var session = rig.Host.StartRecording(_ => { }, _ => { }, e => failure = e, out var error);
            Assert.NotNull(session);
            Assert.Null(error);
            uint token = rig.Native.Sessions;

            rig.Report(HookState.Stopped, reason: "the event tap was invalidated");
            rig.Report(HookState.Running);
            rig.RunUiUntil(() => failure != null);

            Assert.Contains("the event tap was invalidated", failure);
            Assert.Equal(new[] { token }, rig.Native.StoppedSessions.ToArray());
            session.Dispose();
            Assert.Equal(new[] { token }, rig.Native.StoppedSessions.ToArray());
        }

        [Fact]
        public void A_recording_is_delivered_by_its_token_and_a_cancel_reaches_the_hook()
        {
            using var rig = new Rig();
            HotkeyGesture recorded = null;
            var modifiers = new List<HotkeyModifiers>();
            var session = rig.Host.StartRecording(modifiers.Add, g => recorded = g, _ => { }, out _);
            uint token = rig.Native.Sessions;

            rig.Native.Push(Event(HotkeysEventKind.RecordingModifiers, id: token, modifiers: (uint)HotkeyModifiers.Control));
            // a stale token (a cancelled session's) is dropped
            rig.Native.Push(Event(HotkeysEventKind.Recorded, id: token + 7, key: (int)HotkeyKey.B));
            rig.Native.Push(Event(HotkeysEventKind.Recorded, id: token, key: (int)HotkeyKey.D1,
                modifiers: (uint)(HotkeyModifiers.Control | HotkeyModifiers.Shift)));
            rig.RunUiUntil(() => recorded != null);

            Assert.Equal(CtrlShift1, recorded);
            Assert.Equal(new[] { HotkeyModifiers.Control }, modifiers);
            session.Dispose();
            Assert.Equal(0, rig.Native.RecordingsStopped);

            // a cancel before anything was recorded reaches the hook
            using (rig.Host.StartRecording(_ => { }, _ => { }, _ => { }, out _))
            {
            }
            Assert.Equal(1, rig.Native.RecordingsStopped);
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

            rig.Host.ApplyHookFailure("The global hotkey listener stopped.", new[] { first, second }, 1);

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
        public void A_down_state_spares_a_recording_started_after_it()
        {
            // the hook went down and came back before the UI heard: a recording started on the hook
            // that is back must not end with the down state's news
            using var rig = new Rig();
            string failure = null;
            using var session = rig.Host.BeginSession(5, 3, _ => { }, _ => { }, e => failure = e);

            rig.Host.ApplyHookFailure("stopped", Array.Empty<NativeHotkeyHost.Registration>(), 3);

            Assert.Null(failure);
        }

        [Fact]
        public void A_hook_failure_with_no_recording_only_fails_the_registrations()
        {
            using var rig = new Rig();
            var registration = rig.Host.AddRegistration(1, ShiftA, () => { }, fireOnRepeat: false);

            rig.Host.ApplyHookFailure("stopped", new[] { registration }, 1);

            Assert.False(registration.IsRegistered);
            Assert.Equal("stopped", registration.Error);
        }

        // ---- the event thread's decisions ----

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
        public void Disposing_stops_the_hook_drains_its_queue_and_frees_it_once()
        {
            var rig = new Rig();
            rig.Host.RegisterTrigger(ShiftA, () => { });

            rig.Host.Dispose();
            rig.Host.Dispose();

            Assert.True(rig.Native.Stopped);
            Assert.Equal(1, rig.Native.Disposed);
            Assert.Empty(rig.Reports);
            Assert.Equal("The hotkey host has been shut down.", rig.Host.RegisterTrigger(CtrlB, () => { }).Error);
            Assert.Empty(rig.Native.Misuse);
        }

        [Fact]
        public void The_stopped_message_says_how_to_get_hotkeys_back()
        {
            Assert.Contains("Restart Prisc", HotkeyStatusText.StoppedError("the event tap was invalidated"));
            Assert.Contains("the event tap was invalidated", HotkeyStatusText.StoppedError("the event tap was invalidated"));
            Assert.Contains("Restart Prisc", HotkeyStatusText.StoppedError(""));

            // a hook that comes back by itself (macOS) has nothing to restart
            var macOS = HotkeyStatusText.StoppedError("the event tap was invalidated", restartsItself: true);
            Assert.Contains("the event tap was invalidated", macOS);
            Assert.DoesNotContain("Restart", macOS);
        }

        /// <summary>
        /// One supervised hook as GlobalKeyboardHook presents it: registrations held whatever the
        /// state, a queue the test pushes events into, Closed once stopped and drained, and any call
        /// after Dispose recorded as misuse.
        /// </summary>
        private sealed class FakeHook : INativeHotkeys
        {
            public readonly BlockingCollection<HotkeyEvent> Events = new();
            public readonly Dictionary<uint, (int Key, uint Modifiers)> Registered = new();
            public readonly List<string> Misuse = new();
            public uint NextId = 1;
            public uint Sessions;
            public int RecordingsStopped;
            public readonly ConcurrentQueue<uint> StoppedSessions = new();
            public int Disposed;
            public volatile bool Observing;
            public volatile bool Stopped;

            /// <summary>When set, what every later register answers.</summary>
            public GestureRefusal Refuse = GestureRefusal.None;

            /// <summary>When set, what every later <see cref="NextEvent"/> throws: the queue failing.</summary>
            public volatile Exception NextEventThrows;

            /// <summary>The hook's own state, from the last Status pushed.</summary>
            private volatile bool _running = true;

            public void Push(HotkeyEvent ev)
            {
                if (ev.Kind == HotkeysEventKind.Status)
                    _running = ev.HookState == HookState.Running;
                Events.Add(ev);
            }

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

            private void Use(string call)
            {
                if (Volatile.Read(ref Disposed) > 0)
                {
                    lock (Misuse)
                        Misuse.Add(call);
                    throw new ObjectDisposedException(call);
                }
            }

            public bool TryRegister(int key, uint modifiers, out uint id, out GestureRefusal refusal)
            {
                Use("register");
                lock (this)
                {
                    id = 0;
                    refusal = Refuse;
                    if (refusal != GestureRefusal.None)
                        return false;
                    id = NextId++;
                    Registered[id] = (key, modifiers);
                    return true;
                }
            }

            public void Unregister(uint id)
            {
                Use("unregister");
                lock (this)
                    Registered.Remove(id);
            }

            public void SetObserve(bool observe)
            {
                Use("set_observe");
                Observing = observe;
            }

            public bool TryStartRecording(out uint session)
            {
                Use("start_recording");
                session = 0;
                if (!_running || Stopped)
                    return false;
                session = ++Sessions;
                return true;
            }

            public void StopRecording(uint session)
            {
                Use("stop_recording");
                Interlocked.Increment(ref RecordingsStopped);
                StoppedSessions.Enqueue(session);
            }

            public InteropStatus NextEvent(uint timeoutMs, out HotkeyEvent hotkeyEvent)
            {
                Use("next_event");
                hotkeyEvent = default;
                if (NextEventThrows is { } failure)
                    throw failure;
                if (Events.TryTake(out hotkeyEvent, (int)Math.Min(timeoutMs, 50)))
                    return InteropStatus.Ok;
                return Events.IsCompleted ? InteropStatus.Closed : InteropStatus.Timeout;
            }

            public void Stop()
            {
                Use("stop");
                Stopped = true;
                if (!Events.IsAddingCompleted)
                    Events.CompleteAdding();
            }

            public void Dispose() => Interlocked.Increment(ref Disposed);
        }
    }
}
