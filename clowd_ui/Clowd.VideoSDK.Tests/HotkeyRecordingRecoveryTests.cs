using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI;
using Clowd.UI.Config;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// A click on the Hotkeys page that cannot record (macOS before Accessibility is granted) says
    /// why — in the field's status, or on the add button when the new field it made has gone. The
    /// hook comes back by itself once the permission is granted, often with the settings window
    /// still open, and from then on the hotkeys work: neither may go on claiming otherwise.
    /// </summary>
    [Collection(HeadlessApp.Collection)]
    public class HotkeyRecordingRecoveryTests
    {
        private const string Denied = NativeHotkeyHost.PermissionMessage;

        [AvaloniaFact]
        public void A_field_drops_its_recording_error_once_the_hook_is_back()
        {
            using var rig = new Rig();
            var field = rig.Editor.GetLogicalDescendants().OfType<GestureField>().First();

            Assert.False(field.BeginListening());
            Assert.Equal("Error", StatusText(field));
            Assert.Equal(Denied, StatusTip(field));

            rig.Host.Recover();

            Assert.Null(field.RecordingError);
            Assert.Equal("Active", StatusText(field));
            Assert.Contains("active", StatusDot(field).Classes);
            Assert.DoesNotContain("error", StatusDot(field).Classes);
            Assert.Null(StatusTip(field));
        }

        [AvaloniaFact]
        public void The_add_button_drops_its_recording_error_once_the_hook_is_back()
        {
            using var rig = new Rig();
            var add = rig.Editor.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "+ Add shortcut"));

            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Denied, ToolTip.GetTip(add));

            rig.Host.Recover();

            Assert.Null(ToolTip.GetTip(add));
            Assert.False(ToolTip.GetIsOpen(add));
        }

        [AvaloniaFact]
        public void A_status_change_that_is_not_the_hook_coming_back_keeps_the_error()
        {
            // a capturer report, a rebind: the hook is still down, and a click would still fail
            using var rig = new Rig();
            var field = rig.Editor.GetLogicalDescendants().OfType<GestureField>().First();
            Assert.False(field.BeginListening());

            rig.Host.Live[0].SetStatus(false, "something else");

            Assert.Equal(Denied, field.RecordingError);
            Assert.Equal(Denied, StatusTip(field));
        }

        [AvaloniaFact]
        public void A_recording_under_secure_input_says_why_it_hears_nothing()
        {
            // another app's password field hides every key from the hook (macOS Secure Input)
            var (button, tip, word) = Listen(secureInput: true);
            Assert.Equal(GestureField.SecureInputPrompt, button);
            Assert.Equal(GestureField.SecureInputMessage, tip);
            Assert.Equal("Listening", word);

            (button, tip, word) = Listen(secureInput: false);
            Assert.Equal("Press a key combination…", button);
            Assert.Null(tip);
            Assert.Equal("Listening", word);

            static (object Button, object Tip, string Word) Listen(bool secureInput)
            {
                using var rig = new Rig();
                rig.Host.Recover();
                rig.Manager.SecureInputProbe = () => secureInput;
                var field = rig.Editor.GetLogicalDescendants().OfType<GestureField>().First();
                Assert.True(field.BeginListening());
                var button = field.GetLogicalDescendants().OfType<Button>().First();
                return (button.Content, StatusTip(field), StatusText(field));
            }
        }

        private static Ellipse StatusDot(GestureField field) =>
            field.GetLogicalDescendants().OfType<Ellipse>().Single();

        // the dot's panel carries the tooltip
        private static object StatusTip(GestureField field) => ToolTip.GetTip((Control)StatusDot(field).Parent);

        // the word beside the dot: the last of the panel it shares with the invisible copies
        // sizing its column
        private static string StatusText(GestureField field) =>
            ((TextBlock)((Panel)((Panel)StatusDot(field).Parent).Children[1]).Children[^1]).Text;

        /// <summary>The Capture Region row (the macOS defaults: two gestures) in an active window,
        /// over a host whose hook is down for want of Accessibility.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly DownHost Host = new();
            public readonly HotkeyManager Manager;
            public readonly HotkeyShortcutsEditor Editor;
            private readonly Window _window;

            public Rig()
            {
                Manager = new HotkeyManager(Host, new SettingsHotkey(OSPlatform.OSX));
                foreach (var id in Enum.GetValues<HotkeyId>())
                    Manager.SetAction(id, () => { });
                HotkeyManager.Current = Manager;

                Editor = new HotkeyShortcutsEditor(Manager.GetEntry(HotkeyId.CaptureRegion), "Capture Region");
                _window = new Window { Content = Editor };
                _window.Show();
                _window.Activate();
                Dispatcher.UIThread.RunJobs();
                Assert.True(_window.IsActive);
            }

            public void Dispose()
            {
                _window.Close();
                Manager.Dispose();
            }
        }

        /// <summary>A hook that is down: registrations wait, recording is refused with the
        /// permission message, until <see cref="Recover"/> brings it back the way
        /// <see cref="NativeHotkeyHost.RetryStart"/> does.</summary>
        private sealed class DownHost : IGlobalTriggerHost
        {
            public readonly List<Registration> Live = new();
            private string _error = Denied;

            public event EventHandler RecordingAvailable;

            public void Recover()
            {
                _error = null;
                foreach (var registration in Live.ToList())
                    registration.SetStatus(true, "");
                RecordingAvailable?.Invoke(this, EventArgs.Empty);
            }

            public IGlobalTriggerRegistration RegisterTrigger(HotkeyGesture gesture, Action executed, bool fireOnRepeat = false)
            {
                var registration = new Registration(this, _error == null, _error ?? "");
                Live.Add(registration);
                return registration;
            }

            public IDisposable ObserveKeys(Action<GlobalKeyEvent> observer) => throw new NotSupportedException();

            public IDisposable StartRecording(Action<HotkeyModifiers> modifiersChanged, Action<HotkeyGesture> recorded,
                Action<string> failed, out string error)
            {
                error = _error;
                return error == null ? new Recording() : null;
            }

            private sealed class Recording : IDisposable
            {
                public void Dispose() { }
            }

            public sealed class Registration(DownHost host, bool isRegistered, string error) : IGlobalTriggerRegistration
            {
                public bool IsRegistered { get; private set; } = isRegistered;
                public string Error { get; private set; } = error;
                public event EventHandler StatusChanged;

                public void SetStatus(bool isRegistered, string error)
                {
                    IsRegistered = isRegistered;
                    Error = error;
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }

                public void Dispose() => host.Live.Remove(this);
            }
        }
    }
}
