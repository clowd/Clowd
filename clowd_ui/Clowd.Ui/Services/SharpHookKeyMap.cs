using SharpHook.Data;
using AvKey = Avalonia.Input.Key;
using AvMods = Avalonia.Input.KeyModifiers;

namespace Clowd.UI
{
    /// <summary>
    /// Translates Avalonia <see cref="AvKey"/>/<see cref="AvMods"/> to the SharpHook native
    /// equivalents so hotkeys defined in settings can be matched against live keyboard hook events.
    /// </summary>
    public static class SharpHookKeyMap
    {
        /// <summary>
        /// Maps an Avalonia <see cref="AvKey"/> to a SharpHook <see cref="KeyCode"/>.
        /// Returns <c>null</c> for keys we can't register as global hotkeys (Key.None, unknown keys).
        /// Note: <see cref="AvKey.Snapshot"/> is an alias of <see cref="AvKey.PrintScreen"/> in Avalonia.
        /// </summary>
        public static KeyCode? TryMapKey(AvKey key) => key switch
        {
            // Letters
            AvKey.A => KeyCode.VcA, AvKey.B => KeyCode.VcB, AvKey.C => KeyCode.VcC,
            AvKey.D => KeyCode.VcD, AvKey.E => KeyCode.VcE, AvKey.F => KeyCode.VcF,
            AvKey.G => KeyCode.VcG, AvKey.H => KeyCode.VcH, AvKey.I => KeyCode.VcI,
            AvKey.J => KeyCode.VcJ, AvKey.K => KeyCode.VcK, AvKey.L => KeyCode.VcL,
            AvKey.M => KeyCode.VcM, AvKey.N => KeyCode.VcN, AvKey.O => KeyCode.VcO,
            AvKey.P => KeyCode.VcP, AvKey.Q => KeyCode.VcQ, AvKey.R => KeyCode.VcR,
            AvKey.S => KeyCode.VcS, AvKey.T => KeyCode.VcT, AvKey.U => KeyCode.VcU,
            AvKey.V => KeyCode.VcV, AvKey.W => KeyCode.VcW, AvKey.X => KeyCode.VcX,
            AvKey.Y => KeyCode.VcY, AvKey.Z => KeyCode.VcZ,

            // Top-row digits
            AvKey.D0 => KeyCode.Vc0, AvKey.D1 => KeyCode.Vc1, AvKey.D2 => KeyCode.Vc2,
            AvKey.D3 => KeyCode.Vc3, AvKey.D4 => KeyCode.Vc4, AvKey.D5 => KeyCode.Vc5,
            AvKey.D6 => KeyCode.Vc6, AvKey.D7 => KeyCode.Vc7, AvKey.D8 => KeyCode.Vc8,
            AvKey.D9 => KeyCode.Vc9,

            // Function keys
            AvKey.F1 => KeyCode.VcF1,   AvKey.F2 => KeyCode.VcF2,   AvKey.F3 => KeyCode.VcF3,
            AvKey.F4 => KeyCode.VcF4,   AvKey.F5 => KeyCode.VcF5,   AvKey.F6 => KeyCode.VcF6,
            AvKey.F7 => KeyCode.VcF7,   AvKey.F8 => KeyCode.VcF8,   AvKey.F9 => KeyCode.VcF9,
            AvKey.F10 => KeyCode.VcF10, AvKey.F11 => KeyCode.VcF11, AvKey.F12 => KeyCode.VcF12,
            AvKey.F13 => KeyCode.VcF13, AvKey.F14 => KeyCode.VcF14, AvKey.F15 => KeyCode.VcF15,
            AvKey.F16 => KeyCode.VcF16, AvKey.F17 => KeyCode.VcF17, AvKey.F18 => KeyCode.VcF18,
            AvKey.F19 => KeyCode.VcF19, AvKey.F20 => KeyCode.VcF20, AvKey.F21 => KeyCode.VcF21,
            AvKey.F22 => KeyCode.VcF22, AvKey.F23 => KeyCode.VcF23, AvKey.F24 => KeyCode.VcF24,

            // Navigation / editing
            AvKey.Escape => KeyCode.VcEscape,
            AvKey.Tab => KeyCode.VcTab,
            AvKey.Enter => KeyCode.VcEnter,
            AvKey.Space => KeyCode.VcSpace,
            AvKey.Back => KeyCode.VcBackspace,
            AvKey.Delete => KeyCode.VcDelete,
            AvKey.Insert => KeyCode.VcInsert,
            AvKey.Home => KeyCode.VcHome,
            AvKey.End => KeyCode.VcEnd,
            AvKey.PageUp => KeyCode.VcPageUp,
            AvKey.PageDown => KeyCode.VcPageDown,
            AvKey.Up => KeyCode.VcUp,
            AvKey.Down => KeyCode.VcDown,
            AvKey.Left => KeyCode.VcLeft,
            AvKey.Right => KeyCode.VcRight,

            // Lock / system
            AvKey.CapsLock => KeyCode.VcCapsLock,
            AvKey.NumLock => KeyCode.VcNumLock,
            AvKey.Scroll => KeyCode.VcScrollLock,
            AvKey.PrintScreen => KeyCode.VcPrintScreen, // == AvKey.Snapshot
            AvKey.Pause => KeyCode.VcPause,
            AvKey.Help => KeyCode.VcHelp,
            AvKey.Cancel => KeyCode.VcCancel,
            AvKey.Apps => KeyCode.VcContextMenu,
            AvKey.Sleep => KeyCode.VcSleep,

            // Punctuation (OEM keys on a US layout)
            AvKey.OemTilde => KeyCode.VcBackQuote,
            AvKey.OemMinus => KeyCode.VcMinus,
            AvKey.OemPlus => KeyCode.VcEquals,
            AvKey.OemOpenBrackets => KeyCode.VcOpenBracket,
            AvKey.OemCloseBrackets => KeyCode.VcCloseBracket,
            AvKey.OemPipe => KeyCode.VcBackslash,
            AvKey.OemSemicolon => KeyCode.VcSemicolon,
            AvKey.OemQuotes => KeyCode.VcQuote,
            AvKey.OemComma => KeyCode.VcComma,
            AvKey.OemPeriod => KeyCode.VcPeriod,
            AvKey.OemQuestion => KeyCode.VcSlash,

            // Numpad
            AvKey.NumPad0 => KeyCode.VcNumPad0, AvKey.NumPad1 => KeyCode.VcNumPad1,
            AvKey.NumPad2 => KeyCode.VcNumPad2, AvKey.NumPad3 => KeyCode.VcNumPad3,
            AvKey.NumPad4 => KeyCode.VcNumPad4, AvKey.NumPad5 => KeyCode.VcNumPad5,
            AvKey.NumPad6 => KeyCode.VcNumPad6, AvKey.NumPad7 => KeyCode.VcNumPad7,
            AvKey.NumPad8 => KeyCode.VcNumPad8, AvKey.NumPad9 => KeyCode.VcNumPad9,
            AvKey.Divide => KeyCode.VcNumPadDivide,
            AvKey.Multiply => KeyCode.VcNumPadMultiply,
            AvKey.Subtract => KeyCode.VcNumPadSubtract,
            AvKey.Add => KeyCode.VcNumPadAdd,
            AvKey.Decimal => KeyCode.VcNumPadDecimal,
            AvKey.Separator => KeyCode.VcNumPadSeparator,

            // Media
            AvKey.MediaPlayPause => KeyCode.VcMediaPlay,
            AvKey.MediaStop => KeyCode.VcMediaStop,
            AvKey.MediaNextTrack => KeyCode.VcMediaNext,
            AvKey.MediaPreviousTrack => KeyCode.VcMediaPrevious,
            AvKey.VolumeUp => KeyCode.VcVolumeUp,
            AvKey.VolumeDown => KeyCode.VcVolumeDown,
            AvKey.VolumeMute => KeyCode.VcVolumeMute,
            AvKey.SelectMedia => KeyCode.VcMediaSelect,

            // Browser
            AvKey.BrowserBack => KeyCode.VcBrowserBack,
            AvKey.BrowserForward => KeyCode.VcBrowserForward,
            AvKey.BrowserRefresh => KeyCode.VcBrowserRefresh,
            AvKey.BrowserStop => KeyCode.VcBrowserStop,
            AvKey.BrowserSearch => KeyCode.VcBrowserSearch,
            AvKey.BrowserFavorites => KeyCode.VcBrowserFavorites,
            AvKey.BrowserHome => KeyCode.VcBrowserHome,
            AvKey.LaunchMail => KeyCode.VcAppMail,
            AvKey.LaunchApplication1 => KeyCode.VcApp1,
            AvKey.LaunchApplication2 => KeyCode.VcApp2,

            _ => null,
        };

        /// <summary>
        /// Returns true if <paramref name="incoming"/> (the mask on a live key event) matches the
        /// modifier set <paramref name="expected"/> from a stored gesture — treating Left/Right
        /// variants of each modifier as equivalent and requiring exact equality otherwise.
        /// </summary>
        public static bool ModifiersMatch(EventMask incoming, AvMods expected)
        {
            bool wantCtrl = (expected & AvMods.Control) != 0;
            bool wantShift = (expected & AvMods.Shift) != 0;
            bool wantAlt = (expected & AvMods.Alt) != 0;
            bool wantMeta = (expected & AvMods.Meta) != 0;

            bool hasCtrl = (incoming & EventMask.Ctrl) != EventMask.None;
            bool hasShift = (incoming & EventMask.Shift) != EventMask.None;
            bool hasAlt = (incoming & EventMask.Alt) != EventMask.None;
            bool hasMeta = (incoming & EventMask.Meta) != EventMask.None;

            return wantCtrl == hasCtrl
                   && wantShift == hasShift
                   && wantAlt == hasAlt
                   && wantMeta == hasMeta;
        }
    }
}
