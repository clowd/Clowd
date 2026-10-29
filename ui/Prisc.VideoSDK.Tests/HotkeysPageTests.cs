using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Prisc;
using Prisc.Config;
using Prisc.Interop.Testing;
using Prisc.UI;
using Prisc.UI.Config;
using Prisc.UI.Helpers;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The platform-free rules behind the Hotkeys settings page: which actions it shows, the list
    /// arithmetic of recording a shortcut (re-recording one in place, adding one at the end,
    /// canceling either), and the child-list sync that keeps a listening field in the panel while
    /// its own edit changes the list around it. No Avalonia runtime.
    /// </summary>
    public class HotkeysPageTests
    {
        private static readonly HotkeyGesture PrintScreen = new(HotkeyKey.PrintScreen);
        private static readonly HotkeyGesture CtrlShift1 = new(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlShift2 = new(HotkeyKey.D2, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        private static readonly HotkeyGesture CtrlShift3 = new(HotkeyKey.D3, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        // ---- layout ----

        [Fact]
        public void Every_action_is_on_the_page_exactly_once()
        {
            var shown = HotkeyPageLayout.Sections.SelectMany(s => s.Actions).ToList();

            Assert.Equal(shown.Count, shown.Distinct().Count());
            Assert.Equal(Enum.GetValues<HotkeyId>().OrderBy(i => i), shown.OrderBy(i => i));
        }

        [Fact]
        public void Sections_come_in_the_approved_order()
        {
            Assert.Equal(new[] { "Capture", "Record & share", "Draw", "Open", "Upload" },
                HotkeyPageLayout.Sections.Select(s => s.Title));
            Assert.Equal(new[] { HotkeyId.ColorWheel, HotkeyId.NewImageEditor, HotkeyId.NewVideoEditor },
                HotkeyPageLayout.Sections.Single(s => s.Title == "Open").Actions);
        }

        [Fact]
        public void Only_macOS_explains_the_top_row_function_keys()
        {
            // a MacBook's F-keys send brightness and media unless Fn is held or the setting is on
            var note = HotkeyPageLayout.PlatformNote(OSPlatform.OSX);
            Assert.NotNull(note);
            Assert.Contains("Fn", note);
            Assert.Contains("Use F1, F2, etc. keys as standard function keys", note);
            // since Ventura the switch is not on the Keyboard pane itself
            Assert.Contains("System Settings → Keyboard → Keyboard Shortcuts… → Function Keys", note);
            Assert.Null(HotkeyPageLayout.PlatformNote(OSPlatform.Windows));
            Assert.Null(HotkeyPageLayout.PlatformNote(OSPlatform.Linux));
        }

        [Fact]
        public void Every_row_has_a_name_from_its_settings_property()
        {
            using var manager = new HotkeyManager(new FakeHost(), new SettingsHotkey());
            var properties = TypeDescriptor.GetProperties(typeof(SettingsHotkey));

            foreach (var row in HotkeyPageLayout.Sections.SelectMany(s => s.Rows))
            {
                var pd = properties[manager.GetEntry(row.Id).SettingsProperty];
                Assert.NotNull(pd);
                Assert.NotEqual(pd.Name, pd.DisplayName); // a [DisplayName], not the raw property name
                Assert.False(String.IsNullOrWhiteSpace(row.Label));
            }
        }

        // ---- GestureListEdit ----

        [Fact]
        public void Re_recording_takes_the_gesture_out_while_listening_and_puts_the_new_one_in_its_place()
        {
            var list = GestureList.Of(PrintScreen, CtrlShift1, CtrlShift2);
            var edit = GestureListEdit.Replace(list, 1);

            Assert.Equal(GestureList.Of(PrintScreen, CtrlShift2), edit.Listening);
            Assert.Equal(1, edit.SlotIndex(edit.Listening.Count));

            var result = edit.Commit(CtrlShift3);
            Assert.Equal(GestureList.Of(PrintScreen, CtrlShift3, CtrlShift2), result);
            Assert.Equal(1, edit.ResultIndex(result));
        }

        [Fact]
        public void Canceling_a_re_recording_restores_the_list_as_it_was()
        {
            var list = GestureList.Of(PrintScreen, CtrlShift1);
            var edit = GestureListEdit.Replace(list, 0);

            var result = edit.Commit(null);
            Assert.Equal(list, result);
            Assert.Same(list, edit.Saved);
            Assert.Equal(0, edit.ResultIndex(result));
        }

        [Fact]
        public void Re_recording_a_gesture_the_action_already_has_elsewhere_changes_nothing()
        {
            var list = GestureList.Of(PrintScreen, CtrlShift1);
            var edit = GestureListEdit.Replace(list, 0);

            var result = edit.Commit(CtrlShift1);
            Assert.Equal(list, result);
            // the field goes back to showing the gesture it was re-recording
            Assert.Equal(0, edit.ResultIndex(result));
        }

        [Fact]
        public void Re_recording_the_same_gesture_keeps_it()
        {
            var list = GestureList.Of(PrintScreen, CtrlShift1);
            var edit = GestureListEdit.Replace(list, 1);

            Assert.Equal(list, edit.Commit(CtrlShift1));
        }

        [Fact]
        public void Adding_appends_and_leaves_the_list_alone_while_listening()
        {
            var list = GestureList.Of(PrintScreen);
            var edit = GestureListEdit.Append(list);

            Assert.Same(list, edit.Listening);
            Assert.Equal(1, edit.SlotIndex(edit.Listening.Count));

            var result = edit.Commit(CtrlShift1);
            Assert.Equal(GestureList.Of(PrintScreen, CtrlShift1), result);
            Assert.Equal(1, edit.ResultIndex(result));
        }

        [Fact]
        public void Adding_to_an_empty_action_works()
        {
            var edit = GestureListEdit.Append(GestureList.Empty);

            Assert.Equal(0, edit.SlotIndex(0));
            var result = edit.Commit(CtrlShift2);
            Assert.Equal(GestureList.Of(CtrlShift2), result);
            Assert.Equal(0, edit.ResultIndex(result));
        }

        [Fact]
        public void A_canceled_or_duplicate_add_leaves_nothing_for_the_pending_field_to_show()
        {
            var list = GestureList.Of(PrintScreen, CtrlShift1);
            var edit = GestureListEdit.Append(list);

            var canceled = edit.Commit(null);
            Assert.Equal(list, canceled);
            Assert.Equal(-1, edit.ResultIndex(canceled));

            var duplicate = edit.Commit(PrintScreen);
            Assert.Equal(list, duplicate);
            Assert.Equal(-1, edit.ResultIndex(duplicate));
        }

        [Fact]
        public void Replace_rejects_an_index_outside_the_list()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GestureListEdit.Replace(GestureList.Of(PrintScreen), 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => GestureListEdit.Replace(GestureList.Empty, 0));
        }

        [Fact]
        public void The_slot_stays_inside_a_list_that_shrank_meanwhile()
        {
            var edit = GestureListEdit.Replace(GestureList.Of(PrintScreen, CtrlShift1, CtrlShift2), 2);

            // another action took one of the other gestures while this one was listening
            Assert.Equal(1, edit.SlotIndex(1));
        }

        [Fact]
        public void Committing_through_the_manager_takes_the_gesture_from_its_other_owner()
        {
            InteropTestGate.Require(); // the manager formats and checks gestures through prisc_interop
            // the Windows defaults, where Capture Region's only gesture is PrtScr
            using var manager = new HotkeyManager(new FakeHost(), new SettingsHotkey(OSPlatform.Windows));
            foreach (var id in Enum.GetValues<HotkeyId>())
                manager.SetAction(id, () => { });

            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            var wheel = manager.GetEntry(HotkeyId.ColorWheel);

            var edit = GestureListEdit.Append(wheel.Gestures);
            wheel.Gestures = edit.Listening;
            wheel.Gestures = edit.Commit(PrintScreen);

            Assert.Equal(GestureList.Of(PrintScreen), wheel.Gestures);
            Assert.Empty(region.Shortcuts);
        }

        [Fact]
        public void A_re_recording_through_the_manager_keeps_the_others_shortcut_objects()
        {
            InteropTestGate.Require(); // the manager formats and checks gestures through prisc_interop
            using var manager = new HotkeyManager(new FakeHost(), new SettingsHotkey());
            foreach (var id in Enum.GetValues<HotkeyId>())
                manager.SetAction(id, () => { });

            var draw = manager.GetEntry(HotkeyId.DrawOnScreen);
            draw.Gestures = GestureList.Of(CtrlShift1, CtrlShift2);
            var kept = draw.Shortcuts[1];

            var edit = GestureListEdit.Replace(draw.Gestures, 0);
            draw.Gestures = edit.Listening;
            Assert.Same(kept, draw.Shortcuts[0]);

            var result = edit.Commit(CtrlShift3);
            draw.Gestures = result;

            Assert.Equal(GestureList.Of(CtrlShift3, CtrlShift2), draw.Gestures);
            Assert.Same(kept, draw.Shortcuts[1]);
            Assert.Equal(CtrlShift3, draw.Shortcuts[edit.ResultIndex(result)].Gesture);
        }

        // ---- ChildListSync ----

        private sealed class Item
        {
            public Item(string name) => Name = name;
            public string Name { get; }
            public override string ToString() => Name;
        }

        /// <summary>Records every removal, so a test can tell an item that stayed from one that
        /// was taken out and put back.</summary>
        private sealed class RecordingList : Collection<Item>
        {
            public List<Item> Removed { get; } = new();

            public RecordingList(IEnumerable<Item> items)
            {
                foreach (var item in items)
                    Add(item);
            }

            protected override void RemoveItem(int index)
            {
                Removed.Add(this[index]);
                base.RemoveItem(index);
            }
        }

        private static readonly Item A = new("A"), B = new("B"), C = new("C"), D = new("D"), P = new("P");

        [Fact]
        public void Sync_reaches_the_desired_list()
        {
            var cases = new[]
            {
                (From: new Item[0], To: new[] { A, B }),
                (From: new[] { A, B, C }, To: new Item[0]),
                (From: new[] { A, B, C }, To: new[] { A, C }),
                (From: new[] { A, C }, To: new[] { A, B, C }),
                (From: new[] { C, B, A }, To: new[] { A, B, C }),
                (From: new[] { A, B }, To: new[] { D, B, A }),
            };

            foreach (var (from, to) in cases)
            {
                var list = new RecordingList(from);
                ChildListSync.Sync(list, to);
                Assert.Equal(to, list);
            }
        }

        [Fact]
        public void Sync_leaves_items_that_stay_in_place_alone()
        {
            var list = new RecordingList(new[] { A, B, C });

            ChildListSync.Sync(list, new[] { A, B, D, C });

            Assert.Equal(new[] { A, B, D, C }, list);
            Assert.Empty(list.Removed);
        }

        [Fact]
        public void Sync_never_takes_the_pinned_item_out()
        {
            // the listening field's own edit removed the gesture before it (A) and one after it
            // took a place in front of it
            var list = new RecordingList(new[] { A, B, P, C });

            ChildListSync.Sync(list, new[] { C, P, B }, P);

            Assert.Equal(new[] { C, P, B }, list);
            Assert.DoesNotContain(P, list.Removed);
        }

        [Fact]
        public void Sync_moves_whatever_stands_before_the_pinned_item()
        {
            var list = new RecordingList(new[] { B, C, P });

            ChildListSync.Sync(list, new[] { P, C, B }, P);

            Assert.Equal(new[] { P, C, B }, list);
            Assert.DoesNotContain(P, list.Removed);
        }

        [Fact]
        public void Sync_inserts_a_pinned_item_that_is_not_there_yet()
        {
            var list = new RecordingList(new[] { A, B });

            ChildListSync.Sync(list, new[] { A, P, B }, P);

            Assert.Equal(new[] { A, P, B }, list);
            Assert.Empty(list.Removed);
        }

        /// <summary>Registers nothing for real: every registration succeeds.</summary>
        private sealed class FakeHost : IGlobalTriggerHost
        {
            public IGlobalTriggerRegistration RegisterTrigger(HotkeyGesture gesture, Action executed, bool fireOnRepeat = false) => new Registration();

            public IDisposable ObserveKeys(Action<GlobalKeyEvent> observer) => throw new NotSupportedException();

            public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded, Action<string> failed, out string error) =>
                throw new NotSupportedException();

            // never down, so never back either
            public event EventHandler RecordingAvailable { add { } remove { } }

            private sealed class Registration : IGlobalTriggerRegistration
            {
                public bool IsRegistered => true;
                public string Error => "";

                // a fake registration's status never changes, so nobody needs to be told
                public event EventHandler StatusChanged { add { } remove { } }

                public void Dispose() { }
            }
        }
    }
}
