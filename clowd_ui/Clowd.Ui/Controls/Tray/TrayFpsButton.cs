using Avalonia;
using Avalonia.Controls;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The frame-rate tile at the head of a strip: a small caption ("FPS") over a bold number, drawn
    /// straight on the tray with no segment fill so it reads as part of the chassis, like the emblem
    /// beside it — but it hovers, presses and clicks like a button until <see cref="IsReadout"/> says
    /// otherwise.
    /// <para>
    /// The tile has two lives, and the owner picks which with one property. As a button
    /// (<c>IsReadout=false</c>) the number is a TARGET the user is choosing — a click cycles it — so
    /// it carries the hand cursor, the hover veil and the press. As a readout (<c>IsReadout=true</c>)
    /// the number is a MEASUREMENT (what the recorder actually achieved this second), a click would
    /// mean nothing, and so the tile stops being one: no cursor change, no hover, no press, and
    /// <see cref="OnClick"/> is swallowed. It is deliberately not <c>IsEnabled=false</c>: a disabled
    /// control dims to say "you cannot do this right now", and a live readout is not a locked
    /// button, it is a different thing in the same place.
    /// </para>
    /// <para>
    /// Everything visual lives in <c>TrayFpsButton.axaml</c>. The layout is the same stacked pair in
    /// a row and in a column (the tile is a 40 px square either way), so unlike the primary this
    /// control needs no orientation of its own.
    /// </para>
    /// </summary>
    public class TrayFpsButton : Button
    {
        /// <summary>The small upper line. "FPS" on every shipped strip; a property rather than a
        /// literal in the template so the spike harness and a future tile can say something else.</summary>
        public static readonly StyledProperty<string> CaptionProperty =
            AvaloniaProperty.Register<TrayFpsButton, string>(nameof(Caption), "FPS");

        /// <summary>The bold lower line: the target rate as a button, the measured rate as a readout.
        /// Already formatted — the tile does no arithmetic.</summary>
        public static readonly StyledProperty<string> ValueProperty =
            AvaloniaProperty.Register<TrayFpsButton, string>(nameof(Value));

        /// <summary>True turns the button into a label: same face, no hover, no press, no hand
        /// cursor, and clicks go nowhere.</summary>
        public static readonly StyledProperty<bool> IsReadoutProperty =
            AvaloniaProperty.Register<TrayFpsButton, bool>(nameof(IsReadout));

        static TrayFpsButton()
        {
            ControlThemes.EnsureRegistered();
        }

        public string Caption
        {
            get => GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }

        public string Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public bool IsReadout
        {
            get => GetValue(IsReadoutProperty);
            set => SetValue(IsReadoutProperty, value);
        }

        /// <summary>A readout is not clickable, whatever the pointer or the keyboard does: the
        /// theme takes away the visual affordances and this takes away the event, so an owner never
        /// has to re-check the mode inside its Click handler.</summary>
        protected override void OnClick()
        {
            if (IsReadout)
                return;

            base.OnClick();
        }
    }
}
