using System;

namespace Clowd.Config
{
    /// <summary>
    /// Renders an enum settings property as the full-width status banner at the top of its
    /// settings page — one chip per member, the active one's title and caption written out large
    /// beside its icon — instead of as a labeled dropdown row. Meant for the one choice a page
    /// hinges on (the recording mode, or an on/off switch for a whole feature): the banner is what
    /// the rest of the page's rows are gated on through <see cref="VisibleWhenAttribute"/>. Each
    /// member's [Description] is its title, its <see cref="ModeCaptionAttribute"/> the caption
    /// underneath and its <see cref="ModeIconAttribute"/> the glyph in the banner's disc.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ModeSelectorAttribute : Attribute
    {
        /// <summary>
        /// The name of the feature the choice switches, as the page calls it ("Draw on Screen").
        /// Shown as the banner's eyebrow over the active member's title, which is the headline.
        /// Optional: without it the banner has no eyebrow.
        /// </summary>
        public string Feature { get; set; }
    }

    /// <summary>
    /// The caption shown under an enum member's title on a <see cref="ModeSelectorAttribute"/>
    /// banner. A member without one shows just its title.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ModeCaptionAttribute : Attribute
    {
        public string Caption { get; }

        public ModeCaptionAttribute(string caption)
        {
            Caption = caption;
        }
    }

    /// <summary>
    /// The glyph an enum member wears in a <see cref="ModeSelectorAttribute"/> banner's icon disc:
    /// the key of a geometry in Clowd.Ui's VectorIcons.axaml ("IconLayers"). A string rather than
    /// the geometry because this assembly holds no UI resources — Clowd.Ui looks the key up when it
    /// builds the banner. A member without one, or one naming a key that does not resolve, shows
    /// its title's initial in the disc instead.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ModeIconAttribute : Attribute
    {
        public string Key { get; }

        public ModeIconAttribute(string key)
        {
            Key = key;
        }
    }
}
