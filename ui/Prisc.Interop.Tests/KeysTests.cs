using System;
using System.Linq;
using System.Threading.Tasks;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Interop.Tests
{
    /// <summary>The gestures and keyboard family: the struct layout and enum values Rust pins, and one
    /// success, one failure and one round trip through <see cref="NativeGestures"/> and
    /// <see cref="NativeKeyboard"/>. Every export here is any-thread. The grammar's own vectors live
    /// in Rust beside it (prisc_hotkeys gesture.rs and reserved.rs).</summary>
    public sealed class KeysTests
    {
        // Avalonia KeyModifiers bits and HotkeyKey values (Prisc.Shared's generated enums), as the ABI carries them
        private const int Alt = 1, Control = 2, Shift = 4, Meta = 8;
        private const int KeyNone = 0, KeyTab = 3, KeyPrintScreen = 30, KeyQ = 60, KeyX = 67, KeyLeftShift = 116;

        [Fact]
        public unsafe void The_gesture_struct_has_the_layout_rust_pins()
        {
            PriscGesture g = default;
            byte* b = (byte*)&g;
            Assert.Equal(8, sizeof(PriscGesture));
            Assert.Equal(0, (byte*)&g.key - b);
            Assert.Equal(4, (byte*)&g.modifiers - b);
        }

        [Fact]
        public void The_gesture_enums_match_the_abi()
        {
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<GestureStyle>().Select(v => (int)v));
            Assert.Equal(new[] { "Serialized", "Display", "ModifiersPrefix" }, Enum.GetNames<GestureStyle>());
            Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<GestureOs>().Select(v => (int)v));
            Assert.Equal(new[] { "Current", "Windows", "MacOS", "Linux" }, Enum.GetNames<GestureOs>());
            Assert.Equal(Enumerable.Range(0, 8), Enum.GetValues<GestureRefusal>().Select(v => (int)v));
            Assert.Equal(7, (int)GestureRefusal.Reserved);
        }

        [Fact]
        public async Task Parse_and_format_round_trip_from_a_pool_thread()
        {
            InteropTestGate.Require();
            await Task.Run(() =>
            {
                Assert.True(NativeGestures.TryParse("ctrl+shift+prtscr", out var key, out var modifiers, out var error), error);
                Assert.Equal((KeyPrintScreen, Control | Shift), (key, modifiers));
                Assert.Null(error);

                var serialized = NativeGestures.Format(key, modifiers, GestureStyle.Serialized);
                Assert.Equal("Control+Shift+PrintScreen", serialized);
                Assert.True(NativeGestures.TryParse(serialized, out var again, out var againModifiers, out _));
                Assert.Equal((key, modifiers), (again, againModifiers));

                Assert.Equal("Ctrl+Shift+PrtScr", NativeGestures.Format(key, modifiers, GestureStyle.Display, GestureOs.Windows));
                Assert.True(NativeGestures.TryParse("None", out key, out modifiers, out _));
                Assert.Equal((KeyNone, 0), (key, modifiers));
            });
        }

        [Fact]
        public void Text_that_is_no_gesture_says_why()
        {
            InteropTestGate.Require();
            Assert.False(NativeGestures.TryParse("Hyper+A", out var key, out var modifiers, out var error));
            Assert.Equal("unknown modifier 'Hyper'", error);
            Assert.Equal((0, 0), (key, modifiers));
            Assert.False(NativeGestures.TryParse(null, out _, out _, out error));
            Assert.Equal("empty gesture", error);
            // non-ASCII never folds (the Kelvin sign is not K)
            Assert.False(NativeGestures.TryParse("Ctrl+K", out _, out _, out _));
        }

        [Fact]
        public void Display_names_meta_per_os_and_the_prefix_drops_the_key()
        {
            InteropTestGate.Require();
            const int all = Control | Alt | Shift | Meta;
            Assert.Equal("Ctrl+Alt+Shift+Win+X", NativeGestures.Format(KeyX, all, GestureStyle.Display, GestureOs.Windows));
            Assert.Equal("Ctrl+Alt+Shift+Cmd+X", NativeGestures.Format(KeyX, all, GestureStyle.Display, GestureOs.MacOS));
            Assert.Equal("Ctrl+Alt+Shift+Super+X", NativeGestures.Format(KeyX, all, GestureStyle.Display, GestureOs.Linux));
            Assert.Equal("Ctrl+Cmd+", NativeGestures.Format(KeyNone, Control | Meta, GestureStyle.ModifiersPrefix, GestureOs.MacOS));
            Assert.Equal("", NativeGestures.Format(KeyNone, Control, GestureStyle.Display));
            var meta = OperatingSystem.IsWindows() ? "Win" : OperatingSystem.IsMacOS() ? "Cmd" : "Super";
            Assert.Equal($"{meta}+", NativeGestures.Format(KeyNone, Meta, GestureStyle.ModifiersPrefix));
        }

        [Fact]
        public void Formatting_a_value_outside_the_key_table_throws_invalid_arg()
        {
            InteropTestGate.Require();
            var e = Assert.Throws<InteropException>(() => NativeGestures.Format(100_004, 0, GestureStyle.Serialized));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
            Assert.Contains("100004 is not a key", e.Message);
            Assert.Throws<InteropException>(() => NativeGestures.Format(KeyX, 0x10, GestureStyle.Display));
            Assert.Throws<InteropException>(() => NativeGestures.Format(KeyX, 0, (GestureStyle)3));
        }

        [Fact]
        public void Check_answers_for_the_os_asked()
        {
            InteropTestGate.Require();
            Assert.Equal(GestureRefusal.Reserved, NativeGestures.Check(KeyTab, Alt, GestureOs.Windows));
            Assert.Equal(GestureRefusal.None, NativeGestures.Check(KeyTab, Alt, GestureOs.MacOS));
            Assert.Equal(GestureRefusal.Reserved, NativeGestures.Check(KeyQ, Meta, GestureOs.MacOS));
            Assert.Equal(GestureRefusal.UnsupportedKey, NativeGestures.Check(KeyPrintScreen, 0, GestureOs.MacOS));
            Assert.Equal(GestureRefusal.ModifierOnly, NativeGestures.Check(KeyLeftShift, 0));
            Assert.Equal(GestureRefusal.NoKey, NativeGestures.Check(KeyNone, Control));
            Assert.Equal(GestureRefusal.InvalidKey, NativeGestures.Check(-1, 0));
            Assert.Equal(GestureRefusal.InvalidModifiers, NativeGestures.Check(KeyX, 0x10));
            var e = Assert.Throws<InteropException>(() => NativeGestures.Check(KeyX, 0, (GestureOs)9));
            Assert.Equal(InteropStatus.InvalidArg, e.Status);
        }

        [Fact]
        public async Task Keys_down_asks_once_for_up_to_64_codes()
        {
            InteropTestGate.Require();
            Assert.Equal(0UL, NativeKeyboard.KeysDown(Array.Empty<int>()));

            // codes outside every OS's numbering cannot be answered, so they read as down
            Assert.Equal(0b11UL, await Task.Run(() => NativeKeyboard.KeysDown(new[] { 0x1_0000, -1 })));

            // Escape (VK 0x1B, kVK_Escape 0x35): nobody holds it during a test run
            int escape = OperatingSystem.IsWindows() ? 0x1B : 0x35;
            var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 0UL : 1UL;
            Assert.Equal(expected, NativeKeyboard.KeysDown(new[] { escape }));

            Assert.Equal(ulong.MaxValue, NativeKeyboard.KeysDown(Enumerable.Repeat(0x1_0000, NativeKeyboard.MaxCodes).ToArray()));
            Assert.Throws<ArgumentException>(() => NativeKeyboard.KeysDown(new int[NativeKeyboard.MaxCodes + 1]));
        }

        [Fact]
        public void Secure_input_is_off_where_it_does_not_exist()
        {
            InteropTestGate.Require();
            var enabled = NativeKeyboard.SecureInputEnabled();
            if (!OperatingSystem.IsMacOS())
                Assert.False(enabled);
        }
    }
}
