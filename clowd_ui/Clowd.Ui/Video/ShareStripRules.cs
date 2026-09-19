namespace Clowd.UI
{
    /// <summary>
    /// The share strip's enable rules and tooltip words, kept pure so they can be pinned by tests
    /// and so <see cref="ShareRegionFloatingButtons"/> has exactly one place to read them from.
    /// Two owners drive the Hide tile — the permanent obscure retirement and the transient resize
    /// mode — and they are ANDed here rather than each writing <c>IsEnabled</c>, so neither can
    /// clobber the other: a resize that ends after the helper retired the effect must not hand the
    /// user back a Hide tile that cannot work.
    /// </summary>
    public static class ShareStripRules
    {
        /// <summary>Hide is pressable only while the helper can obscure at all and no resize is
        /// running or still being acknowledged.</summary>
        public static bool HideEnabled(bool obscureAvailable, bool resizeActive, bool resizeBusy)
            => obscureAvailable && !resizeActive && !resizeBusy;

        /// <summary>Resize is un-pressable only while a move is in flight — pressing it again then
        /// would put a second move on the wire, and the acks carry no request id.</summary>
        public static bool ResizeEnabled(bool resizeBusy) => !resizeBusy;

        /// <summary>The old tile's label named the verb ("HIDE" while visible, "SHOW" while hidden);
        /// the tooltip and accessible name now do.</summary>
        public static string HideToolTip(bool hidden)
            => hidden ? "Show the shared region" : "Hide the shared region";

        /// <summary>The next press of Resize commits the pending rectangle while the mode is on.</summary>
        public static string ResizeToolTip(bool active)
            => active ? "Apply the new region" : "Move or resize the region";
    }
}
