using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// How a <see cref="TrayButton"/> looks. Deliberately named after the look and never after the job
    /// the button does on a strip: this layer knows nothing about what any strip is for, so the
    /// destructive-looking one is <see cref="Danger"/> rather than the verb in its tooltip.
    /// </summary>
    public enum TrayButtonLook
    {
        /// <summary>Segment fill, white glyph. The default, and what most buttons use.</summary>
        Normal,

        /// <summary>
        /// The spec's "end" class: the same fill, resting at .85 opacity so a trailing
        /// leave/abandon button sits a step behind the buttons that do the work.
        /// </summary>
        Quiet,

        /// <summary><see cref="Quiet"/> with a red glyph and a red hover veil.</summary>
        Danger,

        /// <summary><see cref="Danger"/> that also carries a resting red veil, for a strip where the
        /// destructive button is the only way out and has to be found without hovering first.</summary>
        DangerFilled,
    }

    /// <summary>
    /// The tray's plain icon button: one 16 px glyph in a 40×40 slot with the tray radius.
    /// <para>
    /// Everything visual lives in <c>TrayButton.axaml</c>. This class is three styled properties and
    /// nothing else on purpose — a button whose fills are written from code cannot be styled, cannot
    /// animate, and drifts out of step with the rest of the tray the first time a call site forgets a
    /// case. <see cref="Look"/> and <see cref="IsActive"/> are the two facts the theme needs, and they
    /// are both declarative.
    /// </para>
    /// <para>
    /// <see cref="ContentControl.Content"/> is unused: the template has no content presenter. Labels are
    /// hidden on every shipped tray button except the primary (spec §2), so the owner spends
    /// <c>ToolTip.SetTip</c> and <c>AutomationProperties.SetName</c> on naming the button instead.
    /// </para>
    /// </summary>
    public class TrayButton : Button
    {
        /// <summary>The glyph to draw. Null renders an empty slot rather than throwing.</summary>
        public static readonly StyledProperty<TrayGlyph> GlyphProperty =
            AvaloniaProperty.Register<TrayButton, TrayGlyph>(nameof(Glyph));

        public static readonly StyledProperty<TrayButtonLook> LookProperty =
            AvaloniaProperty.Register<TrayButton, TrayButtonLook>(nameof(Look), TrayButtonLook.Normal);

        /// <summary>
        /// "This mode is on" — an accent fill with a white glyph. Not a toggle: the owner writes it,
        /// because on these strips the visible state follows a real-world fact (a mode that has been
        /// entered), not the click that asked for it.
        /// </summary>
        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<TrayButton, bool>(nameof(IsActive));

        /// <summary>The glyph's drawn size, <see cref="TrayTokens.IconSize"/> by default. A styled
        /// property rather than a template literal so a composite slot can style one half smaller
        /// (<see cref="TraySplitButton"/>'s side half draws at 12).</summary>
        public static readonly StyledProperty<double> GlyphSizeProperty =
            AvaloniaProperty.Register<TrayButton, double>(nameof(GlyphSize), TrayTokens.IconSize);

        static TrayButton()
        {
            ControlThemes.EnsureRegistered();
        }

        public TrayGlyph Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public TrayButtonLook Look
        {
            get => GetValue(LookProperty);
            set => SetValue(LookProperty, value);
        }

        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public double GlyphSize
        {
            get => GetValue(GlyphSizeProperty);
            set => SetValue(GlyphSizeProperty, value);
        }
    }
}
