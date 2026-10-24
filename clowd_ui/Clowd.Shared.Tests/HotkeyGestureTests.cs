using System;
using Xunit;

namespace Clowd.Shared.Tests
{
    /// <summary>
    /// <see cref="HotkeyGesture"/> parses and prints exactly as the clowd_hotkeys crate's
    /// <c>Gesture</c> does (the same vectors as its unit tests), since the settings file, the
    /// standby capturer's command line and its status echo all carry that one text.
    /// </summary>
    public class HotkeyGestureTests
    {
        [Theory]
        [InlineData("PrintScreen", "PrintScreen")]
        [InlineData("Snapshot", "PrintScreen")]
        [InlineData("control+snapshot", "Control+PrintScreen")]
        [InlineData("Shift+Control+A", "Control+Shift+A")]
        [InlineData(" Control + Shift + D1 ", "Control+Shift+D1")]
        [InlineData("Meta+Alt+OemTilde", "Alt+Meta+Oem3")]
        [InlineData("Enter", "Return")]
        [InlineData("A+", "A")]
        [InlineData("None", "None")]
        [InlineData("Control+Control+A", "Control+A")]
        [InlineData("Ctrl+PrtScr", "Control+PrintScreen")]
        [InlineData("ctrl+shift+1", "Control+Shift+D1")]
        [InlineData("Cmd+Shift+3", "Shift+Meta+D3")]
        [InlineData("Win+Esc", "Meta+Escape")]
        [InlineData("Super+A", "Meta+A")]
        [InlineData("Option+Del", "Alt+Delete")]
        [InlineData("Ctrl+Num+", "Control+Add")]
        [InlineData("Num+", "Add")]
        [InlineData("Ctrl+Num-", "Control+Subtract")]
        [InlineData("Ctrl+=", "Control+OemPlus")]
        [InlineData("Alt+Num7", "Alt+NumPad7")]
        public void Parses_like_the_crate_and_prints_the_canonical_form(string input, string serialized)
        {
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
        [InlineData("Control+NotAKey")]
        [InlineData("Hyper+A")]
        [InlineData("Ctrl+")]
        [InlineData("A+B")]
        [InlineData("30")]
        [InlineData("A,B")]
        [InlineData("Control+Shift")] // Shift is a modifier, not a key name
        public void Refuses_what_the_crate_refuses(string input)
        {
            Assert.Null(HotkeyGesture.Parse(input));
        }

        [Fact]
        public void None_is_a_gesture_with_no_key()
        {
            Assert.Equal(new HotkeyGesture(HotkeyKey.None), HotkeyGesture.Parse("None"));
            Assert.Equal("None", new HotkeyGesture(HotkeyKey.None).ToSerializedString());
            Assert.Equal("", new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.Control).ToString());
        }

        [Theory]
        [InlineData("Enter", HotkeyKey.Return)]
        [InlineData("Capital", HotkeyKey.CapsLock)]
        [InlineData("KanaMode", HotkeyKey.HangulMode)]
        [InlineData("KanjiMode", HotkeyKey.HanjaMode)]
        [InlineData("Prior", HotkeyKey.PageUp)]
        [InlineData("Next", HotkeyKey.PageDown)]
        [InlineData("Snapshot", HotkeyKey.PrintScreen)]
        [InlineData("Oem1", HotkeyKey.OemSemicolon)]
        [InlineData("Oem2", HotkeyKey.OemQuestion)]
        [InlineData("OemTilde", HotkeyKey.Oem3)]
        [InlineData("OemOpenBrackets", HotkeyKey.Oem4)]
        [InlineData("Oem5", HotkeyKey.OemPipe)]
        [InlineData("Oem6", HotkeyKey.OemCloseBrackets)]
        [InlineData("Oem7", HotkeyKey.OemQuotes)]
        [InlineData("Oem102", HotkeyKey.OemBackslash)]
        [InlineData("DbeAlphanumeric", HotkeyKey.OemAttn)]
        [InlineData("DbeKatakana", HotkeyKey.OemFinish)]
        [InlineData("DbeHiragana", HotkeyKey.OemCopy)]
        [InlineData("DbeSbcsChar", HotkeyKey.OemAuto)]
        [InlineData("DbeDbcsChar", HotkeyKey.OemEnlw)]
        [InlineData("DbeRoman", HotkeyKey.OemBackTab)]
        [InlineData("Attn", HotkeyKey.DbeNoRoman)]
        [InlineData("DbeEnterWordRegisterMode", HotkeyKey.CrSel)]
        [InlineData("DbeEnterImeConfigureMode", HotkeyKey.ExSel)]
        [InlineData("EraseEof", HotkeyKey.DbeFlushString)]
        [InlineData("Play", HotkeyKey.DbeCodeInput)]
        [InlineData("DbeNoCodeInput", HotkeyKey.Zoom)]
        [InlineData("DbeDetermineString", HotkeyKey.NoName)]
        [InlineData("Pa1", HotkeyKey.DbeEnterDialogConversionMode)]
        public void Every_alias_parses_to_its_canonical_key(string alias, HotkeyKey key)
        {
            Assert.True(HotkeyKeys.TryParse(alias, out var parsed));
            Assert.Equal(key, parsed);
            Assert.Equal(new HotkeyGesture(key), HotkeyGesture.Parse(alias));
        }

