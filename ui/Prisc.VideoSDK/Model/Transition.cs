using System;

namespace Prisc.VideoSDK.Model;

public enum TransitionKind
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Wipe,

    /// <summary>An effect item's entry/exit ramp: eases the item's factor from/to neutral over
    /// <see cref="Transition.DurationTicks"/>. No visual effect — only meaningful on
    /// <see cref="SpeedContent"/>/<see cref="ZoomContent"/> items. Appended so the
    /// string-serialized enum stays wire-compatible.</summary>
    Ramp,

    /// <summary>Shapes only (<see cref="ShapeContent"/>): a line grows along its path from start
    /// to end, a closed outline is traced while its fill fades in, and the exit erases it from the
    /// start the same way. Appended so the string-serialized enum stays wire-compatible; like
    /// <see cref="Ramp"/>, an older build cannot open a project that uses it.</summary>
    Draw,
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
