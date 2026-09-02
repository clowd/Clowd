using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// What fills an <see cref="Item"/>'s span. Polymorphic on a <c>$type</c> discriminator
/// (<c>media</c> / <c>text</c> / <c>image</c> / <c>solid</c> / <c>speed</c> / <c>zoom</c> /
/// <c>cursor</c> / <c>keyboard</c> / <c>background</c>) via System.Text.Json's built-in
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

    public override ItemContent Clone() => new MediaContent
    {
        SourceId = SourceId,
        StreamIndex = StreamIndex,
        SourceInTicks = SourceInTicks,
        Speed = Speed,
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

    /// <summary>Clowd blue as <c>#AARRGGBB</c> — what a fresh <c>solid</c> background is filled
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
/// <see cref="Item.LinkGroupId"/>, so it moves/trims/splits with the screen row. Position is
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
