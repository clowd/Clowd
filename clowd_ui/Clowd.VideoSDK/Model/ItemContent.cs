using System;
using System.Text.Json.Serialization;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// What fills an <see cref="Item"/>'s span. Polymorphic on a <c>$type</c> discriminator
/// (<c>media</c> / <c>text</c> / <c>image</c> / <c>solid</c> / <c>speed</c> / <c>zoom</c>) via System.Text.Json's built-in
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

/// <summary>A playback-speed effect: while the item is active the whole output plays at
/// <see cref="Factor"/> (audio pitch rides with it, like <see cref="MediaContent.Speed"/>).
/// Lives only on the single pinned <see cref="TrackKind.Effect"/> speed row; the item's
/// Entry/Exit hold <see cref="TransitionKind.Ramp"/> transitions easing 1 → factor → 1.</summary>
public sealed class SpeedContent : ItemContent
{
    /// <summary>Target speed factor, validated to 0.1..10.</summary>
    public double Factor { get; set; } = 2.0;

    public override ItemContent Clone() => new SpeedContent { Factor = Factor };
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
