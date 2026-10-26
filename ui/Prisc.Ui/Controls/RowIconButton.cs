using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The small flat icon button used at the right of a list row — the layers panel's eye/lock/delete
    /// and the timeline track headers' enable toggle. A plain <see cref="Button"/> wearing
    /// <c>RowIconButtonTheme</c> (Controls/RowIconButton.axaml) rather than a toggle: these buttons
    /// show their state by swapping the glyph (eye / eye-off), so a checked fill would say the same
    /// thing twice and make a row of icons read as a row of switches.
    /// </summary>
    internal static class RowIconButton
    {
        private static ControlTheme _theme;

        /// <summary>The shared theme, merged into the application resources on first use.</summary>
        public static ControlTheme Theme
        {
            get
            {
                if (_theme != null)
                    return _theme;

                ControlThemes.EnsureRegistered();
                var app = Application.Current;
                if (app != null && app.TryFindResource("RowIconButtonTheme", out var value))
                    _theme = value as ControlTheme;

                return _theme;
            }
        }

        /// <summary>A button of <paramref name="size"/> square carrying <paramref name="icon"/> at
        /// 60% of that (a <see cref="GlyphIcon"/>, not a stretched Path — see that class), with the
        /// theme's default 20px falling through when <paramref name="size"/> is not given.</summary>
        public static Button Build(Geometry icon, IBrush brush, string tip, double size = 20,
            double iconOpacity = 1.0)
        {
            var button = new Button
            {
                Theme = Theme,
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new GlyphIcon(icon, brush)
                {
                    Width = size * 0.6,
                    Height = size * 0.6,
                    Opacity = iconOpacity,
                },
            };

            if (tip != null)
                ToolTip.SetTip(button, tip);

            return button;
        }
    }
}
