using Avalonia.Input;

namespace Clowd
{
    /// <summary>
    /// Translates <see cref="Avalonia.Input.Key"/> names into the key tokens the Rust
    /// capturer's hotkey backend (the handy-keys crate) parses — "A", "1", "F12",
    /// "PrintScreen", "Keypad7", "Grave". The capturer does no translation of its own —
    /// this map is the single source of truth, and
    /// <c>clowd_capture/src/standby_hotkeys.rs</c> carries tests pinning the grammar.
    /// A key with no equivalent (IME keys, browser keys, …) passes through under its
    /// Avalonia name so the capturer rejects it with an explicit per-hotkey error
    /// instead of silently dropping the registration.
    /// </summary>
    public static class CapturerKeyMap
    {
        public static string ToCapturerToken(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return key.ToString();
            if (key >= Key.D0 && key <= Key.D9)
                return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "Keypad" + (key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F24)
                return key.ToString();

            return key switch
            {
                // handy-keys uses macOS naming: "Backspace" aliases its Delete
                // (the backspace key), and the PC Delete key is "ForwardDelete".
                Key.Back => "Backspace",
                Key.Delete => "ForwardDelete",
                Key.Tab => "Tab",
                Key.Return => "Enter",
                Key.Pause => "Pause",
                // Windows rewrites Pause to VK_CANCEL while Ctrl is held, so a
                // recorded Ctrl+Pause gesture arrives as Key.Cancel; the capturer's
                // hook sees the same rewrite and reports it as its Pause key.
                Key.Cancel => "Pause",
                Key.CapsLock => "CapsLock",
                Key.Escape => "Escape",
                Key.Space => "Space",
                Key.PageUp => "PageUp",
                Key.PageDown => "PageDown",
                Key.End => "End",
                Key.Home => "Home",
                Key.Left => "Left",
                Key.Up => "Up",
                Key.Right => "Right",
                Key.Down => "Down",
                Key.Snapshot => "PrintScreen",
                Key.Insert => "Insert",
                Key.Multiply => "KeypadMultiply",
                Key.Add => "KeypadPlus",
                Key.Subtract => "KeypadMinus",
                Key.Decimal => "KeypadDecimal",
                Key.Divide => "KeypadDivide",
                // Key.Separator deliberately unmapped: "KeypadComma" parses but no
                // Windows VK ever produces it in the capturer's hook, which would be
                // an active-but-dead hotkey — passthrough errors visibly instead.
                Key.NumLock => "NumLock",
                Key.Scroll => "ScrollLock",
                Key.OemSemicolon => "Semicolon",
                Key.OemPlus => "Equal",
                Key.OemComma => "Comma",
                Key.OemMinus => "Minus",
                Key.OemPeriod => "Period",
                Key.OemQuestion => "Slash",
                Key.OemTilde => "Grave",
                Key.OemOpenBrackets => "LeftBracket",
                Key.OemPipe => "Backslash",
                Key.OemCloseBrackets => "RightBracket",
                Key.OemQuotes => "Quote",
                Key.Apps => "ContextMenu",
                Key.MediaNextTrack => "NextTrack",
                Key.MediaPreviousTrack => "PrevTrack",
                Key.MediaStop => "Stop",
                Key.MediaPlayPause => "PlayPause",
                _ => key.ToString(),
            };
        }
    }
}
