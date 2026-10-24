using System;
using Avalonia.Input;
using Xunit;

namespace Clowd.Shared.Tests
{
    /// <summary>
    /// <see cref="HotkeyKey"/> (generated from the clowd_hotkeys key table) mirrors Avalonia's
    /// <see cref="Key"/> one to one today. Nothing depends on that for matching keys, but the tray
    /// menu shows global hotkeys through <see cref="HotkeyGesture.ToKeyGestureOrNull"/>, which goes
    /// by name and silently shows no shortcut once the two tables drift apart. These fail first.
    /// </summary>
    public class HotkeyAvaloniaDriftTests
    {
        [Fact]
        public void Every_hotkey_key_is_the_avalonia_key_of_the_same_name_and_value()
        {
            foreach (var key in Enum.GetValues<HotkeyKey>())
            {
                if (key == HotkeyKey.None)
                    continue;

                var keyGesture = new HotkeyGesture(key).ToKeyGestureOrNull();
                Assert.True(keyGesture != null, $"HotkeyKey.{key} has no Avalonia Key of that name");
                Assert.True((int)keyGesture.Key == (int)key,
                    $"HotkeyKey.{key} is {(int)key} but Avalonia's Key.{keyGesture.Key} is {(int)keyGesture.Key}");
            }
        }

        [Fact]
        public void Every_avalonia_key_name_parses_to_the_hotkey_key_of_the_same_value()
        {
            foreach (var name in Enum.GetNames<Key>())
            {
                var value = (int)Enum.Parse<Key>(name);
                Assert.True(HotkeyKeys.TryParse(name, out var key), $"Avalonia's Key.{name} is not a HotkeyKey name");
                Assert.True((int)key == value, $"Avalonia's Key.{name} is {value} but parses to HotkeyKey.{key} ({(int)key})");
            }
        }

        [Fact]
        public void Modifiers_match_by_name_and_value()
        {
            var all = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta;
            var keyGesture = new HotkeyGesture(HotkeyKey.A, all).ToKeyGestureOrNull();

            Assert.NotNull(keyGesture);
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta, keyGesture.KeyModifiers);
            foreach (var modifier in new[] { HotkeyModifiers.Alt, HotkeyModifiers.Control, HotkeyModifiers.Shift, HotkeyModifiers.Meta })
                Assert.Equal((int)modifier, (int)Enum.Parse<KeyModifiers>(modifier.ToString()));
        }

        [Fact]
        public void A_gesture_with_no_key_has_no_key_gesture()
        {
            Assert.Null(new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.Control).ToKeyGestureOrNull());
        }
    }
}
