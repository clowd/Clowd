using System;

namespace Clowd.VideoSDK.Model;

/// <summary>
/// Outlines for the <see cref="MaskShape"/> members that no graphics toolkit draws natively, in
/// plain numbers so the compositor (SkiaSharp) and the editor's gizmo and shape picker (Avalonia)
/// all trace the same curve — the preview is only WYSIWYG if they agree.
/// </summary>
public static class MaskGeometry
{
    /// <summary>The superellipse exponent: |x/a|^n + |y/b|^n = 1. 2 is the plain ellipse and larger
    /// values approach the rectangle; 3 sits between, rounder than the canonical squircle's 4 so the
    /// shape reads as deliberately curved rather than as a rounded rectangle.</summary>
    public const double SquircleExponent = 3.0;

    /// <summary>Points in the polyline <see cref="BuildSquircle"/> writes. Enough that the chord
    /// error stays under half a pixel at 4K item widths, which is below what the antialiased clip
    /// edge resolves anyway.</summary>
    public const int SquircleSegments = 192;

    /// <summary>
    /// Traces the superellipse inscribed in the ellipse's bounding rect — inscribed in the
    /// <b>item rect</b>, so it stretches with the item's aspect exactly as
    /// <see cref="MaskShape.Circle"/> does, rather than staying square.
    /// </summary>
    /// <param name="xy">Receives <see cref="SquircleSegments"/> x/y pairs (so a length of
    /// <c>SquircleSegments * 2</c>), in order around the curve. The last point does not repeat the
    /// first — callers close the figure themselves.</param>
    public static void BuildSquircle(double centerX, double centerY, double radiusX, double radiusY, Span<double> xy)
    {
        if (xy.Length < SquircleSegments * 2)
            throw new ArgumentException($"needs room for {SquircleSegments} x/y pairs", nameof(xy));

        const double power = 2.0 / SquircleExponent;

        for (int i = 0; i < SquircleSegments; i++)
        {
            double t = 2 * Math.PI * i / SquircleSegments;
            double cos = Math.Cos(t);
            double sin = Math.Sin(t);

            xy[i * 2] = centerX + radiusX * Math.Sign(cos) * Math.Pow(Math.Abs(cos), power);
            xy[i * 2 + 1] = centerY + radiusY * Math.Sign(sin) * Math.Pow(Math.Abs(sin), power);
        }
    }
}
