using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// What fills an <see cref="Item"/>'s span. Polymorphic on a <c>$type</c> discriminator
/// (<c>media</c> / <c>text</c> / <c>image</c> / <c>solid</c> / <c>speed</c> / <c>zoom</c> /
/// <c>cursor</c> / <c>keyboard</c> / <c>background</c> / <c>obscure</c> / <c>shape</c>) via System.Text.Json's built-in
/// polymorphism, which the source-generated <see cref="ProjectJsonContext"/> supports. The
/// discriminator strings are wire contract — renaming a class is free, renaming a discriminator
/// breaks every saved project.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MediaContent), "media")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(SolidContent), "solid")]
[JsonDerivedType(typeof(SpeedContent), "speed")]
[JsonDerivedType(typeof(ZoomContent), "zoom")]
[JsonDerivedType(typeof(CursorContent), "cursor")]
[JsonDerivedType(typeof(KeyboardContent), "keyboard")]
[JsonDerivedType(typeof(BackgroundContent), "background")]
[JsonDerivedType(typeof(ObscureContent), "obscure")]
[JsonDerivedType(typeof(ShapeContent), "shape")]
public abstract class ItemContent
{
    /// <summary>Deep copy, used by <see cref="TimelineOps.Split"/> so the two halves never share
    /// mutable state.</summary>
    public abstract ItemContent Clone();
}

/// <summary>One stream of a probed <see cref="Source"/>. Timeline time <c>t</c> inside the item
/// maps to source time <c>SourceInTicks + (t - item.TimelineStartTicks)</c>.</summary>
public sealed class MediaContent : ItemContent
{
    public Guid SourceId { get; set; }

    /// <summary>Stream index within the source container.</summary>
    public int StreamIndex { get; set; }

    /// <summary>Where in the source this item starts, in 100ns ticks. Trimming the start of the
    /// item moves this by the same amount (scaled by <see cref="Speed"/>).</summary>
    public long SourceInTicks { get; set; }

    /// <summary>Playback speed: how many source seconds one timeline second consumes (2 = twice
    /// as fast). With speed, timeline time <c>t</c> maps to source time
    /// <c>SourceInTicks + (t - item.TimelineStartTicks) * Speed</c>. Audio pitch rides with the
    /// speed (linear resample, no time stretching). 1 = realtime; only desynced (unlinked) items
    /// carry a non-unity speed — linked rows must keep the recording's own clock.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// When true the clip is mixed on the OUTPUT clock and ignores every speed effect item: it
    /// always plays in real time, however the project around it is sped up or slowed down. The
    /// voice-over recorder sets this on every take, because a narration recorded against the
    /// warped preview must stay locked to what the viewer hears, not to the project seconds it
    /// happens to span. Audio only (the composer ignores it on a picture row).
    ///
    /// <para>For output tick <c>o</c> an exempt clip is active while
    /// <c>ToOutput(TimelineStartTicks) &lt;= o &lt; ToOutput(TimelineEndTicks)</c> and reads
    /// source tick <c>SourceInTicks + (o - ToOutput(TimelineStartTicks)) * Speed</c>. Its source
    /// span is the truth and its project-time <see cref="Item.DurationTicks"/> is derived from it
    /// under the current warp; <c>EditorSession</c> re-fits it whenever the warp changes. With no
    /// speed item in the project the two clocks coincide and the flag changes nothing.</para>
    /// </summary>
    public bool SpeedWarpExempt { get; set; }

    /// <summary>
    /// When true the item is a freeze frame: every instant it covers shows the one source frame
    /// at <see cref="SourceInTicks"/> (the latest frame at or before it), so it consumes no
    /// source at all — <see cref="Speed"/> is ignored, a trim never moves the in-point and the
    /// item may be stretched to any length. Video rows only. Made by filling a timeline gap with
    /// a neighbor's edge frame (<see cref="TimelineOps.FillGapWithFreeze"/>).
    /// </summary>
    public bool Freeze { get; set; }

    public override ItemContent Clone() => new MediaContent
    {
        SourceId = SourceId,
        StreamIndex = StreamIndex,
        SourceInTicks = SourceInTicks,
        Speed = Speed,
        SpeedWarpExempt = SpeedWarpExempt,
        Freeze = Freeze,
    };
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>A text card / caption. Colors are <c>#AARRGGBB</c> strings.</summary>
public sealed class TextContent : ItemContent
{
    public string Text { get; set; }

