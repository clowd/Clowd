using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A two-line readout that sits in the tray like any other child: line one states the fact
    /// ("Frame 22 · 8,180 px"), line two explains it or says what to do next. The tray has no room for a
    /// paragraph, so both lines are single-line and trim with an ellipsis rather than wrapping — a strip
    /// that grew a second row would move every button under the pointer.
    /// <para>
    /// The owner sets the width. This control deliberately does not measure its text: a block whose width
    /// followed its longest string would resize the whole strip every time the text changed, and a strip
    /// that has to be placed before it is shown needs a footprint that is known up front.
    /// </para>
    /// <para>
    /// Horizontal only, by omission: there is no Orientation here because the two stacked lines have no
    /// sensible rotated form (rotating them would put 200 px of prose down a 42 px column), and the only
    /// strips that carry a readout are the ones that do not rotate.
    /// </para>
    /// </summary>
    public class TrayStatusBlock : TemplatedControl
    {
        /// <summary>Line one: the fact. 11.5 px, semibold, tabular figures so counters do not jitter.</summary>
        public static readonly StyledProperty<string> PrimaryTextProperty =
            AvaloniaProperty.Register<TrayStatusBlock, string>(nameof(PrimaryText));

        /// <summary>Line two: the explanation. 10 px at .6 opacity. Either line may be null or empty.</summary>
        public static readonly StyledProperty<string> SecondaryTextProperty =
            AvaloniaProperty.Register<TrayStatusBlock, string>(nameof(SecondaryText));

        static TrayStatusBlock()
        {
            ControlThemes.EnsureRegistered();
        }

        public string PrimaryText
        {
            get => GetValue(PrimaryTextProperty);
            set => SetValue(PrimaryTextProperty, value);
        }

        public string SecondaryText
        {
            get => GetValue(SecondaryTextProperty);
            set => SetValue(SecondaryTextProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == PrimaryTextProperty || change.Property == SecondaryTextProperty)
                UpdateAutomationName();
        }

        /// <summary>
        /// A narrator gets one sentence, not two fragments: the split into two TextBlocks is a visual
        /// device, and announcing the lines separately reads as two unrelated announcements. The theme
        /// asks for polite live updates, so this name is re-read as the text changes — which is the point
        /// of a readout. An empty line drops out instead of leaving a dangling dash.
        /// </summary>
        private void UpdateAutomationName()
        {
            var primary = PrimaryText;
            var secondary = SecondaryText;

            var name = string.IsNullOrEmpty(primary)
                ? secondary
                : string.IsNullOrEmpty(secondary)
                    ? primary
                    : primary + " — " + secondary;

            AutomationProperties.SetName(this, name);
        }
    }
}
