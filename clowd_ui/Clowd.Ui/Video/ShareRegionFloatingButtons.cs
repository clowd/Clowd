using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clowd.UI.Controls.Tray;

namespace Clowd.UI
{
    /// <summary>
    /// The floating strip of a share-region session: grip · Hide/Show · Resize · Options · Stop.
    /// It knows nothing about the driver, the obscure command bookkeeping or the resize state
    /// machine — those live in <see cref="ShareRegionPage"/>, which pushes the authoritative state in
    /// through <see cref="SetHidden"/>, <see cref="SetResizeState"/> and <see cref="RetireHide"/> and
    /// hears the user through the four events. The strip reads and writes no settings and enumerates
    /// no devices: a separate type from the recording strip makes that a compile-time fact rather than
    /// a runtime check at the top of six methods.
    /// </summary>
    public sealed class ShareRegionFloatingButtons : FloatingTrayWindow
    {
        private readonly TraySplitToggle _hide;
        private readonly TrayButton _resize;
        private readonly TrayButton _options;
        private readonly TrayButton _stop;

        // whether the helper is currently obscuring the mirrored region, and whether it can at all.
        // _obscureAvailable only ever goes false — the helper's GPU effect failure is permanent for
        // the life of that process — so the tile is retired, not toggled off (RetireHide).
        private bool _hidden;                    // the helper is obscuring the region
        private bool _obscureAvailable = true;   // only ever goes false: the GPU failure is permanent

        // the resize mode's two flags, both owned by ShareRegionPage and pushed in through
        // SetResizeState. They are separate because they end at different moments — the overlay
        // comes down (_resizeActive false) as soon as the user commits, but the move is still in
        // flight (_resizeBusy true) until the helper acks the new rectangle or the page's backstop
        // timer fires, and re-entering the mode during that window would put a second move on the
        // wire. UpdateLocks is where both turn into IsEnabled.
        private bool _resizeActive;              // resize mode is on
        private bool _resizeBusy;                // a move is in flight; nothing may re-enter

        /// <summary>OPTIMISTIC: the tile has already repainted itself and reports the state it flipped TO.</summary>
        public event EventHandler<bool> HideToggled;

        /// <summary>NOT optimistic: reports the state being ASKED for; nothing local changes until the
        /// page answers with <see cref="SetResizeState"/>.</summary>
        public event EventHandler<bool> ResizeToggled;

        /// <summary>Stop sharing. The event keeps its name from the old strip, where this tile never
        /// became Finish on a share.</summary>
        public event EventHandler CancelClicked;

        /// <summary>Options: the page opens the Shared Region settings tab.</summary>
        public event EventHandler SettingsClicked;

