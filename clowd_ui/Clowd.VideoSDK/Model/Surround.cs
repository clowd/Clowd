namespace Clowd.VideoSDK.Model;

/// <summary>The surround styles the editor offers on a drawn item, in menu order.
/// <see cref="None"/> is stored as a null <see cref="Item.Surround"/> rather than a kind — "nothing
/// around it" keeps exactly one representation on disk, the same trade
/// <see cref="Transform.Crop"/> and the ramp transitions make.</summary>
public enum SurroundKind
{
    None,
    Shadow,
    Glow,
    Outline,
}

/// <summary>
/// What is drawn AROUND an item's silhouette: what it casts (a shadow, a glow) or wears (an
/// outline). Read by the picture draws (video/image items) and the themed cursor glyph; every other
/// content kind ignores it, and so does the <c>native</c> cursor style — the recorded cursor box
/// already carries the system cursor's own shadow.
///
/// <b>Not an effect</b>, deliberately: the name is reserved for what may later be applied to an
/// item's own pixels (a blur, a colour grade). A surround is strictly outside the silhouette, an
/// effect strictly inside it, and the two would want separate storage and separate UI anyway. Nor is
/// it related to the <i>effect items</i> (<see cref="SpeedContent"/> / <see cref="ZoomContent"/>) on
/// the pinned effect row, which re-time or zoom the whole output.
///
/// <b>Units.</b> <see cref="Size"/> and <see cref="Distance"/> are fractions of the item's drawn
/// reference extent — the shorter side of a picture's dest rect, or the glyph's drawn box for a
/// cursor — so a surround keeps its proportions at any canvas size, and preview and render agree by
/// construction. The light direction is fixed (down-right at 45°): one consistent light across a
/// composition is the point, so there is no per-item angle.
/// </summary>
public sealed class Surround
{
    /// <summary>Widest <see cref="Size"/>/<see cref="Distance"/> a project may hold. Half the
    /// item's extent is already far past useful; beyond it the blur costs real time for something
    /// nobody can read as a shadow.</summary>
    public const double MaxSize = 0.5;

    public const double MaxDistance = 0.5;

    /// <summary>Black at 50% — a shadow is an absence of light, not a colour, so the well starts
    /// where a user would leave it.</summary>
    public const uint DefaultShadowColor = 0x80000000;

    /// <summary>White at 75%: a halo that lifts the item off whatever is behind it. Translucent
    /// because a glow reads as light, and the alpha is <i>in</i> the colour (see
    /// <see cref="KeyboardContent.DefaultBackgroundColor"/> for the same trade).</summary>
    public const uint DefaultGlowColor = 0xC0FFFFFF;

    public const uint DefaultOutlineColor = 0xFFFFFFFF;

    /// <summary>Which of the three decorations this draws. Never
    /// <see cref="SurroundKind.None"/> on a stored surround — that is a null surround.</summary>
    public SurroundKind Kind { get; set; } = SurroundKind.Shadow;

    /// <summary>The decoration's colour, packed ARGB — alpha included, so how strongly the surround
    /// reads is this colour's own alpha.</summary>
    public uint Color { get; set; } = DefaultShadowColor;

    /// <summary>How far the decoration spreads, as a fraction of the item's reference extent: the
    /// blur radius for <see cref="SurroundKind.Shadow"/> and
    /// <see cref="SurroundKind.Glow"/>, the ring thickness for
    /// <see cref="SurroundKind.Outline"/>. Validated to 0..<see cref="MaxSize"/>.</summary>
    public double Size { get; set; } = 0.03;

    /// <summary>How far the shadow falls, as a fraction of the item's reference extent. Read by
    /// <see cref="SurroundKind.Shadow"/> alone — a glow and an outline sit on the item.
    /// Validated to 0..<see cref="MaxDistance"/>.</summary>
    public double Distance { get; set; } = 0.03;

    /// <summary>
    /// The numbers a freshly picked style starts at. They differ by <paramref name="cursor"/>
    /// because the reference extent does: a cursor glyph is drawn at tens of pixels and a picture
    /// at hundreds, so the fraction that reads as "a shadow" on one is a smear on the other.
    /// Nothing carries over when the user switches style — the fields mean different things per
    /// kind (a 10% shadow distance is not a 10% outline), so each style seeds its own.
    /// </summary>
    public static (uint Color, double Size, double Distance) DefaultsFor(SurroundKind kind, bool cursor)
        => kind switch
        {
            SurroundKind.Shadow => (DefaultShadowColor, cursor ? 0.06 : 0.03, cursor ? 0.10 : 0.03),
            SurroundKind.Glow => (DefaultGlowColor, cursor ? 0.12 : 0.04, 0),
            SurroundKind.Outline => (DefaultOutlineColor, cursor ? 0.05 : 0.01, 0),
            _ => (DefaultShadowColor, 0, 0),
        };

    /// <summary>A new surround of the given kind at its own defaults, or null for
    /// <see cref="SurroundKind.None"/>.</summary>
    public static Surround Create(SurroundKind kind, bool cursor)
    {
        if (kind == SurroundKind.None)
            return null;

        var (color, size, distance) = DefaultsFor(kind, cursor);
        return new Surround { Kind = kind, Color = color, Size = size, Distance = distance };
    }

    public Surround Clone() => new Surround
    {
        Kind = Kind,
        Color = Color,
        Size = Size,
        Distance = Distance,
    };
}