    /// <summary>Font family name; null = the renderer's default.</summary>
    public string Font { get; set; }

    /// <summary>Font size in output-canvas pixels.</summary>
    public double Size { get; set; }

    public string Color { get; set; }

    public TextAlign Align { get; set; }

    public override ItemContent Clone() => new TextContent
    {
        Text = Text,
        Font = Font,
        Size = Size,
        Color = Color,
        Align = Align,
    };
}

/// <summary>A still image file.</summary>
public sealed class ImageContent : ItemContent
{
    public string Path { get; set; }

    public override ItemContent Clone() => new ImageContent { Path = Path };
}

/// <summary>A solid color fill (<c>#AARRGGBB</c>).</summary>
public sealed class SolidContent : ItemContent
{
    public string Color { get; set; }

    public override ItemContent Clone() => new SolidContent { Color = Color };
}

/// <summary>
/// A wallpaper drawn from the SDK's embedded library (see <c>BackgroundCatalog</c>): a
/// <see cref="Style"/> — one piece of artwork, or one family of them — and the <see cref="Theme"/>
/// it is drawn in. Picture content like an image, placed by the item's <see cref="Item.Transform"/>
/// exactly as a <see cref="SolidContent"/> is: its natural size is the canvas, so the default
/// transform fills the frame at every output aspect and the art cover-fits (<c>xMidYMid slice</c>)
/// inside that box. Three of the styles are animated loops whose phase is a pure function of the
/// project time the composer is asked for, so the preview and the export show the same frame at
/// the same instant.
/// </summary>
public sealed class BackgroundContent : ItemContent
{
    /// <summary>The style ids the editor offers, in menu order — the wire values stored in
    /// <see cref="Style"/>, so they are permanent. Big Sur first because it is the default; the
    /// three animated styles last so the picker groups them. Kept as a literal here rather than
    /// read off the catalog because the model never references the composition layer; a test
    /// asserts the two lists agree so they cannot drift. An unrecognized value draws the default
    /// style rather than nothing.</summary>
    public static readonly IReadOnlyList<string> Styles = new[]
    {
        "big-sur", "monterey", "gradient", "explode", "solid", "stacked-waves",
        "layered-steps", "moving-blob", "moving-corners", "breathing-field",
    };

    /// <summary>The range <see cref="AnimationSpeed"/> is validated to, and what the editor's
    /// spinner offers: a quarter to quadruple is as far as a wallpaper loop stays a wallpaper
    /// rather than a strobe or a still.</summary>
    public const double MinAnimationSpeed = 0.25;

    public const double MaxAnimationSpeed = 4.0;

    /// <summary>Prisc blue as <c>#AARRGGBB</c> — what a fresh <c>solid</c> background is filled
    /// with, and what an unparseable <see cref="Color"/> falls back to.</summary>
    public const string DefaultColor = "#FF00AFF0";

    /// <summary>One of <see cref="Styles"/>.</summary>
    public string Style { get; set; } = "big-sur";

    /// <summary>Which of the style's themes to draw (<c>BackgroundCatalog</c>), or null for its
    /// default — which is the only sensible value for a style that offers none (Explode), and is
    /// why no project written before a style gained themes needs migrating. A value the style does
    /// not offer falls back to its default rather than drawing nothing; the same semantics as
    /// <see cref="CursorContent.Variant"/>.</summary>
    public string Theme { get; set; }

    /// <summary>The fill color of the <c>solid</c> style, as <c>#AARRGGBB</c> (or
    /// <c>#RRGGBB</c>), the form every other model color takes. Ignored by every style that has
    /// artwork, and kept across a trip through one so a user who tries a wallpaper and comes back
    /// finds the color they picked. An unparseable value draws <see cref="DefaultColor"/> rather
    /// than nothing.</summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>Playback-rate multiplier on an animated style's loop, validated to
    /// <see cref="MinAnimationSpeed"/>..<see cref="MaxAnimationSpeed"/>: 2 runs a 60 s loop in
    /// 30 s of project time. Composes with (does not replace) whatever a speed effect does to the
    /// project clock. Ignored by the static styles.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    public override ItemContent Clone() => new BackgroundContent
    {
        Style = Style,
        Theme = Theme,
        Color = Color,
        AnimationSpeed = AnimationSpeed,
    };
}

/// <summary>How an <see cref="ObscureContent"/> hides what is under it — the image editor's
/// obscure modes, in its menu order (its "Solid" is spelled Blackout here). Serialized as a string
/// (see <see cref="ProjectJsonContext"/>), so the names are wire contract.</summary>
public enum ObscureKind
{
    /// <summary>Pixelated into square cells <see cref="ObscureContent.Strength"/> output pixels
    /// wide.</summary>
    Mosaic,

