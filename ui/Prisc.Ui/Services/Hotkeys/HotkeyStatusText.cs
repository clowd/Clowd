using System;
using Prisc.Interop;

namespace Prisc.UI
{
    /// <summary>
    /// The one owner of the user-visible hotkey texts, keyed by prisc_hotkeys' status code
    /// (<see cref="HotkeyErrorCodes"/>, generated from the crate's code table). Both paths render
    /// through it: the in-process hook (<see cref="NativeHotkeyHost"/>, whose states and refusals map
    /// onto the codes) and the standby capturer's <c>settings_status</c>
    /// (<see cref="HotkeyManager.ApplyExternalStatus"/>), whose own display text stays in its logs.
    /// </summary>
    internal static class HotkeyStatusText
    {
        /// <summary>No restart hint: the hook picks the grant up by itself.</summary>
        internal const string PermissionMessage =
            "Could not start the global hotkey listener. Grant Accessibility permission to Prisc under " +
            MacPermissions.GrantLocation + "; the hotkeys start working as soon as you do.";

        /// <summary>The permission reads as granted, yet the tap was made without key events: a grant
        /// macOS still lists (an older build's, or one removed with '−' while it still answers yes)
        /// but no longer honours. Granting again does nothing; re-adding does. The System page shows
        /// Accessibility as granted, so this must not ask for it.</summary>
        internal const string StaleGrantMessage =
            "Could not start the global hotkey listener. macOS lists Prisc as allowed to use Accessibility, " +
            "but the permission no longer works: remove Prisc from System Settings → Privacy & Security → " +
            "Accessibility and add it again; the hotkeys start working as soon as you do.";

        internal const string UnsupportedMessage = "Global hotkeys are not supported on Linux yet.";

        internal const string DuplicateMessage = "Gesture is already in-use by another hotkey.";

        internal const string ModifierKeyMessage = "A modifier key on its own cannot be a global hotkey.";

        internal const string NoKeyMessage = "This gesture has no key.";

        /// <summary>The text for <paramref name="code"/>. <paramref name="detail"/> is the hook's own
        /// words (a start failure's message, a stop reason), used where the code alone cannot say
        /// enough; <paramref name="recoverable"/> says whether a stopped hook comes back by itself;
        /// <paramref name="gesture"/> names the key a refusal is about. An unknown or missing code
        /// shows <paramref name="detail"/> as it came.</summary>
        internal static string For(string code, string detail, bool recoverable, HotkeyGesture gesture = null) => code switch
        {
            HotkeyErrorCodes.Permission => PermissionMessage,
            HotkeyErrorCodes.StaleGrant => StaleGrantMessage,
            HotkeyErrorCodes.HookFailed => StartError(detail),
            HotkeyErrorCodes.HookStopped => StoppedError(detail, recoverable),
            HotkeyErrorCodes.Unsupported => UnsupportedMessage,
            HotkeyErrorCodes.Duplicate => DuplicateMessage,
            HotkeyErrorCodes.UnsupportedKey => gesture != null
                ? $"The key '{gesture}' cannot be used as a global hotkey."
                : "This key cannot be used as a global hotkey.",
            HotkeyErrorCodes.ModifierKey => ModifierKeyMessage,
            HotkeyErrorCodes.NoKey => NoKeyMessage,
            _ => detail ?? "",
        };

        /// <summary>The code of a down <paramref name="state"/>; null for <see cref="HookState.Running"/>.</summary>
        internal static string CodeOf(HookState state) => state switch
        {
            HookState.Running => null,
            HookState.PermissionMissing => HotkeyErrorCodes.Permission,
            HookState.PermissionStale => HotkeyErrorCodes.StaleGrant,
            HookState.Stopped => HotkeyErrorCodes.HookStopped,
            _ => HotkeyErrorCodes.HookFailed,
        };

        /// <summary>Why a gesture the hook refused cannot be a global hotkey.</summary>
        internal static string For(GestureRefusal refusal, HotkeyGesture gesture) => refusal switch
        {
            GestureRefusal.Duplicate => For(HotkeyErrorCodes.Duplicate, null, false, gesture),
            GestureRefusal.UnsupportedKey or GestureRefusal.InvalidKey => For(HotkeyErrorCodes.UnsupportedKey, null, false, gesture),
            GestureRefusal.ModifierOnly => For(HotkeyErrorCodes.ModifierKey, null, false, gesture),
            GestureRefusal.NoKey => For(HotkeyErrorCodes.NoKey, null, false, gesture),
            _ => $"Could not register the hotkey ({refusal}).",
        };

        /// <summary>Why the hook could not start, in its own words.</summary>
        internal static string StartError(string detail) =>
            "Could not start the global hotkey listener: " + (String.IsNullOrEmpty(detail) ? "unknown error" : detail);

        /// <summary>What every registration and recording reports once the hook stopped, with the
        /// reason it gave (or none). Only a hook that comes back by itself
        /// (<paramref name="restartsItself"/>, macOS) asks for no restart.</summary>
        internal static string StoppedError(string reason, bool restartsItself = false) =>
            "The global hotkey listener stopped" + (String.IsNullOrEmpty(reason) ? "." : ": " + reason.TrimEnd('.') + ".")
            + (restartsItself ? "" : " Restart Prisc to use global hotkeys again.");
    }
}
