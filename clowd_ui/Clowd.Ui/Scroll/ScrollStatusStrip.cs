using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI
{
    /// <summary>
    /// The scrolling capture HUD: a two-line progress readout plus Finish and Cancel, on the generic
    /// tray chassis. The chassis carries everything this window shares with the other strips —
    /// WS_EX_NOACTIVATE so a click can never pull focus away from the window being scrolled (which
    /// would send the synthetic wheel somewhere else), WS_EX_TOOLWINDOW so it stays out of the
    /// taskbar, and the placement search around the capture region.
    /// <para>The one rule this strip does not share with the movable strips: it is never shown inside
    /// the region. The driver photographs that rectangle after every scroll step, so a strip
    /// overlapping it by a single pixel would be stitched into the finished image. That is why it
    /// declares <see cref="TraySize"/> up front and is shown through
    /// <see cref="FloatingTrayWindow.TryShowNear"/>, which refuses rather than overlap: when nothing
    /// fits outside the region the run proceeds without a HUD — Esc and the automatic end detection
    /// still finish it.</para>
    /// <para>No grip: this strip is not movable and not rotatable. There is nothing to drag, so there
    /// is no grab affordance anywhere on it, and it is always horizontal. It also has no key handling
    /// of any kind — Esc belongs to the driver, which polls it while the target holds focus; the strip
    /// only advertises it.</para>
    /// </summary>
    public sealed class ScrollStatusStrip : FloatingTrayWindow
    {
        /// <summary>
        /// Width of the readout column in logical px, padding included (spec §8). Fixed rather than
        /// measured: it is sized for the longest line the page can produce — "Paused — stop moving the
        /// mouse to resume", which paints ≈213 px untrimmed in Segoe UI at 10 px (measured on screen
        /// 2026-09-19; the earlier estimate of ≈193–195 px was ~18 px low, which is why the 220 column
        /// trimmed it to "…to res…"). 240 leaves 222 px of text room once the block's 8/10 padding is
        /// taken out — ~4 % slack for hinting and ClearType variance. Anything longer still trims with
        /// an ellipsis. A column that grew to its text would change the strip's footprint mid-run, and
        /// the footprint has to be known before the window exists.
        /// </summary>
        private const double StatusWidth = 240;

        /// <summary>
        /// The strip's logical size, and the ONE place it is declared: the placement search needs it
        /// before the window has ever been laid out, so it cannot be measured. Derived from the tray's
        /// own tokens rather than hand-mirrored from the layout — 4 + 240 + 4 + 34 + 4 + 34 + 4 =
        /// 324 × 40 — so a token change moves the declaration with it instead of silently diverging
        /// (which is exactly what the old pair of hand-copied constants could do).
        /// </summary>
        private static readonly Size TraySize = new Size(
            TrayTokens.TrayPad * 2 + StatusWidth + TrayTokens.Gap * 2 + TrayTokens.ButtonMinWidth * 2,
            TrayTokens.ButtonHeight + TrayTokens.TrayPad * 2);

        private readonly TrayStatusBlock _status;

        /// <summary>Stop the run and keep everything captured so far.</summary>
        public event EventHandler FinishClicked;

        /// <summary>Throw the whole run away.</summary>
        public event EventHandler CancelClicked;

        public ScrollStatusStrip()
            : base(new FloatingTrayOptions
            {
                Title = "Clowd Scrolling Capture",
                HasGrip = false,
                FixedTraySize = TraySize,
            })
        {
            // the readout covers the window between Show() and the first status line from the driver,
            // and names the two ways to stop because the strip has no other surface for the hint.
            _status = new TrayStatusBlock
            {
                Width = StatusWidth,
                PrimaryText = "Starting…",
                SecondaryText = "Esc or Finish to stop",
            };
            Tray.Items.Add(_status);

            // Finish carries a resting red veil (spec §6 finish.stop): on this strip it is the only
            // button that keeps the capture, and it has to be findable without hovering first.
            var finish = new TrayButton
            {
                Glyph = TrayGlyphs.Stop,
                Look = TrayButtonLook.DangerFilled,
            };
            ToolTip.SetTip(finish, "Stop scrolling and keep what has been captured (Esc does the same)");
            AutomationProperties.SetName(finish, "Stop scrolling and keep what has been captured");
            finish.Click += (s, e) => FinishClicked?.Invoke(this, EventArgs.Empty);
            Tray.Items.Add(finish);

            var cancel = new TrayButton
            {
                Glyph = TrayGlyphs.X,
                Look = TrayButtonLook.Quiet,
            };
            ToolTip.SetTip(cancel, "Discard the scrolling capture");
            AutomationProperties.SetName(cancel, "Discard the scrolling capture");
            cancel.Click += (s, e) => CancelClicked?.Invoke(this, EventArgs.Empty);
            Tray.Items.Add(cancel);
        }

        /// <summary>Sets the two-line readout: what has been captured, and what is happening
        /// (or how to stop it). Either line may be null to blank it.</summary>
        public void SetStatus(string primary, string secondary)
        {
            _status.PrimaryText = primary ?? String.Empty;
            _status.SecondaryText = secondary ?? String.Empty;
        }
    }
}