    /// <summary>Gaussian blur at a sigma of <see cref="ObscureContent.Strength"/> output
    /// pixels.</summary>
    Blur,

    /// <summary>Painted over in solid black; <see cref="ObscureContent.Strength"/> is
    /// ignored.</summary>
    Blackout,
}

/// <summary>
/// A redaction box: the video editor's take on the image editor's obscure tool. It has no pixels
/// of its own — the composer replaces whatever has been drawn <b>below</b> it (lower rows) inside
/// its box with a mosaic, blur or blackout of those pixels, while rows above it draw over the
/// result untouched, so the track order decides what is hidden.
///
/// <para>Placed exactly like a <see cref="SolidContent"/>: its natural size is the canvas, so
/// <see cref="Transform.Scale"/> is a fraction of the canvas width and <see cref="Transform.ScaleY"/>
/// a fraction of its height. Like a <see cref="BackgroundContent"/> it has no aspect ratio of its
/// own and lives permanently free-sized (ScaleY set — see <see cref="Project.Normalize"/>).
/// Rotation and opacity apply; crop, aspect, surround and effect do not.</para>
///
/// <para>Entry/Exit hold <see cref="TransitionKind.Ramp"/> transitions that fade the obscuring in
/// from (and back out to) the clear picture.</para>
/// </summary>
public sealed class ObscureContent : ItemContent
{
    /// <summary>The image editor's default obscure size (its <c>BlurRadius</c>), so a fresh box
    /// looks the same in both editors at 1:1.</summary>
    public const double DefaultStrength = 8;

    /// <summary>The range <see cref="Strength"/> is validated to, and what the editor's spinner
    /// offers.</summary>
    public const double MinStrength = 1;

    public const double MaxStrength = 200;

    public ObscureKind Kind { get; set; } = ObscureKind.Mosaic;

    /// <summary>Mosaic cell size or blur sigma, in output-canvas pixels (mapped onto a smaller
    /// preview canvas exactly like <see cref="TextContent.Size"/>, so the preview matches the
    /// render). Ignored by <see cref="ObscureKind.Blackout"/>.</summary>
    public double Strength { get; set; } = DefaultStrength;

    public override ItemContent Clone() => new ObscureContent { Kind = Kind, Strength = Strength };
}

/// <summary>What a <see cref="ShapeContent"/> draws. Serialized as a string (see
/// <see cref="ProjectJsonContext"/>), so the names are wire contract: append new kinds, never
/// rename one. An older build refuses a name it does not know (the string enum converter throws),
/// so a project using a newer kind does not open there; that beats silently turning the shape into
/// a rectangle and saving it back that way.</summary>
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    Diamond,

    /// <summary>A regular polygon of <see cref="ShapeContent.PolygonSides"/> sides.</summary>
    Polygon,

    /// <summary><see cref="ShapeContent.StarPoints"/> points, the inner vertices at
    /// <see cref="ShapeContent.StarInnerRatio"/> of the outer radius.</summary>
    Star,

    Heart,

    /// <summary>A speech bubble with its tail at the bottom left.</summary>
    Bubble,

    /// <summary>A stroked line between two endpoints; this and the two arrow kinds are the line
    /// kinds (<see cref="ShapeContent.IsLineKind"/>).</summary>
    Line,

    /// <summary>The image editor's arrow: a tapered shaft and a head at the end point.</summary>
    Arrow,

    /// <summary>An arrow with a head at both endpoints.</summary>
    DoubleArrow,
}

