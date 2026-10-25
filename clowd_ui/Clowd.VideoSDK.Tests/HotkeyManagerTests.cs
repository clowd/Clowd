using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd;
using Clowd.Config;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Every action holds an ordered list of gestures, each registered on its own. A gesture
    /// belongs to one action: assigning one another action already holds moves it — the previous
    /// owner keeps the rest of its list, and its OS registration is released before the new one is
    /// made.
    /// </summary>
    public class HotkeyManagerTests
    {
        private static readonly HotkeyGesture PrintScreen = new(HotkeyKey.PrintScreen);
        private static readonly HotkeyGesture CtrlShift1 = new(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlShift2 = new(HotkeyKey.D2, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlShift3 = new(HotkeyKey.D3, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        private static (HotkeyManager Manager, FakeHost Host, SettingsHotkey Settings) Create(bool wireActions = true)
        {
            var host = new FakeHost();
            // the Windows defaults (the PrintScreen family) on every OS: these tests take PrtScr
            // from Capture Region, and count the four defaults' registrations
            var settings = new SettingsHotkey(OSPlatform.Windows);
            var manager = new HotkeyManager(host, settings);
            if (wireActions)
            {
                foreach (var id in Enum.GetValues<HotkeyId>())
                    manager.SetAction(id, () => { });
            }
            return (manager, host, settings);
        }

        [Fact]
        public void Taking_a_gesture_already_in_use_unsets_it_on_the_other_hotkey()
        {
            var (manager, host, settings) = Create();
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            var draw = manager.GetEntry(HotkeyId.DrawOnScreen);
            var regionChanged = false;
            region.PropertyChanged += (_, e) => regionChanged |= e.PropertyName == nameof(HotkeyEntry.Gestures);

            Assert.Equal(GestureList.Of(PrintScreen), region.Gestures);

            draw.AddGesture(PrintScreen);

            Assert.Equal(GestureList.Of(PrintScreen), draw.Gestures);
            Assert.Empty(region.Gestures);
            Assert.Empty(region.Shortcuts);
            Assert.Empty(settings.CaptureRegionShortcut);
            Assert.True(regionChanged);
            Assert.Single(host.Live, r => Equals(r.Gesture, PrintScreen));
        }

        [Fact]
        public void The_other_hotkey_keeps_the_rest_of_its_gestures()
        {
            var (manager, host, _) = Create();
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            region.AddGesture(CtrlShift1);
            Assert.Equal(GestureList.Of(PrintScreen, CtrlShift1), region.Gestures);

            manager.GetEntry(HotkeyId.ColorWheel).AddGesture(PrintScreen);

            Assert.Equal(GestureList.Of(CtrlShift1), region.Gestures);
            Assert.True(region.Shortcuts.Single().IsRegistered);
            Assert.Single(host.Live, r => Equals(r.Gesture, PrintScreen));
            Assert.Single(host.Live, r => Equals(r.Gesture, CtrlShift1));
        }

        [Fact]
        public void Distinct_gestures_leave_other_hotkeys_alone()
        {
            var (manager, _, settings) = Create(wireActions: false);
            var draw = manager.GetEntry(HotkeyId.DrawOnScreen);

            draw.AddGesture(new HotkeyGesture(HotkeyKey.D, HotkeyModifiers.Control | HotkeyModifiers.Shift));
            draw.RemoveGestureAt(0);

            Assert.Equal(GestureList.Of(PrintScreen), settings.CaptureRegionShortcut);
            Assert.Equal(GestureList.Of(new HotkeyGesture(HotkeyKey.PrintScreen, HotkeyModifiers.Control)), settings.CaptureFullscreenShortcut);
        }

        [Fact]
        public void Add_replace_and_remove_edit_the_list_in_order_and_register_every_gesture()
        {
            var (manager, host, settings) = Create();
            var editor = manager.GetEntry(HotkeyId.NewImageEditor);
            Assert.Empty(editor.Gestures);

            editor.AddGesture(CtrlShift1);
            editor.AddGesture(CtrlShift2);
            Assert.Equal(GestureList.Of(CtrlShift1, CtrlShift2), settings.NewImageEditorShortcut);

            // a gesture the action already has: no-op, wherever it is
            var shortcuts = editor.Shortcuts;
            editor.AddGesture(CtrlShift1);
            editor.ReplaceGesture(1, CtrlShift1);
            Assert.Same(shortcuts, editor.Shortcuts);
            Assert.Equal(GestureList.Of(CtrlShift1, CtrlShift2), editor.Gestures);

            editor.ReplaceGesture(0, CtrlShift3);
            Assert.Equal(GestureList.Of(CtrlShift3, CtrlShift2), editor.Gestures);
            Assert.Equal(editor.Gestures, editor.Shortcuts.Select(s => s.Gesture));
            Assert.All(editor.Shortcuts, s => Assert.True(s.IsRegistered));
            Assert.DoesNotContain(host.Live, r => Equals(r.Gesture, CtrlShift1));
            Assert.Single(host.Live, r => Equals(r.Gesture, CtrlShift3));

            editor.RemoveGestureAt(1);
            Assert.Equal(GestureList.Of(CtrlShift3), settings.NewImageEditorShortcut);
            Assert.DoesNotContain(host.Live, r => Equals(r.Gesture, CtrlShift2));
        }

        [Fact]
        public void A_gesture_survives_edits_to_the_rest_of_the_list_with_the_same_status_object()
        {
            var (manager, _, _) = Create();
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            var printScreen = region.Shortcuts.Single();

            region.AddGesture(CtrlShift1);
            manager.SetEnabled(HotkeyId.CaptureRegion, false);
            manager.SetEnabled(HotkeyId.CaptureRegion, true);

            Assert.Same(printScreen, region.Shortcuts[0]);
            Assert.True(printScreen.IsRegistered);
        }

        [Fact]
        public void Replacing_with_a_gesture_takes_it_from_a_hidden_feature_switched_row()
        {
            var (manager, host, settings) = Create();
            settings.FileUploadShortcut = GestureList.Of(CtrlShift1, CtrlShift2);
            manager.Refresh();
            manager.SetEnabled(HotkeyId.FileUpload, false);
            Assert.DoesNotContain(host.Live, r => Equals(r.Gesture, CtrlShift1));

            manager.GetEntry(HotkeyId.CaptureRegion).ReplaceGesture(0, CtrlShift1);

            Assert.Equal(GestureList.Of(CtrlShift1), settings.CaptureRegionShortcut);
            // otherwise it would collide the moment uploads come back on
            Assert.Equal(GestureList.Of(CtrlShift2), settings.FileUploadShortcut);
        }

        [Fact]
        public void Each_gesture_carries_its_own_status_and_the_first_registered_one_is_exposed()
        {
            var (manager, host, _) = Create();
            host.Refuse(CtrlShift1, "Taken by another app.");
            var wheel = manager.GetEntry(HotkeyId.ColorWheel);
            Assert.Null(wheel.FirstRegisteredGesture);

            var changes = 0;
            manager.Changed += (_, _) => changes++;
            wheel.AddGesture(CtrlShift1);
            wheel.AddGesture(CtrlShift2);

            Assert.False(wheel.Shortcuts[0].IsRegistered);
            Assert.Equal("Taken by another app.", wheel.Shortcuts[0].Error);
            Assert.True(wheel.Shortcuts[1].IsRegistered);
            Assert.Equal("", wheel.Shortcuts[1].Error);
            Assert.Equal(CtrlShift2, wheel.FirstRegisteredGesture);
            Assert.True(changes > 0);

            // statuses also change on their own (the hook failing to start, macOS permission)
            var raised = new List<string>();
            wheel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            changes = 0;
            host.Live.Single(r => Equals(r.Gesture, CtrlShift2)).Fail("Hook stopped.");

            Assert.False(wheel.Shortcuts[1].IsRegistered);
            Assert.Equal("Hook stopped.", wheel.Shortcuts[1].Error);
            Assert.Null(wheel.FirstRegisteredGesture);
            Assert.Contains(nameof(HotkeyEntry.FirstRegisteredGesture), raised);
            Assert.True(changes > 0);
        }

        [Fact]
        public void A_switched_off_feature_unregisters_its_gestures_but_keeps_them()
        {
            var (manager, host, settings) = Create();
            var video = manager.GetEntry(HotkeyId.NewVideoEditor);
            video.AddGesture(CtrlShift1);
            video.AddGesture(CtrlShift2);
            Assert.Equal(2, host.Live.Count(r => Equals(r.Gesture, CtrlShift1) || Equals(r.Gesture, CtrlShift2)));

            manager.SetEnabled(HotkeyId.NewVideoEditor, false);

            Assert.False(video.IsEnabled);
            Assert.DoesNotContain(host.Live, r => Equals(r.Gesture, CtrlShift1) || Equals(r.Gesture, CtrlShift2));
            Assert.All(video.Shortcuts, s => Assert.False(s.IsRegistered));
            Assert.All(video.Shortcuts, s => Assert.Equal("This feature is turned off in settings.", s.Error));
            Assert.Null(video.FirstRegisteredGesture);
            Assert.Equal(GestureList.Of(CtrlShift1, CtrlShift2), settings.NewVideoEditorShortcut);

            manager.SetEnabled(HotkeyId.NewVideoEditor, true);

            Assert.True(video.IsEnabled);
            Assert.All(video.Shortcuts, s => Assert.True(s.IsRegistered));
            Assert.Equal(CtrlShift1, video.FirstRegisteredGesture);
        }

        [Fact]
        public void The_tool_window_actions_map_to_their_settings_and_ship_without_gestures()
        {
            var (manager, host, _) = Create();

            Assert.Equal(nameof(SettingsHotkey.ColorWheelShortcut), manager.GetEntry(HotkeyId.ColorWheel).SettingsProperty);
            Assert.Equal(nameof(SettingsHotkey.NewImageEditorShortcut), manager.GetEntry(HotkeyId.NewImageEditor).SettingsProperty);
            Assert.Equal(nameof(SettingsHotkey.NewVideoEditorShortcut), manager.GetEntry(HotkeyId.NewVideoEditor).SettingsProperty);
            Assert.Same(manager.GetEntry(HotkeyId.ColorWheel), manager.GetEntryForProperty(nameof(SettingsHotkey.ColorWheelShortcut)));

            foreach (var id in new[] { HotkeyId.ColorWheel, HotkeyId.NewImageEditor, HotkeyId.NewVideoEditor })
                Assert.Empty(manager.GetEntry(id).Gestures);

            // the four defaults, one registration each, and nothing else
            Assert.Equal(4, host.Live.Count);

            // and every action has an entry, so the page can show a row for each
            Assert.Equal(Enum.GetValues<HotkeyId>().Order(), manager.Entries.Select(e => e.Id).Order());
        }

        [Fact]
        public void Statuses_reported_by_the_capturer_land_on_the_matching_gestures()
        {
            var (manager, host, _) = Create(wireActions: false);
            // warm capture: the standby capturer, not this process's hook, owns the screenshot gestures
            manager.SetAction(HotkeyId.CaptureRegion, null);
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            region.AddGesture(CtrlShift1);
            region.AddGesture(CtrlShift2);
            Assert.Empty(host.Live);
            Assert.All(region.Shortcuts, s => Assert.False(s.IsRegistered));

            manager.ApplyExternalStatus(HotkeyId.CaptureRegion, new[]
            {
                new ExternalGestureStatus("Control+Shift+D1", false, "duplicate gesture"),
                new ExternalGestureStatus("PrintScreen", true, ""),
                // not one of ours (a report for a superseded settings snapshot): ignored
                new ExternalGestureStatus("Alt+F4", true, ""),
            });

            Assert.True(region.Shortcuts[0].IsRegistered);
            Assert.False(region.Shortcuts[1].IsRegistered);
            Assert.Equal("duplicate gesture", region.Shortcuts[1].Error);
            // no report yet: left as it was until the next one arrives
            Assert.False(region.Shortcuts[2].IsRegistered);
            Assert.Equal("", region.Shortcuts[2].Error);
            Assert.Equal(PrintScreen, region.FirstRegisteredGesture);

            // and once this process owns the action again, a stale capturer report must not stomp it
            host.Refuse(PrintScreen, "Taken by another app.");
            manager.SetAction(HotkeyId.CaptureRegion, () => { });
            manager.ApplyExternalStatus(HotkeyId.CaptureRegion, new[] { new ExternalGestureStatus("PrintScreen", true, "") });
            Assert.False(region.Shortcuts[0].IsRegistered);
        }

        [Fact]
        public void A_capturer_permission_failure_shows_the_same_text_as_the_shells_own()
        {
            var (manager, _, _) = Create(wireActions: false);
            manager.SetAction(HotkeyId.CaptureRegion, null);
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            region.AddGesture(CtrlShift1);
            region.AddGesture(CtrlShift2);

            // keyed off the stable code, not the crate's wording (CAPTURE_PROTOCOL.md)
            manager.ApplyExternalStatus(HotkeyId.CaptureRegion, new[]
            {
                new ExternalGestureStatus("PrintScreen", false, "reworded crate text", CaptureStandbySupervisor.PermissionErrorCode),
                new ExternalGestureStatus("Control+Shift+D1", false, "duplicate gesture", "duplicate"),
                // a capturer that predates error_code, or a code the shell does not know: shown as sent
                new ExternalGestureStatus("Control+Shift+D2", false, "some new failure"),
            });

            Assert.Equal(NativeHotkeyHost.PermissionMessage, region.Shortcuts[0].Error);
            Assert.Equal("duplicate gesture", region.Shortcuts[1].Error);
            Assert.Equal("some new failure", region.Shortcuts[2].Error);
        }

        [Fact]
        public void The_standby_command_line_repeats_each_flag_once_per_gesture_in_order()
        {
            var root = new SettingsRoot { Hotkeys = new SettingsHotkey(OSPlatform.Windows) };
            root.General.UseSystemAccentColor = false;
            root.Hotkeys.CaptureRegionShortcut = GestureList.Of(PrintScreen, CtrlShift1);
            root.Hotkeys.CaptureActiveShortcut = GestureList.Empty;

            var args = CaptureArguments.BuildStandby(@"C:\sessions", root).ToList();

            Assert.Equal(new[] { "PrintScreen", "Control+Shift+D1" }, ValuesOf("--hk-main"));
            Assert.Empty(ValuesOf("--hk-window"));
            Assert.Equal(new[] { "Control+PrintScreen" }, ValuesOf("--hk-monitor"));

            IEnumerable<string> ValuesOf(string flag) =>
                args.Select((a, i) => (a, i)).Where(t => t.a == flag).Select(t => args[t.i + 1]).ToList();
        }

        [Fact]
        public void The_macos_defaults_register_and_reach_the_standby_capturer()
        {
            // what a fresh Mac starts with: both region gestures and the screen one, each live
            var host = new FakeHost();
            using var manager = new HotkeyManager(host, new SettingsHotkey(OSPlatform.OSX));
            foreach (var id in Enum.GetValues<HotkeyId>())
                manager.SetAction(id, () => { });

            var cmdShift = HotkeyModifiers.Meta | HotkeyModifiers.Shift;
            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            Assert.Equal(GestureList.Of(new HotkeyGesture(HotkeyKey.D2, cmdShift), new HotkeyGesture(HotkeyKey.F13)), region.Gestures);
            Assert.All(region.Shortcuts, s => Assert.True(s.IsRegistered));
            Assert.Equal(3, host.Live.Count);

            // ...and in the crate's text form on the warm capturer's command line
            var root = new SettingsRoot { Hotkeys = new SettingsHotkey(OSPlatform.OSX) };
            root.General.UseSystemAccentColor = false;
            var args = CaptureArguments.BuildStandby("/sessions", root).ToList();
            Assert.Equal(new[] { "Shift+Meta+D2", "F13" }, ValuesOf("--hk-main"));
            Assert.Empty(ValuesOf("--hk-window"));
            Assert.Equal(new[] { "Shift+Meta+D1" }, ValuesOf("--hk-monitor"));

            IEnumerable<string> ValuesOf(string flag) =>
                args.Select((a, i) => (a, i)).Where(t => t.a == flag).Select(t => args[t.i + 1]).ToList();
        }

        [Theory]
        [InlineData("OSX", HotkeyKey.M, HotkeyModifiers.Meta)]
        [InlineData("OSX", HotkeyKey.Tab, HotkeyModifiers.Meta)]
        [InlineData("WINDOWS", HotkeyKey.Tab, HotkeyModifiers.Alt)]
        public void A_reserved_gesture_already_in_the_settings_is_never_registered(string os, HotkeyKey key, HotkeyModifiers modifiers)
        {
            // recorded before the recorder refused it, or edited in by hand: binding it would
            // swallow the OS's own combination in every app
            var reserved = new HotkeyGesture(key, modifiers);
            var host = new FakeHost();
            var settings = new SettingsHotkey(OSPlatform.Windows);
            settings.DrawOnScreenShortcut = GestureList.Of(reserved, CtrlShift1);
            using var manager = new HotkeyManager(host, settings, OSPlatform.Create(os));
            foreach (var id in Enum.GetValues<HotkeyId>())
                manager.SetAction(id, () => { });

            var draw = manager.GetEntry(HotkeyId.DrawOnScreen);
            Assert.DoesNotContain(host.Live, r => Equals(r.Gesture, reserved));
            Assert.False(draw.Shortcuts[0].IsRegistered);
            Assert.Contains("reserved", draw.Shortcuts[0].Error);
            Assert.True(draw.Shortcuts[1].IsRegistered);
            // kept, so the row shows it and its ✕ can remove it
            Assert.Equal(GestureList.Of(reserved, CtrlShift1), settings.DrawOnScreenShortcut);
        }

        [Fact]
        public void A_reserved_gesture_the_capturer_owns_says_so_and_never_reaches_it()
        {
            var cmdM = new HotkeyGesture(HotkeyKey.M, HotkeyModifiers.Meta);
            var settings = new SettingsHotkey(OSPlatform.OSX);
            settings.CaptureRegionShortcut = GestureList.Of(cmdM);
            using var manager = new HotkeyManager(new FakeHost(), settings, OSPlatform.OSX);
            manager.SetAction(HotkeyId.CaptureRegion, null); // warm capture: the capturer's

            var shortcut = Assert.Single(manager.GetEntry(HotkeyId.CaptureRegion).Shortcuts);
            Assert.Contains("reserved", shortcut.Error);
            // a report for it cannot make it look active
            manager.ApplyExternalStatus(HotkeyId.CaptureRegion, new[] { new ExternalGestureStatus("Meta+M", true, "") });
            Assert.False(shortcut.IsRegistered);
        }

        [Fact]
        public void The_standby_command_line_leaves_out_reserved_gestures()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return; // nothing is reserved elsewhere, and there is no standby capturer either

            // the capturer binds what it is sent, on the OS it runs on
            var reserved = OperatingSystem.IsMacOS()
                ? new HotkeyGesture(HotkeyKey.M, HotkeyModifiers.Meta)
                : new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt);
            var root = new SettingsRoot { Hotkeys = new SettingsHotkey(OSPlatform.Windows) };
            root.General.UseSystemAccentColor = false;
            root.Hotkeys.CaptureRegionShortcut = GestureList.Of(reserved, PrintScreen);

            var args = CaptureArguments.BuildStandby("/sessions", root).ToList();

            var main = args.Select((a, i) => (a, i)).Where(t => t.a == "--hk-main").Select(t => args[t.i + 1]);
            Assert.Equal(new[] { "PrintScreen" }, main);
        }

        [Fact]
        public void The_hook_coming_back_reaches_the_views_until_the_manager_is_disposed()
        {
            var (manager, host, _) = Create();
            int raised = 0;
            manager.RecordingAvailable += (_, _) => raised++;

            host.RaiseRecordingAvailable();
            Assert.Equal(1, raised);

            manager.Dispose();
            host.RaiseRecordingAvailable();
            Assert.Equal(1, raised);
        }

        [Fact]
        public void Recording_raises_RecordingChanged_on_start_and_on_end()
        {
            var (manager, host, _) = Create();
            var states = new List<bool>();
            manager.RecordingChanged += (_, _) => states.Add(manager.IsRecording);
            HotkeyGesture recorded = null;

            var session = manager.StartRecording(_ => { }, g => recorded = g, _ => { }, out var error);

            Assert.NotNull(session);
            Assert.Null(error);
            Assert.True(manager.IsRecording);
            Assert.Equal(new[] { true }, states);

            // one recording at a time: a second is refused without an error
            Assert.Null(manager.StartRecording(_ => { }, _ => { }, _ => { }, out error));
            Assert.Null(error);

            host.Recording.Recorded(CtrlShift1);

            Assert.Equal(CtrlShift1, recorded);
            Assert.False(manager.IsRecording);
            Assert.Equal(new[] { true, false }, states);

            // disposing after the end changes nothing, and a new recording can start
            session.Dispose();
            Assert.Equal(new[] { true, false }, states);
            Assert.NotNull(manager.StartRecording(_ => { }, _ => { }, _ => { }, out _));
        }

        [Fact]
        public void Disposing_a_recording_cancels_it_in_the_host_and_ends_it()
        {
            var (manager, host, _) = Create();
            var states = new List<bool>();
            manager.RecordingChanged += (_, _) => states.Add(manager.IsRecording);

            var session = manager.StartRecording(_ => { }, _ => { }, _ => { }, out _);
            var inner = host.Recording;
            session.Dispose();

            Assert.True(inner.Canceled);
            Assert.False(manager.IsRecording);
            Assert.Equal(new[] { true, false }, states);
        }

        [Fact]
        public void A_recorded_callback_that_throws_still_ends_the_recording()
        {
            var (manager, host, _) = Create();
            manager.StartRecording(_ => { }, _ => throw new InvalidOperationException("boom"), _ => { }, out _);

            Assert.Throws<InvalidOperationException>(() => host.Recording.Recorded(CtrlShift1));

            Assert.False(manager.IsRecording);
        }

        [Fact]
        public void A_host_that_cannot_record_passes_its_error_through()
        {
            var (manager, host, _) = Create();
            host.RecordingError = "Grant Accessibility permission.";
            var raised = false;
            manager.RecordingChanged += (_, _) => raised = true;

            var session = manager.StartRecording(_ => { }, _ => { }, _ => { }, out var error);

            Assert.Null(session);
            Assert.Equal("Grant Accessibility permission.", error);
            Assert.False(manager.IsRecording);
            Assert.False(raised);
        }

        [Fact]
        public void A_recording_the_host_ends_with_an_error_ends_and_hands_the_error_over()
        {
            // the hook dying mid-recording: no gesture will ever come, and the standby capturer's
            // hotkeys (paused through RecordingChanged) must not stay paused waiting for one
            var (manager, host, _) = Create();
            var states = new List<bool>();
            manager.RecordingChanged += (_, _) => states.Add(manager.IsRecording);
            HotkeyGesture recorded = null;
            string failure = null;
            var session = manager.StartRecording(_ => { }, g => recorded = g, e => failure = e, out _);

            host.Recording.Fail("The global hotkey listener stopped.");

            Assert.Equal("The global hotkey listener stopped.", failure);
            Assert.Null(recorded);
            Assert.False(manager.IsRecording);
            Assert.Equal(new[] { true, false }, states);

            session.Dispose();
            Assert.Equal(new[] { true, false }, states);
        }

        private sealed class FakeHost : IGlobalTriggerHost
        {
            public readonly List<FakeRegistration> Live = new();
            private readonly Dictionary<HotkeyGesture, string> _refused = new();

            /// <summary>When set, <see cref="StartRecording"/> refuses with this error.</summary>
            public string RecordingError;

            /// <summary>The recording last started.</summary>
            public FakeRecording Recording;

            /// <summary>Makes every later registration of <paramref name="gesture"/> fail up front,
            /// as the real host does for a key it cannot hook.</summary>
            public void Refuse(HotkeyGesture gesture, string error) => _refused[gesture] = error;

            public IGlobalTriggerRegistration RegisterTrigger(HotkeyGesture gesture, Action executed, bool fireOnRepeat = false)
            {
                if (_refused.TryGetValue(gesture, out var error))
                    return new FakeRegistration(null, gesture, false, error);

                // a real backend refuses a gesture that is still held by another registration.
                Assert.DoesNotContain(Live, r => Equals(r.Gesture, gesture));
                var registration = new FakeRegistration(this, gesture, true, "");
                Live.Add(registration);
                return registration;
            }

            public IDisposable ObserveKeys(Action<GlobalKeyEvent> observer) => throw new NotSupportedException();

            public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
                Action<string> failed, out string error)
            {
                error = RecordingError;
                if (error != null)
                    return null;

                return Recording = new FakeRecording(recorded, failed);
            }

            public event EventHandler RecordingAvailable;

            public void RaiseRecordingAvailable() => RecordingAvailable?.Invoke(this, EventArgs.Empty);
        }

        private sealed class FakeRecording(Action<HotkeyGesture> recorded, Action<string> failed) : IDisposable
        {
            public bool Canceled { get; private set; }

            public void Recorded(HotkeyGesture gesture) => recorded(gesture);

            public void Fail(string error) => failed(error);

            public void Dispose() => Canceled = true;
        }

        private sealed class FakeRegistration(FakeHost host, HotkeyGesture gesture, bool isRegistered, string error)
            : IGlobalTriggerRegistration
        {
            public HotkeyGesture Gesture { get; } = gesture;
            public bool IsRegistered { get; private set; } = isRegistered;
            public string Error { get; private set; } = error;
            public event EventHandler StatusChanged;

            public void Fail(string message)
            {
                IsRegistered = false;
                Error = message;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            public void Dispose() => host?.Live.Remove(this);
        }
    }
}
