using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// What a transport button can be: nothing has started yet, something is running, it is held, or
    /// the thing behind it has been asked to start and has not answered yet.
    /// </summary>
    public enum TrayPrimaryState
    {
        Idle,
        Active,
        Paused,
        Waiting,
    }

    /// <summary>
    /// The tray's one wide button: a transport control. It knows the four transport states, a label
    /// string and the three media glyphs (record dot, pause bars, play triangle) — and deliberately
    /// nothing about what is being transported, which is what lets any strip use it.
    /// <para>
    /// Two rules of the design spec are load-bearing and are why this is a control rather than a
    /// composed template at each call site. First, the width is fixed (60 px horizontally): the label
    /// changes on every timer tick, and if the button could grow the whole strip would twitch once a
    /// second. Second, at rest the glyph is the <em>status</em> and on hover it is the <em>verb</em>
    /// (running shows a pulsing dot, and offers pause), so there are two glyph layers in one slot
    /// sized so the label does not shift when they swap.
    /// </para>
    /// </summary>
    public class TrayPrimaryButton : Button, ITrayOrientable
    {
        public static readonly StyledProperty<TrayPrimaryState> StateProperty =
            AvaloniaProperty.Register<TrayPrimaryButton, TrayPrimaryState>(nameof(State), TrayPrimaryState.Idle);

        /// <summary>
        /// The owner's words — "Start", "00:00", "Wait…". The control never composes or formats this:
        /// only the owner knows whether its transport counts up, down, or not at all.
        /// </summary>
        public static readonly StyledProperty<string> LabelProperty =
            AvaloniaProperty.Register<TrayPrimaryButton, string>(nameof(Label));

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayPrimaryButton, Orientation>(nameof(Orientation), Orientation.Horizontal);

        static TrayPrimaryButton()
        {
            ControlThemes.EnsureRegistered();
        }

        public TrayPrimaryState State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        public string Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == StateProperty)
            {
                // The one thing the state cannot express as a style: Waiting must not be clickable, because
                // the recorder (or whatever the owner is waiting on) has already been asked to start and a
                // second press would ask again. It keeps full opacity and its tooltip all the same — the
                // theme re-asserts Opacity 1 after the :disabled dim — so it reads as "busy", not "broken".
                IsEnabled = State != TrayPrimaryState.Waiting;
            }
        }
    }
}