/// <summary>The image editor's ink dash styles; the patterns are multiples of the stroke width
/// (Dashed 4 on, 2 off; Dotted 1 on, 2 off). Serialized as a string, with the same append-only
/// rule as <see cref="ShapeKind"/>.</summary>
public enum ShapeDash
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>
/// A vector shape: a closed outline (rectangle, ellipse, polygon, star, heart, speech bubble) or a
/// line kind (line, arrow, double arrow), drawn by the composer in both the preview and the
/// render, so it is placed, faded, slid, wiped and surrounded exactly like every other visual item.
///
/// <para>Boxed like a <see cref="SolidContent"/>: its natural size is the canvas, so
/// <see cref="Transform.Scale"/> is a fraction of the canvas width and <see cref="Transform.ScaleY"/>
/// a fraction of its height, and like an <see cref="ObscureContent"/> it lives permanently
/// free-sized (ScaleY set, see <see cref="Project.Normalize"/>). A closed shape fills that box and
/// its outline is stroked <b>inside</b> it, as the image editor strokes its rectangles, so the ink
/// never leaves the box the gizmo shows. Crop, aspect, mask and effect do not apply.</para>
///
/// <para>A line kind's geometry is its endpoints, stored as fractions of the box
/// (<see cref="StartX"/>..<see cref="EndY"/>), and a bow (<see cref="Bend"/>,
/// <see cref="BendSkew"/>) stored relative to the chord, so moving or stretching the box carries
/// the line with it. The editor's handles rewrite the box tightly around the line on every drag
/// (<c>ShapeLineLayout.Write</c>), and every kind change goes through
/// <c>ShapeLineLayout.ConvertKind</c>.</para>
///
/// <para>Stroke widths and corner radii are output-canvas pixels, mapped onto a smaller preview
/// canvas exactly like <see cref="TextContent.Size"/>, so the preview matches the render.</para>
/// </summary>
public sealed class ShapeContent : ItemContent
{
    /// <summary>The image editor's default ink (its <c>ObjectColor</c>).</summary>
    public const string DefaultStrokeColor = "#FFFF0000";

    /// <summary>What a bare entry reads with. A shape the editor adds is seeded from the output
    /// height instead (<c>EditorSession.AddShape</c>), the way a text card's size is.</summary>
    public const double DefaultStrokeWidth = 6;

    /// <summary>The range <see cref="StrokeWidth"/> is validated to, and what the editor's
    /// spinner offers. 0 draws no outline on a closed shape.</summary>
    public const double MinStrokeWidth = 0;

    public const double MaxStrokeWidth = 200;

    /// <summary>The thinnest a line kind draws: it has no fill, so a width of 0 would draw
    /// nothing at all.</summary>
    public const double MinLineStrokeWidth = 1;

    /// <summary>Red at 35%: with the red outline it makes a highlight box.</summary>
    public const string DefaultFillColor = "#59FF0000";

    public const double MinCornerRadius = 0;

    public const double MaxCornerRadius = 1000;

    public const double DefaultCornerRadius = 0;

    public const int MinPolygonSides = 3;

    public const int MaxPolygonSides = 12;

    public const int DefaultPolygonSides = 6;

    public const int MinStarPoints = 3;

    public const int MaxStarPoints = 12;

    public const int DefaultStarPoints = 5;

    public const double MinStarInnerRatio = 0.1;

    public const double MaxStarInnerRatio = 0.95;

    public const double DefaultStarInnerRatio = 0.5;

    /// <summary>The range each endpoint fraction is validated to: a line may reach well past its
    /// box after a programmatic stretch, but not without bound.</summary>
    public const double MinLineCoord = -1;

    public const double MaxLineCoord = 2;

    /// <summary>The largest <see cref="Bend"/> either way, in chord lengths.</summary>
    public const double MaxBend = 4;

    /// <summary>The largest <see cref="BendSkew"/> either way: the image editor's limit
    /// (<c>CurveBend.MaxSkew</c>), past which the curve folds back on itself.</summary>
    public const double MaxBendSkew = 0.25;

    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;

    /// <summary>The outline's color, and the whole ink of a line kind, as <c>#AARRGGBB</c> (or
    /// <c>#RRGGBB</c>). An unparseable value draws the default red.</summary>
    public string StrokeColor { get; set; } = DefaultStrokeColor;

    /// <summary>Output-canvas pixels. 0 draws no outline on a closed shape; a line kind draws at
    /// least <see cref="MinLineStrokeWidth"/>.</summary>
    public double StrokeWidth { get; set; } = DefaultStrokeWidth;

    public ShapeDash Dash { get; set; } = ShapeDash.Solid;

