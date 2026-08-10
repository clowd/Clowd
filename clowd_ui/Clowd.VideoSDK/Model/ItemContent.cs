using System;
using System.Text.Json.Serialization;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// What fills an <see cref="Item"/>'s span. Polymorphic on a <c>$type</c> discriminator
/// (<c>media</c> / <c>text</c> / <c>image</c> / <c>solid</c>) via System.Text.Json's built-in
/// polymorphism, which the source-generated <see cref="ProjectJsonContext"/> supports. The
/// discriminator strings are wire contract — renaming a class is free, renaming a discriminator
/// breaks every saved project.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MediaContent), "media")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(SolidContent), "solid")]
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
    /// item moves this by the same amount.</summary>
    public long SourceInTicks { get; set; }

    public override ItemContent Clone() => new MediaContent
    {
        SourceId = SourceId,
        StreamIndex = StreamIndex,
        SourceInTicks = SourceInTicks,
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
