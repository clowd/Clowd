using System;

namespace Clowd.VideoSDK.Model;

public enum TrackKind
{
    Video,
    Audio,

    /// <summary>A row of effect items (<see cref="SpeedContent"/>/<see cref="ZoomContent"/>):
    /// never painted or mixed, it modulates how the rest of the timeline plays. Appended so the
    /// string-serialized enum stays wire-compatible.</summary>
    Effect,
}

/// <summary>One timeline row. Tracks carry presentation state only — the items on a track live in
/// the project's flat <see cref="Project.Items"/> list, keyed by <see cref="Item.TrackId"/>.
/// Video tracks composite bottom-up in ascending <see cref="Order"/> (higher order draws on top,
/// which is how the webcam PiP sits over the screen).</summary>
public sealed class Track
{
    public Guid Id { get; set; }

    public TrackKind Kind { get; set; }

    public string Name { get; set; }

    /// <summary>Stacking/row position. Ascending order composites later (on top) for video.</summary>
    public int Order { get; set; }

    /// <summary>Video: excluded from the picture. Meaningless for audio (use <see cref="Muted"/>).</summary>
    public bool Hidden { get; set; }

    /// <summary>Audio: excluded from the mix. Meaningless for video (use <see cref="Hidden"/>).</summary>
    public bool Muted { get; set; }

    /// <summary>UI hint: the editor refuses to start edits on a locked track. Not enforced by
    /// <see cref="TimelineOps"/> — a link-group op started from an unlocked member still applies
    /// to the whole group.</summary>
    public bool Locked { get; set; }
}
