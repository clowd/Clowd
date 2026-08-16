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
    /// aspect ratio (after <see cref="Crop"/>) unless <see cref="ScaleY"/> overrides it.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Item height as a fraction of the canvas height, or null — the default — to keep the
    /// content's own aspect ratio. Only set when the user unlocks the aspect ratio in the editor
    /// and sizes the two axes apart; a recording, a webcam and an import all want the lock, so the
    /// common case writes nothing to the project file at all.
    ///
    /// For text, where <see cref="Scale"/> multiplies the natural block width instead of mapping to
    /// a canvas fraction, this multiplies the natural block height the same way: ScaleY is to the
    /// height exactly what Scale is to the width, whichever rule the content follows.
    /// </summary>
    public double? ScaleY { get; set; }

    /// <summary>Clockwise rotation about the item centre, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>0 = transparent, 1 = opaque. Multiplies with transition opacity.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    /// Target aspect ratio (width/height), or null — the default — for the content's own. Applied
    /// AFTER <see cref="Crop"/>: <see cref="AspectStretch"/> false trims the cropped picture
    /// symmetrically to this ratio, true distorts it into the ratio — either way the drawn box
    /// always honours the ratio, and cropping only changes what is shown inside it. The crop
    /// stores only what the user cut, never what the ratio implied.
    /// </summary>
    public double? Aspect { get; set; }

    /// <summary>How <see cref="Aspect"/> is reached: false (the default) crops the excess away
    /// ("fill"), true stretches the picture to fit. Meaningless while Aspect is null.</summary>
    public bool AspectStretch { get; set; }

    /// <summary>Region of the source picture to show, or null for the whole frame. Applied first
    /// — before <see cref="Aspect"/> and <see cref="Scale"/>.</summary>
    public CropRect Crop { get; set; }

    /// <summary>Shape the item is clipped to, or null for the plain rectangle.</summary>
    public Mask Mask { get; set; }

    public Transform Clone() => new Transform
    {
        X = X,
        Y = Y,
        Scale = Scale,
        ScaleY = ScaleY,
        Rotation = Rotation,
        Opacity = Opacity,
        Aspect = Aspect,
        AspectStretch = AspectStretch,
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

/// <summary>Mirror of the v1 <c>WebcamOverlayShape</c>, plus <see cref="Squircle"/> which v1 had no
/// equivalent for. New members must be appended: the project file writes these as strings, so the
/// names are the on-disk format.</summary>
public enum MaskShape
{
    /// <summary>The ellipse inscribed in the item's rendered rect (a circle when the rect is
    /// square) — the exact semantics of the v1 "Circle" mask PNGs and overlay preview.</summary>
    Circle,
    RoundedRect,

    /// <summary>The superellipse inscribed in the item's rendered rect — a fixed shape with no
    /// radius to tune, unlike <see cref="RoundedRect"/>. See <see cref="MaskGeometry"/>.</summary>
    Squircle,
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