    /// <summary>Whether a closed shape is filled with <see cref="FillColor"/>. A flag rather than
    /// a zero alpha, so turning the fill off and back on keeps the color. Line kinds ignore it.</summary>
    public bool Fill { get; set; }

    public string FillColor { get; set; } = DefaultFillColor;

    /// <summary>Output pixels, on the kinds with corners (<see cref="HasCorners"/>). Each
    /// vertex's radius is clamped so its arc stays within half of each edge beside it, so a large
    /// value rounds every corner as far as it can go.</summary>
    public double CornerRadius { get; set; } = DefaultCornerRadius;

    public int PolygonSides { get; set; } = DefaultPolygonSides;

    public int StarPoints { get; set; } = DefaultStarPoints;

    /// <summary>The inner vertices' radius over the outer one, before the star is stretched to
    /// fill its box.</summary>
    public double StarInnerRatio { get; set; } = DefaultStarInnerRatio;

    /// <summary>Line kinds: the start point as a fraction of the box (0, 0 is its top left). The
    /// default line runs from the bottom left corner to the top right one.</summary>
    public double StartX { get; set; } = 0;

    public double StartY { get; set; } = 1;

    /// <summary>Line kinds: the end point (where an arrow's head is), as a fraction of the box.</summary>
    public double EndX { get; set; } = 1;

    public double EndY { get; set; } = 0;

    /// <summary>Line kinds: the bow, as the image editor's <c>CurveOffset</c> divided by the chord
    /// length: signed, along the chord's left normal in canvas space (y down). 0 is straight.</summary>
    public double Bend { get; set; }

    /// <summary>Line kinds: the image editor's <c>CurveSkew</c>, the bow's lean along the chord
    /// as a fraction of it (positive toward the end).</summary>
    public double BendSkew { get; set; }

    public static bool IsLineKind(ShapeKind kind) =>
        kind is ShapeKind.Line or ShapeKind.Arrow or ShapeKind.DoubleArrow;

    /// <summary>The kinds <see cref="CornerRadius"/> rounds.</summary>
    public static bool HasCorners(ShapeKind kind) => kind is ShapeKind.Rectangle or ShapeKind.Triangle
        or ShapeKind.Diamond or ShapeKind.Polygon or ShapeKind.Star or ShapeKind.Bubble;

    /// <summary>Computed from <see cref="Kind"/> and never stored: everything that changes how a
    /// shape draws must serialize, or undo (which snapshots the JSON) would lose it.</summary>
    [JsonIgnore]
    public bool IsLine => IsLineKind(Kind);

    public override ItemContent Clone() => new ShapeContent
    {
        Kind = Kind,
        StrokeColor = StrokeColor,
        StrokeWidth = StrokeWidth,
        Dash = Dash,
        Fill = Fill,
        FillColor = FillColor,
        CornerRadius = CornerRadius,
        PolygonSides = PolygonSides,
        StarPoints = StarPoints,
        StarInnerRatio = StarInnerRatio,
        StartX = StartX,
        StartY = StartY,
        EndX = EndX,
        EndY = EndY,
        Bend = Bend,
        BendSkew = BendSkew,
    };
}

/// <summary>A playback-speed effect: while the item is active the whole output plays at
/// <see cref="Factor"/>. Lives only on the single pinned <see cref="TrackKind.Effect"/> speed
/// row; the item's Entry/Exit hold <see cref="TransitionKind.Ramp"/> transitions easing
/// 1 → factor → 1.</summary>
public sealed class SpeedContent : ItemContent
{
    /// <summary>Target speed factor, validated to 0.1..10.</summary>
    public double Factor { get; set; } = 2.0;

    /// <summary>When true (the default) the audio under the item is time-stretched so its pitch
    /// stays put; when false it is plainly resampled and pitch rides with the speed (like
    /// <see cref="MediaContent.Speed"/>).</summary>
    public bool PitchCorrect { get; set; } = true;

    public override ItemContent Clone() => new SpeedContent { Factor = Factor, PitchCorrect = PitchCorrect };
}

/// <summary>A zoom effect: scales every video track composited beneath its row (lower
/// <see cref="Track.Order"/>) by <see cref="Zoom"/> about the focal point. Stacked zoom rows
/// multiply; Entry/Exit hold <see cref="TransitionKind.Ramp"/> transitions easing
/// 1 → zoom → 1.</summary>
public sealed class ZoomContent : ItemContent
{
    /// <summary>Magnification, validated to 1.0..5.0.</summary>
    public double Zoom { get; set; } = 1.5;

