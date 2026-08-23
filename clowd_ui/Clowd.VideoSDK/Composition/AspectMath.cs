using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition;

/// <summary>
/// Resolves a <see cref="Transform"/>'s aspect-ratio intent (<see cref="Transform.Aspect"/> /
/// <see cref="Transform.AspectStretch"/>) and user crop into the two numbers the composer and the
/// editor's placement math actually consume: which region of the source picture to show, and what
/// height/width ratio the drawn box has. One implementation, used by <c>FrameComposer</c> (preview
/// AND render) and the editor's <c>ItemPlacement</c>, so the gizmo cannot disagree with the pixels.
///
/// Order of operations — the model's contract: <see cref="Transform.Crop"/> cuts the source
/// FIRST, and the aspect ratio then reshapes whatever survived (fill trims it symmetrically to
/// the ratio, stretch distorts it into the ratio). The drawn box therefore always honors the
/// chosen ratio — cropping changes what is shown inside it, never its shape — and the crop fields
/// store only what the user cut.
/// </summary>
public static class AspectMath
{
    /// <summary>
    /// The region of the raw source picture to display, as fractional insets: the user's crop,
    /// plus — for a fill aspect — the centered trim that brings the cropped region to the target
    /// ratio. A stretch trims nothing extra (the whole cropped region is distorted into the
    /// ratio). Insets may sum to ≥ 1 — "cropped to nothing", which callers must treat as nothing
    /// to draw.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) SourceInsets(
        Transform transform, double sourceWidth, double sourceHeight)
    {
        double userL = 0, userT = 0, userR = 0, userB = 0;
        if (transform?.Crop is { } crop)
        {
            userL = Clamp01(crop.Left);
            userT = Clamp01(crop.Top);
            userR = Clamp01(crop.Right);
            userB = Clamp01(crop.Bottom);
        }

        double croppedW = 1 - userL - userR;
        double croppedH = 1 - userT - userB;

        if (transform is not { Aspect: > 0, AspectStretch: false } ||
            !(sourceWidth > 0) || !(sourceHeight > 0) || croppedW <= 0 || croppedH <= 0)
        {
            return (userL, userT, userR, userB);
        }

        // fill: trim the CROPPED region, centered, to the target ratio — the trim is a fraction
        // of that region, so it scales back to source fractions by what the crop left standing
        double content = (sourceWidth * croppedW) / (sourceHeight * croppedH);
        double target = transform.Aspect.Value;
        if (content > target)
        {
            double trim = (1 - target / content) / 2 * croppedW;
            return (userL + trim, userT, userR + trim, userB);
        }

        if (content < target)
        {
            double trim = (1 - content / target) / 2 * croppedH;
            return (userL, userT + trim, userR, userB + trim);
        }

        return (userL, userT, userR, userB);
    }

    /// <summary>
    /// The drawn box's height/width ratio when the height derives from the content (i.e.
    /// <see cref="Transform.ScaleY"/> is null): the target ratio whenever one is set — fill and
    /// stretch both land the box exactly on it — otherwise the cropped region's own ratio. Null
    /// when the dimensions are unknown or the crop leaves nothing.
    /// </summary>
    public static double? DisplayAspect(Transform transform, double sourceWidth, double sourceHeight)
    {
        if (!(sourceWidth > 0) || !(sourceHeight > 0))
            return null;

        var (l, t, r, b) = SourceInsets(transform, sourceWidth, sourceHeight);
        double regionW = 1 - l - r;
        double regionH = 1 - t - b;
        if (regionW <= 0 || regionH <= 0)
            return null;

        if (transform?.Aspect is > 0 and var target)
            return 1 / target;

        return (sourceHeight * regionH) / (sourceWidth * regionW);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