        public ShareRegionFloatingButtons()
            : base(new FloatingTrayOptions { Title = "Clowd Share Toolbar", PreferAboveBeforeVertical = true })
        {
            // the grip's tooltip is where this session says how it is doing: the strip has no label,
            // and the helper's frame rate is the only evidence the user has that the mirror is live.
            ToolTip.SetTip(Grip, "Sharing · Drag to move");

            // Hide/Show. The polarity is the source-toggle one — ON (open eye, green bar) means the
            // meeting CAN see the region, the reading that works from across a room — so hiding
            // dims the tile and turns the bar red. The bar is a state light, never a meter. No
            // chevron: there is nothing to pick. The tooltip names what a press will DO, and
            // SetHidden is the only writer of both.
            _hide = new TraySplitToggle
            {
                OnGlyph = TrayGlyphs.Eye,
                OffGlyph = TrayGlyphs.EyeOff,
                IsStatusOnly = true,
                HasChevron = false,
            };
            _hide.ToggleClicked += HideClicked;

            // Resize enters the move/resize mode: a hit-testable overlay appears over the region
            // and the next press of this tile commits the new rectangle. Not a split toggle —
            // nothing is flowing or muted here, so a red/green bar would be a lie; the accent fill
            // the page latches via SetResizeState is the "mode is on" signal instead. It
            // deliberately does not flip itself on click: the page owns the transition and may
            // refuse it.
            _resize = new TrayButton { Glyph = TrayGlyphs.Resize };
            _resize.Click += ResizeClicked;

            // Options stays: it is the same tile the recording strip carries and does the same job,
            // only pointed at the Shared Region settings page instead of the Recording one (the page
            // picks the tab; this window just raises SettingsClicked). Worth having here for the
            // reason the recording strip has it — the settings that matter to a live share, the
            // obscure style and strength, are exactly the ones you want to reach WHILE looking at
            // the thing they change.
            _options = new TrayButton { Glyph = TrayGlyphs.Sliders };
            ToolTip.SetTip(_options, "Shared region settings");
            AutomationProperties.SetName(_options, "Shared region settings");
            _options.Click += (s, e) => SettingsClicked?.Invoke(this, EventArgs.Empty);

            // Stop is fixed on this strip: a share has nothing to save, so the trailing tile never
            // morphs the way the recording strip's Cancel becomes Finish.
            _stop = new TrayButton { Glyph = TrayGlyphs.X, Look = TrayButtonLook.Quiet };
            ToolTip.SetTip(_stop, "Stop sharing");
            AutomationProperties.SetName(_stop, "Stop sharing");
            _stop.Click += (s, e) => CancelClicked?.Invoke(this, EventArgs.Empty);

            Tray.Items.Add(_hide);
            Tray.Items.Add(_resize);
            Tray.Items.Add(_options);
            Tray.Items.Add(_stop);

            // Seeded HERE, in the constructor, so the tile's glyph, bar and tooltip can never be set
            // from anywhere but the one funnel. Without this the tile would inherit the control's
            // IsOn=false default, which under this polarity is the dimmed crossed-eye and a RED bar:
            // the strip would open claiming the region is hidden while the helper is mirroring it in
            // the clear, which is the one lie this tile must never tell. A session starts un-hidden,
            // and _hidden says so; passing the field rather than a literal false keeps the two from
            // ever drifting apart.
            SetHidden(_hidden);

            // the resize tile's tooltip and both IsEnabled flags come from their single writers too.
            SetResizeState(_resizeActive, _resizeBusy);
        }

        /// <summary>
        /// Pushes the obscure state onto the Hide tile without raising <see cref="HideToggled"/> —
        /// the authoritative direction, for the helper's acks. Two of those matter: the ack for a
        /// toggle the user just made (a no-op repaint, and cheap insurance that the strip agrees
        /// with the process actually drawing the frames), and the UNSOLICITED
        /// <c>obscure/none</c> the helper emits when its GPU effect fails to build — a retraction
        /// of an obscure the user asked for and can currently see is on. Pair that one with
        /// <see cref="RetireHide"/>: the failure is permanent for that process.
        /// This is the SINGLE funnel for the tile's glyph, bar and tooltip: the initial seed in the
        /// constructor, the click path, the ack path and the retirement path all route through it,
        /// so no other line anywhere assigns any of them.
        /// The two say different things on purpose. The lit glyph and the green bar mean the
        /// meeting CAN see the screen, which is the same reading the recording strip's mic and
        /// system tiles carry and the only one that works from across a room. The TOOLTIP (and
        /// accessible name) names what a press will do — "Hide the shared region" while visible,
        /// "Show the shared region" while hidden.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            _hidden = hidden;
            _hide.IsOn = !hidden;

            var tip = ShareStripRules.HideToolTip(hidden);
            _hide.ToggleToolTip = tip;
            AutomationProperties.SetName(_hide, tip);
        }

        /// <summary>
        /// Retires the Hide tile permanently. Called when the helper says its obscure effect failed
        /// to build, which it never retries — so this is a retirement rather than a temporary lock,
        /// and the tile stays visible-but-dead on purpose: a button that vanishes mid-session reads
        /// as a bug, and the strip re-laying itself out around the gap would move Stop under the
        /// pointer. One-way: there is no call that re-offers the tile, because nothing can make the
        /// effect come back. Repeatable — a second call re-asserts the same state.
        /// The only notice is a blip beside the strip. Never a dialog: this can land in the middle
        /// of a live meeting the user is presenting to, where a modal is both a screen-sharing
        /// embarrassment and a thing they cannot dismiss without losing their place.
        /// Goes through <see cref="UpdateLocks"/> rather than writing IsEnabled directly, so
        /// leaving resize mode can never silently re-enable a tile that was retired while it ran.
        /// </summary>
        public void RetireHide()
        {
            _obscureAvailable = false;
            UpdateLocks();

            // whatever the user last asked for, nothing is being obscured now — say so, so the
            // dead tile is not left lit over a region that is being mirrored in the clear.
            SetHidden(false);
            ShowStatusBlip("Hide unavailable");
        }