    /// <summary>Focal point as a fraction of the canvas width, validated to 0..1.</summary>
    public double FocusX { get; set; } = 0.5;

    /// <summary>Focal point as a fraction of the canvas height, validated to 0..1.</summary>
    public double FocusY { get; set; } = 0.5;

    public override ItemContent Clone() => new ZoomContent
    {
        Zoom = Zoom,
        FocusX = FocusX,
        FocusY = FocusY,
    };
}

/// <summary>A rendered cursor overlay driven by the recording's input-capture data
/// (<see cref="Source.InputCapturePath"/>). Lives on a <see cref="TrackKind.Video"/> row that is
/// hard-synced to its recording: every item always carries the recording's
/// <see cref="Item.GroupId"/>, so it moves/trims/splits with the screen row. Position is
/// data-driven (the captured cursor path), never the item's <see cref="Item.Transform"/>.</summary>
public sealed class CursorContent : ItemContent
{
    /// <summary>The style names the editor offers, in menu order. <c>none</c> draws no cursor at
    /// all (the click highlight is its own setting and still draws); <c>native</c> draws the cursor
    /// sprites the recorder rasterized into the capture file (the real cursor, custom app cursors
    /// included); every other style draws a themed vector glyph at the captured position. An
    /// unrecognized value renders as the theme's arrow.</summary>
    public static readonly IReadOnlyList<string> Styles = new[] { "none", "native", "vision", "point", "bibata", "breezex", "macos", "fuchsia", "neon" };

    /// <summary>The click animation names the editor offers, in menu order. <c>ripple</c>/<c>pulse</c>
    /// fire one burst per release; <c>ring</c> is a circle outline pinned to the pointer that closes
    /// while a button is held and springs back out; <c>pressure</c> draws nothing of its own and
    /// instead stretches the recorded pixels toward the pointer while a button is held.</summary>
    public static readonly IReadOnlyList<string> ClickAnimations = new[] { "none", "ripple", "pulse", "ring", "pressure" };

    public Guid SourceId { get; set; }

    /// <summary>One of <see cref="Styles"/>.</summary>
    public string Style { get; set; } = "vision";

    /// <summary>Which of the style's colorways to draw (<c>CursorAssets.Variants</c>), or null
    /// for its default — which is the only sensible value for the styles that ship just one, and
    /// is why no project written before colorways existed needs migrating. A value the style does
    /// not offer falls back to its default rather than drawing nothing.</summary>
    public string Variant { get; set; }

    /// <summary>Size multiplier, validated to 0.25..5: over the style's base size for themed
    /// glyphs, over the sprite's recorded pixel size for the <c>native</c> style.</summary>
    public double Size { get; set; } = 1.0;

    /// <summary>Debounces the capture's Hidden/visible flicker: Windows hides the cursor while
    /// the user types and flashes it back on every pause, so with this on (the default, and the
    /// only behavior before the setting existed) a hidden cursor stays hidden until it actually
    /// moves (more than a few pixels — jitter does not count) or clicks again. Off draws exactly
    /// what CURSORINFO reported frame by frame.</summary>
    public bool Debounce { get; set; } = true;

    /// <summary>One of <see cref="ClickAnimations"/>.</summary>
    public string ClickAnimation { get; set; } = "none";

    /// <summary>Click animation color, packed ARGB.</summary>
    public uint ClickColor { get; set; } = 0xFFFF0000;

    /// <summary>The <c>ring</c> highlight's inner fill opacity, validated to 0..1: the outline is
    /// drawn in <see cref="ClickColor"/> as-is and the disc inside it in the same color at this
    /// opacity. Ignored by every other animation.</summary>
    public const double DefaultFillOpacity = 0.15;

    public double FillOpacity { get; set; } = DefaultFillOpacity;

    /// <summary>The range every highlight multiplier below is validated to, and what the editor's
    /// spinners offer. One range for all three: they are all "a bit more / a bit less of the
    /// stock behavior", and a quarter to quadruple is as far as any of them stays useful.</summary>
    public const double MinHighlightFactor = 0.25;

    public const double MaxHighlightFactor = 4.0;

