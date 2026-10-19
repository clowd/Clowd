using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Clowd;
using Clowd.Config;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Assigning a gesture another hotkey already holds moves it: the previous owner is left unset
    /// and its OS registration is released before the new one is made.
    /// </summary>
    public class HotkeyManagerTests
    {
        [Fact]
        public void Taking_a_gesture_already_in_use_unsets_the_other_hotkey()
        {
            var host = new FakeHost();
            var settings = new SettingsHotkey();
            var manager = new HotkeyManager(host, settings);
            foreach (var id in Enum.GetValues<HotkeyId>())
                manager.SetAction(id, () => { });

            var region = manager.GetEntry(HotkeyId.CaptureRegion);
            var draw = manager.GetEntry(HotkeyId.DrawOnScreen);
            var regionChanged = false;
            region.PropertyChanged += (_, e) => regionChanged |= e.PropertyName == nameof(HotkeyEntry.Gesture);

            var printScreen = new SimpleKeyGesture(Key.Snapshot);
            Assert.Equal(printScreen, region.Gesture);

            draw.Gesture = printScreen;

            Assert.Equal(printScreen, draw.Gesture);
            Assert.Null(region.Gesture);
            Assert.Null(settings.CaptureRegionShortcut);
            Assert.True(regionChanged);
            Assert.Single(host.Live, r => Equals(r.Gesture, printScreen));
        }

        [Fact]
        public void Distinct_gestures_leave_other_hotkeys_alone()
        {
            var settings = new SettingsHotkey();
            var manager = new HotkeyManager(new FakeHost(), settings);

            manager.GetEntry(HotkeyId.DrawOnScreen).Gesture = new SimpleKeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift);
            manager.GetEntry(HotkeyId.DrawOnScreen).Gesture = null;

            Assert.Equal(new SimpleKeyGesture(Key.Snapshot), settings.CaptureRegionShortcut);
            Assert.Equal(new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Control), settings.CaptureFullscreenShortcut);
        }

        private sealed class FakeHost : IGlobalTriggerHost
        {
            public readonly List<FakeRegistration> Live = new();

            public bool IsPaused { get; set; }

            public IGlobalTriggerRegistration RegisterTrigger(SimpleKeyGesture gesture, Action executed)
            {
                // a real backend refuses a gesture that is still held by another registration.
                Assert.DoesNotContain(Live, r => Equals(r.Gesture, gesture));
                var registration = new FakeRegistration(this, gesture);
                Live.Add(registration);
                return registration;
            }

            public IDisposable ObserveKeys(Action<GlobalKeyEvent> observer) => throw new NotSupportedException();
        }

        private sealed class FakeRegistration(FakeHost host, SimpleKeyGesture gesture) : IGlobalTriggerRegistration
        {
            public SimpleKeyGesture Gesture { get; } = gesture;
            public bool IsRegistered => true;
            public string Error => "";
            public event EventHandler StatusChanged { add { } remove { } }
            public void Dispose() => host.Live.Remove(this);
        }
    }
}
