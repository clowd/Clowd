using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>The color dialog's tabs, in rail order.</summary>
    public enum ColorDialogTabKind
    {
        Simple,
        Image,
        Palette,
        OkLch,
    }

    /// <summary>
    /// Base for the color dialog's tabs. Each tab owns its own state (it is created once and only
    /// hidden while another tab is showing, so everything survives a trip round the rail) and
    /// reports one thing back to the window: its Selected color (brief §8). The top bar's Selected
    /// chip and what OK returns all follow the active tab's selection.
    /// </summary>
    /// <remarks>
    /// Layout contract: a tab fills the surface's content box (the window supplies the 20px
    /// padding and the footer), and lays itself out on the shared two-column grid —
    /// <c>ColumnDefinitions="260,24,*"</c> — so the wheel, the image and the palette bars land in the
    /// same place on every tab.
    /// </remarks>
    public abstract class ColorDialogTab : UserControl
    {
        private Color? _selectedColor;

        protected ColorDialogTab()
        {
            // the theme's UserControl clips to its bounds, and a wheel marker on the rim overhangs
            // the tab's top edge (the wheel is top-aligned): let it draw into the surface's padding
            ClipToBounds = false;
        }

        /// <summary>The window this tab is in, for the cross-tab actions (<see cref="ColorDialog.ShowInSimple"/>,
        /// <see cref="ColorDialog.ShowInPalette"/>, <see cref="ColorDialog.Accept"/>,
        /// <see cref="ColorDialog.CopyTextAsync"/>). Set by the window before it is shown.</summary>
        public ColorDialog Host { get; internal set; }

        /// <summary>This tab's Selected color, or null while it has none (the Image tab before an
        /// image is loaded). The window keeps showing the last selection it had while the active
        /// tab has none.</summary>
        public Color? SelectedColor => _selectedColor;

        /// <summary>Raised whenever <see cref="SelectedColor"/> changes, including live during a drag.</summary>
        public event EventHandler SelectedColorChanged;

        /// <summary>Call whenever the tab's selection changes; raises the event only on a real change.</summary>
        protected void SetSelectedColor(Color? color)
        {
            if (_selectedColor == color)
                return;

            _selectedColor = color;
            SelectedColorChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Called each time the rail makes this the visible tab.</summary>
        protected internal virtual void OnShown()
        { }

        /// <summary>Escape was pressed while this tab is showing. Return true to consume it — to
        /// end something transient, such as a ring drag or an open flyout — rather than let it
        /// cancel the dialog.</summary>
        protected internal virtual bool HandleEscape() => false;

        /// <summary>Whether the top bar's eyedropper samples into this tab. Where it does not (the
        /// Image tab, which has no one color to set) the window switches to Simple first.</summary>
        protected internal virtual bool AcceptsEyedropper => false;

        /// <summary>An eyedropper drag has begun: snapshot whatever a cancel must put back.</summary>
        protected internal virtual void BeginEyedropper()
        { }

        /// <summary>The color under the pointer, live while dragging (always opaque).</summary>
        protected internal virtual void PreviewEyedropper(Color sampled)
        { }

        /// <summary>The drag is over: <paramref name="picked"/> is the color to keep, or null to
        /// put back what <see cref="BeginEyedropper"/> saw.</summary>
        protected internal virtual void EndEyedropper(Color? picked)
        { }
    }

    /// <summary>
    /// Text on a colored fill (a palette bar's hex, its BASE tag and hover buttons): dark or white
    /// ink, whichever the fill's luminance calls for (brief §4). Not themed — it follows the color
    /// under it, whatever the window's theme.
    /// </summary>
    public static class ColorDialogInk
    {
        public static readonly IBrush Dark = new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
        public static readonly IBrush Light = new ImmutableSolidColorBrush(Colors.White);

        /// <summary>Whether <paramref name="fill"/> is light enough to want dark ink: WCAG relative
        /// luminance over 0.18, the point where dark and white ink have about equal contrast.</summary>
        public static bool IsLight(Color fill) => RelativeLuminance(fill) > 0.18;

        public static IBrush For(Color fill) => IsLight(fill) ? Dark : Light;

        public static double RelativeLuminance(Color color)
        {
            static double Channel(byte c)
            {
                var v = c / 255d;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
    }
}
