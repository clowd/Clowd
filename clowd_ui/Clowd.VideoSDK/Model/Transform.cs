using System;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// Places an item's picture on the output canvas. Geometry is normalized against the canvas so a
/// project survives a change of output resolution — exactly the convention of the v1
/// <c>WebcamOverlay</c>, which maps losslessly: <c>CenterX/CenterY → X/Y</c>,
/// <c>Width → Scale</c>, <c>Shape/CornerRadius → Mask</c>.
///
/// <list type="bullet">
/// <item><see cref="X"/>/<see cref="Y"/> — item <b>centre</b>, 0-1 of canvas width/height.</item>
/// <item><see cref="Scale"/> — item width as a fraction of canvas width; the height follows the
/// content's own aspect ratio (a webcam item never needs to know the canvas aspect).</item>
/// </list>
///
/// The defaults (centred, full width) draw a same-aspect video full-frame, so a plain screen
/// recording needs no transform at all.
/// </summary>
public sealed class Transform
{
    /// <summary>Item centre X, 0-1 of the canvas width.</summary>
    public double X { get; set; } = 0.5;

    /// <summary>Item centre Y, 0-1 of the canvas height.</summary>
    public double Y { get; set; } = 0.5;

    /// <summary>Item width as a fraction of the canvas width; height follows the content's
    /// aspect ratio (after <see cref="Crop"/>).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Clockwise rotation about the item centre, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>0 = transparent, 1 = opaque. Multiplies with transition opacity.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Region of the source picture to show, or null for the whole frame. Applied
    /// before <see cref="Scale"/>.</summary>
    public CropRect Crop { get; set; }

    /// <summary>Shape the item is clipped to, or null for the plain rectangle.</summary>
    public Mask Mask { get; set; }

    public Transform Clone() => new Transform
    {
        X = X,
        Y = Y,
        Scale = Scale,
        Rotation = Rotation,
        Opacity = Opacity,
        Crop = Crop?.Clone(),
        Mask = Mask?.Clone(),
    };
}

/// <summary>Fractional insets from each edge of the source picture, 0-1 (all zero = uncropped).
/// Insets rather than a rect so a default-constructed crop is the identity.</summary>
public sealed class CropRect
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Right { get; set; }

    public double Bottom { get; set; }

    public CropRect Clone() => new CropRect { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
}

/// <summary>Mirror of the v1 <c>WebcamOverlayShape</c>.</summary>
public enum MaskShape
{
    /// <summary>The ellipse inscribed in the item's rendered rect (a circle when the rect is
    /// square) — the exact semantics of the v1 "Circle" mask PNGs and overlay preview.</summary>
    Circle,
    RoundedRect,
}

/// <summary>Clips the item to a shape. <see cref="CornerRadius"/> is a fraction of the item's
/// rendered <b>height</b> (0 = square corners, 0.5 = fully rounded ends) and applies to
/// <see cref="MaskShape.RoundedRect"/> only — the same convention as the v1 webcam overlay.</summary>
public sealed class Mask
{
    public MaskShape Shape { get; set; }

    public double CornerRadius { get; set; }

    public Mask Clone() => new Mask { Shape = Shape, CornerRadius = CornerRadius };
}
