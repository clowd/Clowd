using System;

namespace Clowd.VideoSDK.Model;

public enum TransitionKind
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Wipe,
}

public enum TransitionEasing
{
    Linear,
    CubicIn,
    CubicOut,
    CubicInOut,
}

/// <summary>An entry or exit effect played over the first/last <see cref="DurationTicks"/> of an
/// item. The direction of a slide/wipe is the direction the picture travels on entry (mirrored
/// automatically on exit by the compositor).</summary>
public sealed class Transition
{
    public TransitionKind Kind { get; set; }

    /// <summary>Length of the effect in 100ns ticks. Clamped by the compositor to the item's own
    /// duration when the item is shorter.</summary>
    public long DurationTicks { get; set; }

    public TransitionEasing Easing { get; set; }

    public Transition Clone() => new Transition
    {
        Kind = Kind,
        DurationTicks = DurationTicks,
        Easing = Easing,
    };
}