    /// <summary>Size multiplier on the dot drawn under a held mouse button. Ignored while
    /// <see cref="ClickAnimation"/> is <c>none</c>, which draws no highlight at all.</summary>
    public double HoldSize { get; set; } = 1.0;

    /// <summary>Size multiplier on the animation the release fires — the whole sweep scales, so a
    /// pulse shrinks from further out just as a ripple grows further.</summary>
    public double ClickSize { get; set; } = 1.0;

    /// <summary>Playback-rate multiplier on that animation: 2 runs it in half the time. Distinct
    /// from the clip's own speed, which the composer already folds in so a sped-up clip does not
    /// compress the highlight.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    public override ItemContent Clone() => new CursorContent
    {
        SourceId = SourceId,
        Style = Style,
        Variant = Variant,
        Size = Size,
        Debounce = Debounce,
        ClickAnimation = ClickAnimation,
        ClickColor = ClickColor,
        FillOpacity = FillOpacity,
        HoldSize = HoldSize,
        ClickSize = ClickSize,
        AnimationSpeed = AnimationSpeed,
    };
}

/// <summary>Which keystroke runs the overlay shows. Serialized as a string (see
/// <see cref="ProjectJsonContext"/>), so the names are wire contract.</summary>
public enum KeystrokeFilter
{
    /// <summary>Everything the capture recorded — typing included.</summary>
    None,

    /// <summary>Only the keys that draw as keycaps: shortcuts, plus every non-printable key the
    /// overlay would render as a key (Esc, Enter, F5, …). Plain typing is dropped.</summary>
    Special,

    /// <summary>Only shortcut chords (a non-shift modifier + keys, e.g. Ctrl+C).</summary>
    Shortcuts,
}

/// <summary>A keystroke overlay driven by the recording's input-capture data
/// (<see cref="Source.InputCapturePath"/>). Same hard-sync rules as <see cref="CursorContent"/>;
/// unlike it, placement <b>is</b> the item's <see cref="Item.Transform"/> — X/Y anchor the
/// block's bottom center and Scale is the wrap width as a fraction of the canvas, with rows
/// stacking upward from the anchor.</summary>
public sealed class KeyboardContent : ItemContent
{
    /// <summary>The typing pill's default fill: black at 55%. The translucency is <b>in</b> the
    /// stored alpha, so a user who picks a color picks its opacity with it.</summary>
    public const uint DefaultBackgroundColor = 0x8C000000;

    public const uint DefaultTextColor = 0xFFFFFFFF;

    /// <summary>Keystrokes read at a glance or not at all, so the block starts big — the
    /// compositor falls back to this for a non-positive size too.</summary>
    public const double DefaultFontSize = 40;

    public Guid SourceId { get; set; }

    /// <summary>Font size in output-canvas pixels, validated to 8..200.</summary>
    public double FontSize { get; set; } = DefaultFontSize;

    /// <summary>How long a finished run of keystrokes stays fully visible after its last key,
    /// in ms, validated to 0..10000. What happens either side of it — how a row arrives and how
    /// it leaves — is the item's own <see cref="Item.Entry"/>/<see cref="Item.Exit"/>, applied
    /// per row rather than to the block (see <c>FrameComposer.DrawKeyboard</c>).</summary>
    public int LingerMs { get; set; } = 1000;

    /// <summary>Typing gap that ends a run and starts a new row, in ms, validated to
    /// 0..10000.</summary>
    public int PauseBreakMs { get; set; } = 1000;

    /// <summary>Which keystrokes the overlay shows (see <see cref="KeystrokeFilter"/>).</summary>
    public KeystrokeFilter Filter { get; set; } = KeystrokeFilter.None;

    /// <summary>Typed text color, packed ARGB. Styles the plain-typing pill's text only — the
    /// special keys draw as keycaps with their own fixed livery.</summary>
    public uint TextColor { get; set; } = DefaultTextColor;

    /// <summary>Typing pill fill, packed ARGB — alpha included (see
    /// <see cref="DefaultBackgroundColor"/>).</summary>
    public uint BackgroundColor { get; set; } = DefaultBackgroundColor;

    public override ItemContent Clone() => new KeyboardContent
    {
        SourceId = SourceId,
        FontSize = FontSize,
        LingerMs = LingerMs,
        PauseBreakMs = PauseBreakMs,
        Filter = Filter,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
    };
}
