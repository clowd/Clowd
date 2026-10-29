using System;
using System.Runtime.InteropServices;
using Prisc.Interop;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>
    /// <see cref="HotkeyGesture"/> parses and prints through the prisc_hotkeys crate's
    /// <c>Gesture</c> (<c>prisc_gesture_*</c>), since the settings file, the standby capturer's
    /// command line and its status echo all carry that one text. The grammar's vectors live beside
    /// it in Rust (gesture.rs, reserved.rs); these are the marshalling smoke tests of each operation,
    /// the full-table round trips that pin the generated C# enums to the Rust table, and the per-OS
    /// reserved cases through the overload callers use.
    /// </summary>
    public class HotkeyGestureTests
    {
        [Theory]
        [InlineData("PrintScreen", "PrintScreen")]
        [InlineData("control+snapshot", "Control+PrintScreen")]
        [InlineData("Shift+Control+A", "Control+Shift+A")]
        [InlineData("Ctrl+PrtScr", "Control+PrintScreen")]
        [InlineData("Cmd+Shift+3", "Shift+Meta+D3")]
        [InlineData("Ctrl+Num+", "Control+Add")]
        [InlineData("Option+Del", "Alt+Delete")]
        public void Parses_like_the_crate_and_prints_the_canonical_form(string input, string serialized)
        {
            InteropTestGate.Require();
            var gesture = HotkeyGesture.Parse(input);

            Assert.NotNull(gesture);
            Assert.Equal(serialized, gesture.ToSerializedString());
            Assert.Equal(gesture, HotkeyGesture.Parse(serialized));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("+")]
        [InlineData("NotAKey")]
        [InlineData("Hyper+A")]
        [InlineData("Control+Shift")] // Shift is a modifier, not a key name
        public void Refuses_what_the_crate_refuses(string input)
        {
            InteropTestGate.Require();
            Assert.Null(HotkeyGesture.Parse(input));
        }

        [Fact]
        public void The_crate_says_why_text_is_no_gesture()
        {
            InteropTestGate.Require();
            Assert.False(NativeGestures.TryParse("Hyper+A", out _, out _, out var error));
            Assert.Equal("unknown modifier 'Hyper'", error);
            Assert.False(NativeGestures.TryParse("Control+Shift", out _, out _, out error));
            Assert.Equal("unknown key 'Shift'", error);
        }

        [Fact]
        public void None_is_a_gesture_with_no_key()
        {
            InteropTestGate.Require();
            Assert.Equal(new HotkeyGesture(HotkeyKey.None), HotkeyGesture.Parse("None"));
            Assert.Equal("None", new HotkeyGesture(HotkeyKey.None).ToSerializedString());
            Assert.Equal("", new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.Control).ToString());
        }

        [Theory]
        [InlineData("Enter", HotkeyKey.Return)]
        [InlineData("Snapshot", HotkeyKey.PrintScreen)]
        [InlineData("OemTilde", HotkeyKey.Oem3)]
        [InlineData("Pa1", HotkeyKey.DbeEnterDialogConversionMode)]
        public void An_alias_parses_to_its_canonical_key(string alias, HotkeyKey key)
        {
            InteropTestGate.Require();
            Assert.Equal(new HotkeyGesture(key), HotkeyGesture.Parse(alias));
        }

        /// <summary>The generated <see cref="HotkeyKey"/> and the Rust key table agree on every
        /// name and value, both ways through the crate.</summary>
        [Fact]
        public void Every_key_name_round_trips()
        {
            InteropTestGate.Require();
            foreach (var key in Enum.GetValues<HotkeyKey>())
            {
                var name = HotkeyKeys.Name(key);
                Assert.Equal(key.ToString(), name);
                Assert.Equal(new HotkeyGesture(key), HotkeyGesture.Parse(name));
                Assert.Equal(new HotkeyGesture(key), HotkeyGesture.Parse(name.ToLowerInvariant()));
                Assert.Equal(name, new HotkeyGesture(key).ToSerializedString());
            }
        }

        [Theory]
        [InlineData(HotkeyKey.PrintScreen, HotkeyModifiers.None, "PrtScr")]
        [InlineData(HotkeyKey.PrintScreen, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+PrtScr")]
        [InlineData(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+1")]
        [InlineData(HotkeyKey.MediaPlayPause, HotkeyModifiers.None, "Play/Pause")]
        public void Display_text(HotkeyKey key, HotkeyModifiers modifiers, string expected)
        {
            InteropTestGate.Require();
            Assert.Equal(expected, new HotkeyGesture(key, modifiers).ToString());
        }

        [Fact]
        public void Display_text_parses_back_for_every_key()
        {
            InteropTestGate.Require();
            const HotkeyModifiers all = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta;
            foreach (var key in Enum.GetValues<HotkeyKey>())
            {
                if (key == HotkeyKey.None)
                    continue;

                var gesture = new HotkeyGesture(key, all);
                Assert.Equal(gesture, HotkeyGesture.Parse(gesture.ToString()));
            }
        }

        [Fact]
        public void Meta_reads_as_the_key_it_is_on_this_os()
        {
            InteropTestGate.Require();
            var meta = OperatingSystem.IsWindows() ? "Win" : OperatingSystem.IsMacOS() ? "Cmd" : "Super";

            Assert.Equal($"Ctrl+Alt+Shift+{meta}+X",
                new HotkeyGesture(HotkeyKey.X, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta).ToString());
            Assert.Equal($"Ctrl+{meta}+", HotkeyGesture.ModifiersText(HotkeyModifiers.Meta | HotkeyModifiers.Control));
            Assert.Equal("", HotkeyGesture.ModifiersText(HotkeyModifiers.None));
        }

        [Fact]
        public void Task_switching_and_task_manager_are_reserved_on_windows()
        {
            InteropTestGate.Require();
            var reserved = new[]
            {
                new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt),
                new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt | HotkeyModifiers.Shift),
                new HotkeyGesture(HotkeyKey.Escape, HotkeyModifiers.Alt),
                new HotkeyGesture(HotkeyKey.Escape, HotkeyModifiers.Control),
                new HotkeyGesture(HotkeyKey.Escape, HotkeyModifiers.Control | HotkeyModifiers.Shift),
            };

            foreach (var gesture in reserved)
            {
                Assert.True(HotkeyGesture.IsSystemReserved(gesture, OSPlatform.Windows), gesture.ToSerializedString());
                // Alt+Tab is an ordinary combination on a Mac
                Assert.False(HotkeyGesture.IsSystemReserved(gesture, OSPlatform.OSX), gesture.ToSerializedString());
                Assert.False(HotkeyGesture.IsSystemReserved(gesture, OSPlatform.Linux), gesture.ToSerializedString());
            }

            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Tab), OSPlatform.Windows));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Control), OSPlatform.Windows));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Escape), OSPlatform.Windows));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Q, HotkeyModifiers.Meta), OSPlatform.Windows));
            Assert.False(HotkeyGesture.IsSystemReserved(null, OSPlatform.Windows));
        }

        [Fact]
        public void Switching_spotlight_leaving_the_window_and_the_ways_out_of_a_hang_are_reserved_on_macos()
        {
            InteropTestGate.Require();
            const HotkeyModifiers Cmd = HotkeyModifiers.Meta;
            var reserved = new[]
            {
                new HotkeyGesture(HotkeyKey.Tab, Cmd),
                new HotkeyGesture(HotkeyKey.Tab, Cmd | HotkeyModifiers.Shift),
                new HotkeyGesture(HotkeyKey.Oem3, Cmd),
                new HotkeyGesture(HotkeyKey.Oem3, Cmd | HotkeyModifiers.Shift),
                new HotkeyGesture(HotkeyKey.OemBackslash, Cmd),
                new HotkeyGesture(HotkeyKey.Space, Cmd),
                new HotkeyGesture(HotkeyKey.Q, Cmd),
                new HotkeyGesture(HotkeyKey.W, Cmd),
                new HotkeyGesture(HotkeyKey.H, Cmd),
                new HotkeyGesture(HotkeyKey.M, Cmd),
                new HotkeyGesture(HotkeyKey.Escape, Cmd | HotkeyModifiers.Alt),
                new HotkeyGesture(HotkeyKey.Q, Cmd | HotkeyModifiers.Control),
            };

            foreach (var gesture in reserved)
            {
                Assert.True(HotkeyGesture.IsSystemReserved(gesture, OSPlatform.OSX), gesture.ToSerializedString());
                Assert.False(HotkeyGesture.IsSystemReserved(gesture, OSPlatform.Windows), gesture.ToSerializedString());
            }

            // the screenshot keys are a screenshot tool's to take over
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.D3, Cmd | HotkeyModifiers.Shift), OSPlatform.OSX));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.D4, Cmd | HotkeyModifiers.Shift), OSPlatform.OSX));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.D5, Cmd | HotkeyModifiers.Shift), OSPlatform.OSX));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Q, Cmd | HotkeyModifiers.Shift), OSPlatform.OSX));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt), OSPlatform.OSX));
            Assert.False(HotkeyGesture.IsSystemReserved(null, OSPlatform.OSX));
        }

        [Fact]
        public void The_current_os_list_is_the_one_that_applies()
        {
            InteropTestGate.Require();
            var cmdQ = new HotkeyGesture(HotkeyKey.Q, HotkeyModifiers.Meta);
            var altTab = new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt);

            Assert.Equal(OperatingSystem.IsMacOS(), HotkeyGesture.IsSystemReserved(cmdQ));
            Assert.Equal(OperatingSystem.IsWindows(), HotkeyGesture.IsSystemReserved(altTab));
        }

        [Fact]
        public void Equality_is_on_key_and_modifiers()
        {
            var a = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Control);

            Assert.Equal(a, new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Control));
            Assert.Equal(a.GetHashCode(), new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Control).GetHashCode());
            Assert.NotEqual(a, new HotkeyGesture(HotkeyKey.A));
            Assert.NotEqual(a, new HotkeyGesture(HotkeyKey.B, HotkeyModifiers.Control));
        }
    }
}
