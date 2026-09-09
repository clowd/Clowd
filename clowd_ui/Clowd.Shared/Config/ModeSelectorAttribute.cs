using System;

namespace Clowd.Config
{
    /// <summary>
    /// Renders an enum settings property as a row of large, captioned tiles — one per member — at
    /// the top of its settings page instead of as a labeled dropdown row. Meant for the one choice
    /// a page hinges on (the recording mode, or a future on/off switch for a whole feature): the
    /// tiles are what the rest of the page's rows are gated on through
    /// <see cref="VisibleWhenAttribute"/>. Each member's [Description] is its tile title and its
    /// <see cref="ModeCaptionAttribute"/> the caption underneath.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ModeSelectorAttribute : Attribute
    {
    }

    /// <summary>
    /// The caption shown under an enum member's title on a <see cref="ModeSelectorAttribute"/>
    /// tile. A member without one shows just its title.
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
}
