namespace Clowd.VideoSDK.Model;

/// <summary>The AI-assisted effects the editor offers on an item's own pixels, in menu order.
/// <see cref="None"/> is stored as a null <see cref="Item.Effect"/> rather than a kind — "no
/// effect" keeps exactly one representation on disk, the same trade
/// <see cref="SurroundKind.None"/> makes for the surround.</summary>
public enum VideoEffectKind
{
    None,

    /// <summary>Blur the whole item — no segmentation involved.</summary>
    Blur,

    /// <summary>Blur everything the person matte marks as background.</summary>
    BgBlur,

    /// <summary>Remove everything the person matte marks as background.</summary>
    BgRemove,
}

/// <summary>
/// What is applied to an item's OWN pixels — the other side of the silhouette from
/// <see cref="Surround"/>, which draws strictly outside it (see its remarks for why the two are
/// separate concepts with separate storage). Read by the picture draws (video/image items); every
/// other content kind ignores it.
///
/// <para>The background kinds consume a person matte generated offline into a sidecar file beside
/// the project (see <c>Ai.AiSidecars</c>); a missing or stale sidecar degrades to drawing the item
/// untouched rather than blocking playback or render.</para>
/// </summary>
public sealed class VideoEffect
{
    /// <summary>Where the blur dial starts: strong enough to read as a blur at a glance without
    /// erasing all context behind the subject.</summary>
    public const double DefaultAmount = 0.5;

    /// <summary>Which effect this applies. Never <see cref="VideoEffectKind.None"/> on a stored
    /// effect — that is a null effect.</summary>
    public VideoEffectKind Kind { get; set; } = VideoEffectKind.Blur;

    /// <summary>Blur strength, 0..1, for <see cref="VideoEffectKind.Blur"/> and
    /// <see cref="VideoEffectKind.BgBlur"/>; ignored by <see cref="VideoEffectKind.BgRemove"/>,
    /// which has no dial.</summary>
    public double Amount { get; set; } = DefaultAmount;

    /// <summary>Whether the kind needs the person matte sidecar — what separates a plain blur
    /// (a mapping change) from the segmented kinds (a structural change: the player's stream set
    /// gains or loses the matte).</summary>
    public static bool NeedsMatte(VideoEffectKind kind)
        => kind is VideoEffectKind.BgBlur or VideoEffectKind.BgRemove;

    /// <summary>A new effect of the given kind at its defaults, or null for
    /// <see cref="VideoEffectKind.None"/>.</summary>
    public static VideoEffect Create(VideoEffectKind kind)
    {
        if (kind == VideoEffectKind.None)
            return null;

        return new VideoEffect { Kind = kind, Amount = DefaultAmount };
    }

    public VideoEffect Clone() => new VideoEffect
    {
        Kind = Kind,
        Amount = Amount,
    };
}
