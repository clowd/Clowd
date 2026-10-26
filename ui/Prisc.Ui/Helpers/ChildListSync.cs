using System;
using System.Collections.Generic;

namespace Prisc.UI.Helpers
{
    /// <summary>
    /// Brings a panel's children in line with the list they should be, touching as little as
    /// possible: children that stay in place are left alone, and one child can be pinned so it is
    /// never taken out of the list at all, even when others move around it.
    /// </summary>
    /// <remarks>
    /// Pinning is for a child that holds the keyboard focus and must keep it: removing a control
    /// from the visual tree takes its focus away (raising LostFocus), and for the Hotkeys page's
    /// listening shortcut field a lost focus means a canceled recording. That field's own edit
    /// changes the list it sits in — the gesture being re-recorded leaves the action while the
    /// field listens — so a rebuild-everything refresh would cancel every edit the moment it
    /// began. Generic over the list so the rules are tested without a UI.
    /// </remarks>
    public static class ChildListSync
    {
        /// <summary>
        /// Makes <paramref name="items"/> equal to <paramref name="desired"/> (which must not
        /// repeat an item). <paramref name="pinned"/>, when already in <paramref name="items"/>,
        /// is never removed: whatever stands between it and its place is moved instead.
        /// </summary>
        public static void Sync<T>(IList<T> items, IReadOnlyList<T> desired, T pinned = null) where T : class
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            if (desired == null)
                throw new ArgumentNullException(nameof(desired));

            var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);

            // whatever is no longer wanted goes first, so what is left is only ever out of order
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(items[i]))
                    items.RemoveAt(i);
            }

            for (int k = 0; k < desired.Count; k++)
            {
                var want = desired[k];
                if (k < items.Count && ReferenceEquals(items[k], want))
                    continue;

                var at = IndexOf(items, want, k + 1);
                if (at >= 0 && ReferenceEquals(want, pinned))
                {
                    // the pinned item sits further on: move the items in its way out instead (each
                    // is still wanted, so a later step puts it back where it belongs).
                    while (!ReferenceEquals(items[k], want))
                        items.RemoveAt(k);
                    continue;
                }

                if (at >= 0)
                    items.RemoveAt(at);

                items.Insert(k, want);
            }

            // nothing unwanted is left, so anything past the end is a duplicate placement — which
            // cannot happen when desired holds each item once; kept as a guard all the same.
            while (items.Count > desired.Count)
                items.RemoveAt(items.Count - 1);
        }

        private static int IndexOf<T>(IList<T> items, T item, int start) where T : class
        {
            for (int i = start; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], item))
                    return i;
            }

            return -1;
        }
    }
}
