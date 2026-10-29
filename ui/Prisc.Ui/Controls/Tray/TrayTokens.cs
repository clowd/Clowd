using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Prisc.UI.Controls.Tray
{
    /// <summary>
    /// The single source of truth for the floating tray's look: the graphite palette, the geometry
    /// and the motion durations from the design spec. Every value here is theme-invariant on purpose
    /// — these strips float over an arbitrary desktop, never over the app, so the app's light/dark
    /// variant is irrelevant to them.
    /// <para>
    /// Tints are NOT pre-blended literals. Each veil is a white/red/amber brush at a fixed alpha that
    /// is layered over whatever sits beneath it, so changing <see cref="Tray"/> or <see cref="Seg"/>
    /// keeps every hover and every state fill arithmetically correct without touching this file.
    /// </para>
    /// <para>
    /// XAML reaches every member with <c>{x:Static tray:TrayTokens.X}</c>. The one value that cannot
    /// live here is the user's accent colour: it is a runtime setting, so the tray window writes it
    /// into its own resources under <see cref="AccentBrushKey"/> and the themes use a DynamicResource.
    /// </para>
    /// <para>
    /// The tokens the capture overlay's egui panel shares (the fills, the ring, radius, padding, the
    /// button and icon sizes, the veils, the tooltip, the fade and the shadow) are generated from
    /// <c>prisc_rust_core::contract::tray</c> into the other part of this class (TrayTokens.g.cs);
    /// this part turns them into Avalonia colours, brushes and shadows.
    /// </para>
    /// </summary>
    public static partial class TrayTokens
    {
        // colours (spec §0) — fixed, theme-invariant, never derived from the accent
        public static readonly Color Tray = Opaque(TrayFill),
                                     Seg = Opaque(SegFill),
                                     Fg = Colors.White,
                                     Rec = Color.Parse("#E2504A"),
                                     Ok = Color.Parse("#2FA85A"),
                                     Warn = Color.Parse("#D8901B");

        public static readonly IBrush TrayBrush = new ImmutableSolidColorBrush(Tray),
                                      SegBrush = new ImmutableSolidColorBrush(Seg),
                                      FgBrush = new ImmutableSolidColorBrush(Fg),
                                      RecBrush = new ImmutableSolidColorBrush(Rec),
                                      OkBrush = new ImmutableSolidColorBrush(Ok),
                                      WarnBrush = new ImmutableSolidColorBrush(Warn);

        // veils = alpha overlays over whatever is beneath (spec §0 "implement as layered overlays")
        /// <summary>White .12 — hover veil on Normal/Quiet buttons. Was .08 over the original #2F3237
        /// segment; the lighter segment needed a bigger step to keep the hover readable.</summary>
        public static readonly IBrush HoverVeil = new ImmutableSolidColorBrush(Fg, HoverVeilAlpha);

        /// <summary>White .12 — the underline-bar track behind the level pill.</summary>
        public static readonly IBrush Track = new ImmutableSolidColorBrush(Fg, 0.12);

        /// <summary>White .08 — the hairline between a toggle and its chevron.</summary>
        public static readonly IBrush Divider = new ImmutableSolidColorBrush(Fg, 0.08);

        /// <summary>#3B3D40 — the 1 px highlight ring drawn inside the tray edge: <see cref="Tray"/> plus 10 %
        /// white, baked opaque so the ring never shows the desktop through it. (Was white .06, then .10; both
        /// were veils and read faint.)</summary>
        public static readonly IBrush Ring = new ImmutableSolidColorBrush(Opaque(RingAvalonia));

        // Active primary rest / Idle hover / rotate hover / Active hover
        public static readonly IBrush WhiteVeil10 = new ImmutableSolidColorBrush(Fg, 0.10),
                                      WhiteVeil12 = new ImmutableSolidColorBrush(Fg, HoverVeilAlpha),
                                      WhiteVeil14 = new ImmutableSolidColorBrush(Fg, 0.14),
                                      WhiteVeil16 = new ImmutableSolidColorBrush(Fg, 0.16);

        /// <summary>White .18 — the hover veil on a bare (fill-less) mini button. A step heavier than
        /// <see cref="HoverVeil"/> because it lands on the darker tray rather than on a segment.</summary>
        public static readonly IBrush WhiteVeil18 = new ImmutableSolidColorBrush(Fg, HoverVeilBareAlpha);

        /// <summary>White .35 — the 1 px inset edge of a selected dot button, over its
        /// <see cref="WhiteVeil16"/> fill.</summary>
        public static readonly IBrush WhiteVeil35 = new ImmutableSolidColorBrush(Fg, 0.35);

        /// <summary>White .12 — the free-standing 1 px rule between two groups on a strip
        /// (<see cref="TrayDivider"/>). Not <see cref="Divider"/>: that hairline sits inside a split slot,
        /// over the segment fill, and is deliberately fainter.</summary>
        public static readonly IBrush GroupDivider = new ImmutableSolidColorBrush(Fg, 0.12);

        /// <summary>White .18 — the 1 px hairline inside every colour dot (<see cref="TraySwatch"/>), so
        /// a near-black colour still has an edge against the graphite tray.</summary>
        public static readonly IBrush DotHairline = new ImmutableSolidColorBrush(Fg, 0.18);

        // DangerFilled rest / Danger hover
        public static readonly IBrush RecVeil22 = new ImmutableSolidColorBrush(Rec, 0.22),
                                      RecVeil28 = new ImmutableSolidColorBrush(Rec, 0.28);

        // Paused rest / Paused hover
        public static readonly IBrush WarnVeil22 = new ImmutableSolidColorBrush(Warn, 0.22),
                                      WarnVeil34 = new ImmutableSolidColorBrush(Warn, 0.34);

        /// <summary>#F2141619 — tooltip and status-blip fill.</summary>
        public static readonly IBrush TipFill = new ImmutableSolidColorBrush(WithAlpha(TipFillRgb, (byte)TipFillAlpha));

        /// <summary>#F7202226 — device-menu fill. Deliberately opaque enough to need no blur.</summary>
        public static readonly IBrush MenuFill = new ImmutableSolidColorBrush(Color.Parse("#F7202226"));

        public static readonly IBrush MenuBorder = new ImmutableSolidColorBrush(Fg, 0.12);
        public static readonly IBrush MenuItemHover = new ImmutableSolidColorBrush(Fg, 0.10);

        /// <summary>
        /// Resource key the tray window writes the effective capture accent into. The themes bind it
        /// with a DynamicResource so the accent can be a per-window value without a static brush.
        /// </summary>
        public const string AccentBrushKey = "TrayAccentBrush";

        // geometry, logical px (spec §0–§9); ButtonHeight and ButtonMinWidth are generated

        /// <summary>
        /// The one corner radius for the buttons AND the tray around them (the user asked for the two to
        /// match rather than for the outer one to be inner + padding). A <see cref="CornerRadius"/> has no
        /// double conversion in XAML, so the shapes themselves are tokens: the full one, and the four
        /// "rounded on one side only" variants a split toggle's halves use — named for the side that KEEPS
        /// its rounding. The radius itself (Radius) is generated.
        /// </summary>
        public static readonly CornerRadius Corner = new CornerRadius(Radius),
                                            CornerLeft = new CornerRadius(Radius, 0, 0, Radius),
                                            CornerRight = new CornerRadius(0, Radius, Radius, 0),
                                            CornerTop = new CornerRadius(Radius, Radius, 0, 0),
                                            CornerBottom = new CornerRadius(0, 0, Radius, Radius);

        /// <summary>The hover veil on the grip's two cells (dot handle, rotate button): smaller than the
        /// button radius because a cell is half a button tall.</summary>
        public static readonly CornerRadius GripCorner = new CornerRadius(5);

        /// <summary>
        /// <see cref="TrayPad"/> as the tray's presenter margin, the 1 px ring included: the spec's 4 px
        /// tray padding (TrayPad, generated, as is Gap) is spent as a 1 px ring plus 3 px of inner
        /// padding, so the outer box is exactly <see cref="ButtonHeight"/> + 8 (48 px) tall. The one
        /// <see cref="Thickness"/>-shaped token here, because it has both a XAML consumer (the tray
        /// template's <c>ItemsPresenter</c>) and a C# one that derives a fixed tray size from
        /// <see cref="TrayPad"/> arithmetically — so the declared size and the real margin cannot drift
        /// apart. Thickness tokens that would have had no consumer are deliberately absent.
        /// </summary>
        public static readonly Thickness TrayPadding = new Thickness(TrayPad);

        public const double ChevronWidth = 16, ChevronHeight = 14, PrimaryWidth = 66;

        /// <summary>
        /// The grip's extent along the strip axis: its width in a row, its height in a column. The other
        /// axis is the row height / column width, split into two equal cells — the dot handle and the
        /// rotate button — so the two read as a matched pair rather than as a grid with a small button
        /// tucked under it.
        /// </summary>
        public const double GripLength = 26, RotateGlyphSize = 14;


        /// <summary>
        /// The compact tile: a 26 px square with a 15 px shape-fitted icon, for a second, lighter lane of
        /// settings and housekeeping that runs at <see cref="SecondaryLaneExtent"/> rather than at the
        /// button height. <see cref="RingThickness"/> is the inset accent ring of a "ringed" button.
        /// </summary>
        public const double MiniButtonSize = 26, MiniGlyphSize = 15, RingThickness = 1.5;

        /// <summary><see cref="RingThickness"/> as a <see cref="Thickness"/>: a setter cannot convert an
        /// x:Static double, so the "ringed" style binds this one.</summary>
        public static readonly Thickness RingBorder = new Thickness(RingThickness);

        /// <summary>
        /// A colour swatch (<see cref="TraySwatch"/>): a 16 px dot, then — when selected or hovered — a
        /// 2 px gap showing the tray, then a 1.5 px ring. 16 + 2·(2 + 1.5) = 23, inside the 26 px tile.
        /// </summary>
        public const double SwatchDot = 16, SwatchGap = 2, SwatchRing = 1.5;

        /// <summary>The two lanes of a <see cref="TrayDeck"/> across the strip axis: the button row and
        /// the compact row under it (or the two columns, rotated).</summary>
        public const double PrimaryLaneExtent = ButtonHeight, SecondaryLaneExtent = MiniButtonSize;

        /// <summary>A <see cref="TrayDivider"/>'s length across the strip axis.</summary>
        public const double DividerLength = 24;
        public const double TrackWidth = 24, TrackHeight = 4, LevelMinWidth = 8;

        /// <summary>
        /// A split toggle's toggle half along the strip axis: 8 px inset, the 20 px icon, 5 px of air,
        /// the 4 px meter standing beside it, 5 px inset. The meter sits on the icon's trailing side
        /// (right in a row, below in a column), so the half is this long in a row and this tall in a
        /// column while the other dimension stays the button size.
        /// </summary>
        public const double ToggleLength = 42, ToggleInset = 8;
        public const double MenuGap = 8, DragThreshold = 5;

        /// <summary>
        /// The tooltip's clearance from the strip, carried as a transparent margin INSIDE the popup rather
        /// than as a placement offset. A placement offset is added in the same direction after the
        /// positioner flips a tip that does not fit below the strip to above it, so the "gap" then pushed
        /// the tip 7 px INTO the strip. A margin is symmetric: the tip sits 7 px below when it fits and 7 px
        /// above when it does not, and 8 px beside a column either way.
        /// </summary>
        public static readonly Thickness TipMargin = new Thickness(ToolTipGapRight, ToolTipGapBottom);

        // motion
        public static readonly TimeSpan FillTransition = TimeSpan.FromMilliseconds(FillTransitionMs),
                                        PressTransition = TimeSpan.FromMilliseconds(120),
                                        LevelTransition = TimeSpan.FromMilliseconds(80),
                                        Pulse = TimeSpan.FromMilliseconds(1400),
                                        BlipDuration = TimeSpan.FromSeconds(6);

        // the accelerator key hint (TrayKeyHint): a semibold letter tucked into a tile's bottom-right
        // corner that flies in while the strip's hotkeys are live (issue #110, mockup C). 9 px is the
        // mockup's size (tried up to 14 on screen: 10 and up crowd the tile); a hinted tile draws its
        // glyph a size down (KeyHintGlyphSize) to make room, and the hint's dark halo separates the two
        // where they meet.

        /// <summary>The glyph size of a tile that carries a key hint, down from <see cref="IconSize"/>.</summary>
        public const double KeyHintGlyphSize = 17;

        /// <summary>The hint's inset from the tile's bottom-right corner: clear of both edges, so it reads
        /// as inside the tile rather than pressed into its corner.</summary>
        public static readonly Thickness KeyHintMargin = new Thickness(0, 0, 5, 4);

        /// <summary>How far below its resting place a hidden hint sits: its inset, its line and its halo,
        /// so it is wholly past the tile's bottom edge (the template clips it there).</summary>
        public const double KeyHintTravel = 20;

        /// <summary>White .92: as legible as the glyph, a hair behind it.</summary>
        public static readonly IBrush KeyHintBrush = new ImmutableSolidColorBrush(Fg, 0.92);

        /// <summary>The fly-in, the fly-away, and the step between neighbouring tiles' starts, so the
        /// hints ripple along the strip rather than landing all at once.</summary>
        public static readonly TimeSpan KeyHintIn = TimeSpan.FromMilliseconds(340),
                                        KeyHintOut = TimeSpan.FromMilliseconds(180),
                                        KeyHintStagger = TimeSpan.FromMilliseconds(28);

        /// <summary>
        /// The spec's shadow. Not the default: every transparent pixel of the window rect eats clicks
        /// (WM_NCHITTEST/HTTRANSPARENT only forwards within one thread, so no hook can pass them to the
        /// recorded app), and a 34 px blur means a 46 px dead band under the strip. Measured
        /// unshippable as-is — see <see cref="ShadowCompact"/>.
        /// </summary>
        public static readonly BoxShadows Shadow = BoxShadows.Parse("0 12 34 0 #66000000");

        /// <summary>
        /// The shipped shadow: small enough that its dead band is a fringe, not a band.
        /// <para>
        /// Measured 2026-09-19 on one 3440×1440 monitor at 100 % (DPI 96), WindowFromPoint +
        /// GetForegroundWindow: with <see cref="Shadow"/> (spec, 0 12 34 0 #66000000; window 786×108
        /// around a 718×40 tray) a click 5 px and 20 px below the tray box is eaten and only +60 px
        /// reaches the app — the whole 46 px bottom reserve is dead. With <c>ShadowCompact</c>
        /// (0 3 10 0 #59000000; window 738×60) clicks at +3 and +8 px are eaten and +20 px reaches the
        /// app: a ~13 px band, bottom only. Spec shadow is unshippable as-is; compact ships.
        /// </para>
        /// </summary>
        public static readonly BoxShadows ShadowCompact = new(new BoxShadow
        {
            OffsetY = ShadowOffsetY,
            Blur = ShadowBlur,
            Color = new Color((byte)ShadowAlpha, 0, 0, 0),
        });

        // the engaged frame (FloatingTray.IsEngaged): an accent spine over the grip edge, an accent ring,
        // and an accent glow in place of the drop shadow

        /// <summary>The resource keys of the engaged frame's glow and of the glow burst, written by the
        /// window from the accent (<see cref="EngagedGlow"/>, <see cref="BurstGlow"/>) next to
        /// <see cref="AccentBrushKey"/>.</summary>
        public const string EngagedGlowKey = "TrayEngagedGlow", BurstGlowKey = "TrayBurstGlow";

        /// <summary>How deep the spine runs in from the grip edge: the tray's padding, the grip, and half
        /// the gap after it, so its inner edge falls midway between the grip and the first tile. The grip
        /// is GripLength along the strip axis in every layout and orientation.</summary>
        public const double SpineLength = TrayPad + GripLength + Gap / 2;

        /// <summary>The accent ring: half a pixel heavier than the 1 px graphite ring it covers.</summary>
        public const double EngagedRing = 1.5;

        /// <summary>Where the engaged frame's graphite panel sits over the accent: the ring's width in from
        /// three edges and the spine's length in from the grip edge — the left of a row, the top of a column.</summary>
        public static readonly Thickness EngagedInsetRow = new Thickness(SpineLength, EngagedRing, EngagedRing, EngagedRing),
                                         EngagedInsetColumn = new Thickness(EngagedRing, SpineLength, EngagedRing, EngagedRing);

        /// <summary>The panel's corners: concentric with the tray's on the outer edges, and the same where it
        /// meets the spine, so the graphite reads as one rounded shape set into the accent.</summary>
        public static readonly CornerRadius EngagedPanelCorner = new CornerRadius(Radius - EngagedRing);

        /// <summary>
        /// The room the glow needs outside the tray on every side, in logical px, at its widest (the burst
        /// on top of the resting glow). The window reserves it (<see cref="FloatingTrayOptions.EngagedFrame"/>),
        /// so the glow's falloff never meets the window edge.
        /// </summary>
        public const double GlowReserve = 16;

        /// <summary>The engaged glow: a soft accent halo plus a crisp accent hairline just outside the edge.</summary>
        public static BoxShadows EngagedGlow(Color accent) => Glow(accent, spread: 1, alpha: 0.5, hairline: 1, hairlineAlpha: 0.4);

        /// <summary>The glow burst, drawn over whatever glow there is: the halo swells in spread and
        /// strength rather than in blur, which is what <see cref="GlowReserve"/> has room for.</summary>
        public static BoxShadows BurstGlow(Color accent) => Glow(accent, spread: 4, alpha: 0.8, hairline: 3, hairlineAlpha: 0.65);

        /// <summary>The blur of every glow's halo.</summary>
        public const double GlowBlur = 12;

        private static BoxShadows Glow(Color accent, double spread, double alpha, double hairline, double hairlineAlpha) => new(
            new BoxShadow { Blur = GlowBlur, Spread = spread, Color = WithAlpha(accent, alpha) },
            new[] { new BoxShadow { Spread = hairline, Color = WithAlpha(accent, hairlineAlpha) } });

        private static Color WithAlpha(Color c, double alpha) => new Color((byte)Math.Round(255 * alpha), c.R, c.G, c.B);

        /// <summary>A generated <c>0x00RRGGBB</c> token at <paramref name="alpha"/>.</summary>
        private static Color WithAlpha(uint rgb, byte alpha) => new Color(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static Color Opaque(uint rgb) => WithAlpha(rgb, 0xFF);
    }
}
