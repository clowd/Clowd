using System;
using System.Collections.Generic;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>
    /// The keys <see cref="KeystrokeFeed"/> believes are held, and which of them a new press shows
    /// were released without their key-up reaching the hook (the secure desktop or the lock screen
    /// took it) — left alone they would read as held forever and turn all later typing into chords.
    /// <para>
    /// The edges arrive on the hotkey event thread some time after the hook saw them, so the OS key
    /// state is newer than the edge being handled and must not be read as the state at that edge:
    /// a key-up still queued behind it would be released early and break a chord apart. Instead, a
    /// modifier is released when the hook's own snapshot at the edge (<see cref="GlobalKeyEvent.Modifiers"/>)
    /// no longer holds it, and any other key only once it has been quiet for <see cref="StaleMs"/>
    /// and the OS says it is up.
    /// </para>
    /// Pure (the OS query is passed in) and single-threaded.
    /// </summary>
    public sealed class KeystrokeHeldKeys
    {
        /// <summary>Longer than the longest initial auto-repeat delay (1 s on Windows and macOS): a
        /// held key repeats, so one this quiet was released or is held across another press.</summary>
        public const double StaleMs = 1500;

        // code -> the modifier flag its press added (None for other keys) and its last press/repeat
        private readonly Dictionary<int, (HotkeyModifiers Flag, double LastMs)> _held = new();

        // the hook's modifiers at the previous edge; null until the first, so keys already held when
        // observing started are never taken for modifiers
        private HotkeyModifiers? _lastModifiers;

        /// <summary>A press or auto-repeat of <paramref name="code"/>, with the hook's modifiers at
        /// it. Returns the held keys to release before it, in no particular order.</summary>
        public IReadOnlyList<int> Down(int code, HotkeyModifiers modifiers, double nowMs, Func<int, bool> isPhysicallyDown)
        {
            List<int> released = null;
            foreach (var (held, state) in _held)
            {
                if (held == code)
                    continue;

                var gone = state.Flag != HotkeyModifiers.None
                    ? (modifiers & state.Flag) == 0
                    : nowMs - state.LastMs >= StaleMs && !isPhysicallyDown(held);
                if (gone)
                    (released ??= new()).Add(held);
            }

            if (released != null)
            {
                foreach (var held in released)
                    _held.Remove(held);
            }

            // a modifier's press is the edge that adds its flag; every other press leaves them be
            var flag = _held.TryGetValue(code, out var known)
                ? known.Flag
                : _lastModifiers is { } last ? modifiers & ~last : HotkeyModifiers.None;
            _held[code] = (flag, nowMs);
            _lastModifiers = modifiers;
            return released ?? (IReadOnlyList<int>)Array.Empty<int>();
        }

        /// <summary>A release of <paramref name="code"/>, with the hook's modifiers after it.</summary>
        public void Up(int code, HotkeyModifiers modifiers)
        {
            _held.Remove(code);
            _lastModifiers = modifiers;
        }
    }
}
