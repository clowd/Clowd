using System;
using Avalonia.Input;
using Prisc.Interop.Testing;
using Xunit;

namespace Prisc.Shared.Tests
{
    /// <summary>
    /// In-app shortcut text goes through the global hotkeys' display form
    /// (<c>prisc_gesture_format</c>), whose vectors live in prisc_hotkeys. These check the Avalonia
    /// key and modifiers reach it as themselves.
    /// </summary>
    public class SimpleKeyGestureTests
    {
        [Fact]
        public void Text_is_the_hotkey_display_form()
        {
            InteropTestGate.Require();
            // Avalonia's flag order is Alt before Control; the display order is Ctrl, Alt
            Assert.Equal("Ctrl+Alt+F4", new SimpleKeyGesture(Key.F4, KeyModifiers.Alt | KeyModifiers.Control).ToString());
            Assert.Equal("Shift+1", new SimpleKeyGesture(Key.D1, KeyModifiers.Shift).ToString());
            Assert.Equal("Ctrl+Del", new SimpleKeyGesture(Key.Delete, KeyModifiers.Control).ToString());
            Assert.Equal("Esc", new SimpleKeyGesture(Key.Escape).ToString());
            Assert.Equal(String.Empty, new SimpleKeyGesture(Key.None, KeyModifiers.Control).ToString());
            var meta = OperatingSystem.IsMacOS() ? "Cmd" : OperatingSystem.IsWindows() ? "Win" : "Super";
            Assert.Equal(meta + "+K", new SimpleKeyGesture(Key.K, KeyModifiers.Meta).ToString());
        }
    }
}
