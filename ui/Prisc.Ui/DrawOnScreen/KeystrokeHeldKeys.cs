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
    /// Pure (the OS query is passed in, and asked once per press for every stale key together) and
    /// single-threaded.
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

        /// <summary>The most stale keys one press asks the OS about (one bit each in the answer);
        /// any beyond it are asked about on a later press.</summary>
        public const int MaxQueried = 64;

        /// <summary>A press or auto-repeat of <paramref name="code"/>, with the hook's modifiers at
        /// it. Returns the held keys to release before it, in no particular order.
        /// <paramref name="keysDown"/> is the OS query: given the codes it is asked about, bit i of
        /// its answer says the i-th is physically down. It is called at most once per press, and only
        /// when some key has gone quiet for <see cref="StaleMs"/>.</summary>
        public IReadOnlyList<int> Down(int code, HotkeyModifiers modifiers, double nowMs, Func<IReadOnlyList<int>, ulong> keysDown)
        {
            List<int> released = null;
            List<int> stale = null;
            foreach (var (held, state) in _held)
            {
                if (held == code)
                    continue;

                if (state.Flag != HotkeyModifiers.None)
                {
                    if ((modifiers & state.Flag) == 0)
                        (released ??= new()).Add(held);
                }
                else if (nowMs - state.LastMs >= StaleMs && (stale?.Count ?? 0) < MaxQueried)
                {
                    (stale ??= new()).Add(held);
                }
            }

            if (stale != null)
            {
                var down = keysDown(stale);
                for (int i = 0; i < stale.Count; i++)
                {
                    if ((down & (1UL << i)) == 0)
                        (released ??= new()).Add(stale[i]);
                }
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
