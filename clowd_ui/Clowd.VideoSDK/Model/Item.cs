using System;
using System.Text.Json.Serialization;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// One clip on the timeline: a half-open span
/// <c>[TimelineStartTicks, TimelineStartTicks + DurationTicks)</c> of the output, filled by
/// <see cref="Content"/> and placed on the canvas by <see cref="Transform"/>. A "cut" is not a
/// model concept — cutting a recording produces multiple items back-to-back, each with its own
/// <see cref="MediaContent.SourceInTicks"/>.
///
/// Mutate items through <see cref="TimelineOps"/>, not directly: that is the only place the
/// <see cref="LinkGroupId"/> semantics live.
/// </summary>
public sealed class Item
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    /// <summary>Where the item begins on the output timeline, in 100ns ticks.</summary>
    public long TimelineStartTicks { get; set; }

    /// <summary>Length on the output timeline, in 100ns ticks. Always positive; editing
    /// operations keep it at or above <see cref="TimelineOps.MinSegmentTicks"/>.</summary>
    public long DurationTicks { get; set; }

    /// <summary>Exclusive end of the item on the output timeline.</summary>
    [JsonIgnore]
    public long TimelineEndTicks => TimelineStartTicks + DurationTicks;

    public ItemContent Content { get; set; }

    public Transform Transform { get; set; } = new Transform();

    /// <summary>What is drawn AROUND the item's silhouette (shadow / glow / outline), or null for a
    /// bare item. Read by the picture draws and the themed cursor glyph only. Deliberately not
    /// called an effect: an effect is applied to the item's own pixels (<see cref="Effect"/>),
    /// which is a different thing on a different side of the silhouette — see
    /// <see cref="Clowd.VideoSDK.Model.Surround"/>.</summary>
    public Surround Surround { get; set; }

    /// <summary>What is applied to the item's OWN pixels (blur / background blur / background
    /// removal), or null for an untouched item — the slot the <see cref="Surround"/> remarks
    /// reserved. Read by the picture draws only; see
    /// <see cref="Clowd.VideoSDK.Model.VideoEffect"/>.</summary>
    public VideoEffect Effect { get; set; }

    /// <summary>Transition played over the first part of the item, or null for a hard start.</summary>
    public Transition Entry { get; set; }

    /// <summary>Transition played over the last part of the item, or null for a hard end.</summary>
    public Transition Exit { get; set; }

    /// <summary>Linear gain applied to the item's audio, 1.0 = unity.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Items sharing a non-null group id move/trim/split as one — this is the sync
    /// toggle for the rows that came from a single recording. Null = unlinked.</summary>
    public Guid? LinkGroupId { get; set; }
}
