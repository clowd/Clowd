using System;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The one piece of arithmetic between a level feed and a level pill's width: peak dBFS onto the
    /// 0..1 fraction the tray's split toggles animate. It lives on its own because it is the only place
    /// a silent input and an absent one can be confused, and the two must look different: a silent
    /// input reads 0 (an empty pill), an input that does not exist reads <c>null</c> so the pill
    /// collapses to its 8 px minimum instead of freezing at whatever the last sample happened to be.
    /// </summary>
    public static class TrayLevel
    {
        /// <summary>
        /// Peak dBFS → 0..1 over a −60 dB floor — the mapping the old <c>SetMeterFill</c> drew the
        /// 100 ms level feed with (and the WASAPI/CoreAudio listeners before it), which expressed the
        /// same line as <c>clamp(db / 60 * 100 + 100)</c> percent. Anything at or below −60 dB is 0,
        /// 0 dBFS is 1, and a positive (clipping) sample stays at 1. <c>null</c> in ⇒ <c>null</c> out:
        /// that source does not exist, or the feed behind it was torn down.
        /// </summary>
        public static double? FromPeakDbfs(double? db) => db == null ? null : Math.Clamp(db.Value / 60.0 + 1.0, 0, 1);
    }
}
