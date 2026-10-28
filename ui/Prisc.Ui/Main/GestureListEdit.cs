using System;

namespace Prisc.UI.Config
{
    /// <summary>
    /// The list arithmetic behind recording one shortcut of a hotkey action on the Hotkeys page:
    /// either replacing the gesture at an index or appending a new one. Pure, so it is tested
    /// without a UI; <see cref="HotkeyShortcutsEditor"/> applies what it computes.
    /// </summary>
    /// <remarks>
    /// While a shortcut is being re-recorded the action holds <see cref="Listening"/>, which is
    /// the saved list without that gesture: the standby capturer's own hook is only paused for
    /// the recording on a best-effort basis (a message over its stdin), and would otherwise still
    /// catch the old combination — the one the user is most likely to press again. The gesture comes back on cancel, or is replaced in
    /// its old position on commit, so the order the user built up survives a re-recording.
    /// </remarks>
    public sealed class GestureListEdit
    {
        /// <summary>The action's gestures when the edit began; what a cancel puts back.</summary>
        public GestureList Saved { get; }

        /// <summary>The position being re-recorded, or null when a gesture is being appended.</summary>
        public int? ReplacedIndex { get; }

        private GestureListEdit(GestureList saved, int? replacedIndex)
        {
            Saved = saved ?? GestureList.Empty;
            ReplacedIndex = replacedIndex;
        }

        /// <summary>Re-recording the gesture at <paramref name="index"/> of <paramref name="gestures"/>.</summary>
        public static GestureListEdit Replace(GestureList gestures, int index)
        {
            gestures ??= GestureList.Empty;
            if ((uint)index >= (uint)gestures.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new GestureListEdit(gestures, index);
        }

        /// <summary>Recording a new gesture after the last of <paramref name="gestures"/>.</summary>
        public static GestureListEdit Append(GestureList gestures) => new GestureListEdit(gestures, null);

        /// <summary>What the action holds while the edit listens (see the remarks).</summary>
        public GestureList Listening => ReplacedIndex is int index ? Saved.RemoveAt(index) : Saved;

        /// <summary>Where the listening field sits among the action's current shortcuts, of which
        /// there are <paramref name="count"/>: the replaced gesture's old position, or the end.</summary>
        public int SlotIndex(int count) => Math.Clamp(ReplacedIndex ?? count, 0, Math.Max(0, count));

        /// <summary>
        /// The action's gestures once <paramref name="gesture"/> was recorded. A gesture the action
        /// already holds elsewhere changes nothing (an action cannot hold one twice), and an empty
        /// one is a cancel. Taking the gesture from another action is the assignment's business
        /// (<see cref="HotkeyEntry.Gestures"/>), not this list's.
        /// </summary>
        public GestureList Commit(HotkeyGesture gesture)
        {
            if (gesture == null || gesture.Key == HotkeyKey.None)
                return Saved;

            return ReplacedIndex is int index ? Saved.SetItem(index, gesture) : Saved.Add(gesture);
        }

        /// <summary>
        /// The position in <paramref name="result"/> (what <see cref="Commit"/> or a cancel left the
        /// action with) of the gesture the listening field now stands for, so the field — and the
        /// keyboard focus on it — stays put showing it; -1 when it stands for nothing new (an
        /// append that was canceled, or that recorded a gesture the action already showed).
        /// </summary>
        public int ResultIndex(GestureList result)
        {
            result ??= GestureList.Empty;
            if (ReplacedIndex is int index)
                return index < result.Count ? index : -1;

            return result.Count > Saved.Count ? result.Count - 1 : -1;
        }
    }
}