        /// <summary>
        /// Authoritative resize-tile state, owned by the page.
        /// <paramref name="active"/> latches the accent fill and locks Hide — the region is already
        /// being obscured for the duration of the mode, so a Hide press would be commanding the one
        /// thing that is not the user's to command right now.
        /// <paramref name="busy"/> means a move is in flight: Resize is un-pressable and Hide stays
        /// locked even though the overlay is already gone, because the page has not yet heard which
        /// rectangle the helper actually applied.
        /// Touches neither <c>_obscureAvailable</c> nor <c>_hidden</c>, so the Hide tile comes back
        /// exactly as it was — including still retired, if it was retired mid-mode. Safe before any
        /// layout or ack, and accepts the corrective <c>(false, false)</c> for a refused request.
        /// </summary>
        public void SetResizeState(bool active, bool busy)
        {
            _resizeActive = active;
            _resizeBusy = busy;

            // the accent fill is this tile's whole "the mode is on" signal: it is not a split
            // toggle, so there is no bar and no dimmed-off glyph to carry it.
            _resize.IsActive = active;

            var tip = ShareStripRules.ResizeToolTip(active);
            ToolTip.SetTip(_resize, tip);
            AutomationProperties.SetName(_resize, tip);

            UpdateLocks();
        }

        /// <summary>Once-a-second frame rate report from the helper, shown in the grip's tooltip —
        /// the only evidence the user has that the mirror is still live.</summary>
        public void SetFps(double fps)
        {
            ToolTip.SetTip(Grip, $"Sharing at {fps:F0} FPS · Drag to move");
        }

        /// <summary>The one place the share strip's enable rules are applied. Both owners — the
        /// permanent obscure retirement and the transient resize mode — are ANDed in
        /// <see cref="ShareStripRules"/> rather than each writing IsEnabled, so neither can clobber
        /// the other: a resize that ends after the helper retired the effect must not hand the user
        /// back a Hide tile that cannot work. Nothing else writes either tile's IsEnabled.</summary>
        private void UpdateLocks()
        {
            _hide.IsEnabled = ShareStripRules.HideEnabled(_obscureAvailable, _resizeActive, _resizeBusy);
            _resize.IsEnabled = ShareStripRules.ResizeEnabled(_resizeBusy);
        }

        private void HideClicked(object sender, EventArgs e)
        {
            // IsEnabled already blocks all three of these; the checks are here because the
            // consequence of getting one wrong is an obscure command to a helper that has told us
            // it cannot honor one (leaving the strip lit over a clear region), or a command racing
            // the obscure state the resize mode is holding on the region's behalf.
            if (!ShareStripRules.HideEnabled(_obscureAvailable, _resizeActive, _resizeBusy))
                return;

            SetHidden(!_hidden);
            HideToggled?.Invoke(this, _hidden);
        }

        /// <summary>
        /// Asks the page to enter or leave resize mode. Deliberately does NOT flip any local state:
        /// entering the mode means hiding the region, showing an overlay and taking over the
        /// obscure state, all of which the page can legitimately refuse (it is closing, the share
        /// never started, the helper died), and a tile that had already latched itself accent would
        /// then be permanently out of step with a mode that never began. The page answers with
        /// <see cref="SetResizeState"/>, which is the only writer of the tile's appearance.
        /// </summary>
        private void ResizeClicked(object sender, RoutedEventArgs e)
        {
            if (!ShareStripRules.ResizeEnabled(_resizeBusy))
                return;

            ResizeToggled?.Invoke(this, !_resizeActive);
        }
    }
}
