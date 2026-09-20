using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Clowd.UI.Controls.Tray
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
    /// </summary>
    public static class TrayTokens
    {
        // colours (spec §0) — fixed, theme-invariant, never derived from the accent
        public static readonly Color Tray = Color.Parse("#25272B"),
                                     Seg = Color.Parse("#3A3E44"),
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
        public static readonly IBrush HoverVeil = new ImmutableSolidColorBrush(Fg, 0.12);

        /// <summary>White .12 — the underline-bar track behind the level pill.</summary>
        public static readonly IBrush Track = new ImmutableSolidColorBrush(Fg, 0.12);

        /// <summary>White .08 — the hairline between a toggle and its chevron.</summary>
        public static readonly IBrush Divider = new ImmutableSolidColorBrush(Fg, 0.08);

        /// <summary>White .10 — the 1 px highlight ring drawn inside the tray edge (was .06; too faint to read as an edge).</summary>
        public static readonly IBrush Ring = new ImmutableSolidColorBrush(Fg, 0.10);

        // Active primary rest / Idle hover / rotate hover / Active hover
        public static readonly IBrush WhiteVeil10 = new ImmutableSolidColorBrush(Fg, 0.10),
                                      WhiteVeil12 = new ImmutableSolidColorBrush(Fg, 0.12),
                                      WhiteVeil14 = new ImmutableSolidColorBrush(Fg, 0.14),
                                      WhiteVeil16 = new ImmutableSolidColorBrush(Fg, 0.16);

        // DangerFilled rest / Danger hover
        public static readonly IBrush RecVeil22 = new ImmutableSolidColorBrush(Rec, 0.22),
                                      RecVeil28 = new ImmutableSolidColorBrush(Rec, 0.28);

        // Paused rest / Paused hover
        public static readonly IBrush WarnVeil22 = new ImmutableSolidColorBrush(Warn, 0.22),
                                      WarnVeil34 = new ImmutableSolidColorBrush(Warn, 0.34);

        /// <summary>#F2141619 — tooltip and status-blip fill.</summary>
        public static readonly IBrush TipFill = new ImmutableSolidColorBrush(Color.Parse("#F2141619"));

        /// <summary>#F7202226 — device-menu fill. Deliberately opaque enough to need no blur.</summary>
        public static readonly IBrush MenuFill = new ImmutableSolidColorBrush(Color.Parse("#F7202226"));

        public static readonly IBrush MenuBorder = new ImmutableSolidColorBrush(Fg, 0.12);
        public static readonly IBrush MenuItemHover = new ImmutableSolidColorBrush(Fg, 0.10);

        /// <summary>
        /// Resource key the tray window writes the effective capture accent into. The themes bind it
        /// with a DynamicResource so the accent can be a per-window value without a static brush.
        /// </summary>
        public const string AccentBrushKey = "TrayAccentBrush";

        // geometry, logical px (spec §0–§9)
        public const double ButtonHeight = 40, ButtonMinWidth = 40;

        /// <summary>
        /// The one corner radius for the buttons AND the tray around them (the user asked for the two to
        /// match rather than for the outer one to be inner + padding). A <see cref="CornerRadius"/> has no
        /// double conversion in XAML, so the shapes themselves are tokens: the full one, and the four
        /// "rounded on one side only" variants a split toggle's halves use — named for the side that KEEPS
        /// its rounding.
        /// </summary>
        public const double Radius = 8;

        public static readonly CornerRadius Corner = new CornerRadius(Radius),
                                            CornerLeft = new CornerRadius(Radius, 0, 0, Radius),
                                            CornerRight = new CornerRadius(0, Radius, Radius, 0),
                                            CornerTop = new CornerRadius(Radius, Radius, 0, 0),
                                            CornerBottom = new CornerRadius(0, 0, Radius, Radius);

        /// <summary>The hover veil on the grip's two cells (dot handle, rotate button): smaller than the
        /// button radius because a cell is half a button tall.</summary>
        public static readonly CornerRadius GripCorner = new CornerRadius(5);

        /// <summary>
        /// The spec's 4 px tray padding, spent as a 1 px ring plus 3 px of inner padding so the outer
        /// box is exactly <see cref="ButtonHeight"/> + 8 (48 px) tall.
        /// </summary>
        public const double TrayPad = 4, Gap = 4;

        /// <summary>
        /// <see cref="TrayPad"/> as the tray's presenter margin, the 1 px ring included. The one
        /// <see cref="Thickness"/>-shaped token here, because it has both a XAML consumer (the tray
        /// template's <c>ItemsPresenter</c>) and a C# one that derives a fixed tray size from
        /// <see cref="TrayPad"/> arithmetically — so the declared size and the real margin cannot drift
        /// apart. Thickness tokens that would have had no consumer are deliberately absent.
        /// </summary>
        public static readonly Thickness TrayPadding = new Thickness(TrayPad);

        public const double IconSize = 20, ChevronWidth = 16, ChevronHeight = 14, PrimaryWidth = 66;

        /// <summary>
        /// The grip's extent along the strip axis: its width in a row, its height in a column. The other
        /// axis is the row height / column width, split into two equal cells — the dot handle and the
        /// rotate button — so the two read as a matched pair rather than as a grid with a small button
        /// tucked under it.
        /// </summary>
        public const double GripLength = 26, RotateGlyphSize = 14;

        /// <summary>The app mark's extent along the strip axis (<see cref="TrayEmblem"/>): a 32 px mark
        /// with 4 px either side — a button's footprint, drawn on the tray with no fill so it reads as
        /// part of the chassis like the grip, not as a button.</summary>
        public const double EmblemLength = 40;
        public const double TrackWidth = 24, TrackHeight = 4, LevelMinWidth = 8;

        /// <summary>
        /// A split toggle's toggle half along the strip axis: 8 px inset, the 20 px icon, 5 px of air,
        /// the 4 px meter standing beside it, 5 px inset. The meter sits on the icon's trailing side
        /// (right in a row, below in a column), so the half is this long in a row and this tall in a
        /// column while the other dimension stays the button size.
        /// </summary>
        public const double ToggleLength = 42, ToggleInset = 8;
        public const double MenuGap = 8, ToolTipGapBottom = 7, ToolTipGapRight = 8, DragThreshold = 5;

        /// <summary>
        /// The tooltip's clearance from the strip, carried as a transparent margin INSIDE the popup rather
        /// than as a placement offset. A placement offset is added in the same direction after the
        /// positioner flips a tip that does not fit below the strip to above it, so the "gap" then pushed
        /// the tip 7 px INTO the strip. A margin is symmetric: the tip sits 7 px below when it fits and 7 px
        /// above when it does not, and 8 px beside a column either way.
        /// </summary>
        public static readonly Thickness TipMargin = new Thickness(ToolTipGapRight, ToolTipGapBottom);
        public const int ToolTipShowDelayMs = 350;

        // motion
        public static readonly TimeSpan FillTransition = TimeSpan.FromMilliseconds(180),
                                        PressTransition = TimeSpan.FromMilliseconds(120),
                                        LevelTransition = TimeSpan.FromMilliseconds(80),
                                        Pulse = TimeSpan.FromMilliseconds(1400),
                                        BlipDuration = TimeSpan.FromSeconds(6);

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
        public static readonly BoxShadows ShadowCompact = BoxShadows.Parse("0 3 10 0 #59000000");
    }
}