        [Fact]
        public void Every_key_name_round_trips()
        {
            foreach (var key in Enum.GetValues<HotkeyKey>())
            {
                var name = HotkeyKeys.Name(key);
                Assert.Equal(key.ToString(), name);
                Assert.True(HotkeyKeys.TryParse(name, out var parsed), name);
                Assert.Equal(key, parsed);
                Assert.True(HotkeyKeys.TryParse(name.ToLowerInvariant(), out parsed), name);
                Assert.Equal(key, parsed);
            }
        }

        [Theory]
        [InlineData(HotkeyKey.PrintScreen, HotkeyModifiers.None, "PrtScr")]
        [InlineData(HotkeyKey.PrintScreen, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+PrtScr")]
        [InlineData(HotkeyKey.Delete, HotkeyModifiers.Control, "Ctrl+Del")]
        [InlineData(HotkeyKey.Escape, HotkeyModifiers.None, "Esc")]
        [InlineData(HotkeyKey.D1, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+1")]
        [InlineData(HotkeyKey.D0, HotkeyModifiers.None, "0")]
        [InlineData(HotkeyKey.F4, HotkeyModifiers.Alt | HotkeyModifiers.Control, "Ctrl+Alt+F4")]
        [InlineData(HotkeyKey.OemPlus, HotkeyModifiers.Alt, "Alt+=")]
        [InlineData(HotkeyKey.PageDown, HotkeyModifiers.Shift, "Shift+PgDn")]
        [InlineData(HotkeyKey.NumPad7, HotkeyModifiers.None, "Num7")]
        [InlineData(HotkeyKey.Add, HotkeyModifiers.Control, "Ctrl+Num+")]
        [InlineData(HotkeyKey.MediaPlayPause, HotkeyModifiers.None, "Play/Pause")]
        public void Display_text(HotkeyKey key, HotkeyModifiers modifiers, string expected)
        {
            Assert.Equal(expected, new HotkeyGesture(key, modifiers).ToString());
        }

        [Fact]
        public void Display_text_parses_back_for_every_key()
        {
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
            var meta = OperatingSystem.IsWindows() ? "Win" : OperatingSystem.IsMacOS() ? "Cmd" : "Super";

            Assert.Equal($"Ctrl+Alt+Shift+{meta}+X",
                new HotkeyGesture(HotkeyKey.X, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta).ToString());
            Assert.Equal($"Ctrl+{meta}+", HotkeyGesture.ModifiersText(HotkeyModifiers.Meta | HotkeyModifiers.Control));
            Assert.Equal("", HotkeyGesture.ModifiersText(HotkeyModifiers.None));
        }

        [Fact]
        public void Task_switching_combinations_are_reserved_on_windows()
        {
            var reserved = new[]
            {
                new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt),
                new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Alt | HotkeyModifiers.Shift),
                new HotkeyGesture(HotkeyKey.Escape, HotkeyModifiers.Alt),
                new HotkeyGesture(HotkeyKey.Escape, HotkeyModifiers.Control),
            };

            foreach (var gesture in reserved)
                Assert.Equal(OperatingSystem.IsWindows(), HotkeyGesture.IsSystemReserved(gesture));

            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Tab)));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Tab, HotkeyModifiers.Control)));
            Assert.False(HotkeyGesture.IsSystemReserved(new HotkeyGesture(HotkeyKey.Escape)));
            Assert.False(HotkeyGesture.IsSystemReserved(null));
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
