using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A two-part slot (spec §5): an icon toggle, an optional chevron half beside it, and an underline
    /// bar under the icon that says on/off at a glance and can also show a level.
    /// <para>
    /// The control <b>never flips <see cref="IsOn"/> itself</b>. A click only raises
    /// <see cref="ToggleClicked"/>; the owner decides what happens, and that is the whole reason this is
    /// not a <c>ToggleButton</c>. Its owners genuinely disagree about what a click means: one repaints
    /// immediately and waits for a remote acknowledgement to confirm it, another opens a device menu
    /// instead of turning anything on, and a third is a read-only status light. A control that flipped
    /// itself would have to be un-flipped by two of the three.
    /// </para>
    /// <para>
    /// Everything visual is a theme style keyed off the properties below (TraySplitToggle.axaml). The one
    /// value written from code is the level pill's width, because it is arithmetic on a live number rather
    /// than a state.
    /// </para>
    /// </summary>
    public class TraySplitToggle : TemplatedControl, ITrayOrientable
    {
        /// <summary>
        /// On/off state, written by the owner. Picks the glyph (<see cref="OnGlyph"/> /
        /// <see cref="OffGlyph"/>), its opacity (1 / .45) and the bar's colour (green / red).
        /// </summary>
        public static readonly StyledProperty<bool> IsOnProperty =
            AvaloniaProperty.Register<TraySplitToggle, bool>(nameof(IsOn));

        /// <summary>Whether the chevron half exists at all. False leaves the toggle alone in the slot
        /// (40 wide, all four corners rounded).</summary>
        public static readonly StyledProperty<bool> HasChevronProperty =
            AvaloniaProperty.Register<TraySplitToggle, bool>(nameof(HasChevron));

        /// <summary>Disables the chevron half alone — it dims and stops taking clicks while the toggle
        /// beside it stays live. Not the same as disabling the control, which dims the whole slot.</summary>
        public static readonly StyledProperty<bool> IsChevronEnabledProperty =
            AvaloniaProperty.Register<TraySplitToggle, bool>(nameof(IsChevronEnabled), true);

        /// <summary>The bar is a state light rather than a meter: always full width, never animated, and
        /// <see cref="Level"/> is ignored.</summary>
        public static readonly StyledProperty<bool> IsStatusOnlyProperty =
            AvaloniaProperty.Register<TraySplitToggle, bool>(nameof(IsStatusOnly));

        /// <summary>0..1 level for the bar while the toggle is on and metered. Null means "on, but no
        /// number to show": the pill sits at its 8 px minimum instead of freezing at the last sample.</summary>
        public static readonly StyledProperty<double?> LevelProperty =
            AvaloniaProperty.Register<TraySplitToggle, double?>(nameof(Level));

        public static readonly StyledProperty<TrayGlyph> OnGlyphProperty =
            AvaloniaProperty.Register<TraySplitToggle, TrayGlyph>(nameof(OnGlyph));

        public static readonly StyledProperty<TrayGlyph> OffGlyphProperty =
            AvaloniaProperty.Register<TraySplitToggle, TrayGlyph>(nameof(OffGlyph));

        /// <summary>Tooltip of the toggle half. The owner rewrites it as the state changes, so it names
        /// the state or the verb rather than the source.</summary>
        public static readonly StyledProperty<string> ToggleToolTipProperty =
            AvaloniaProperty.Register<TraySplitToggle, string>(nameof(ToggleToolTip));

        /// <summary>Tooltip of the chevron half. Free to grow a reason for its state ("… · locked while
        /// …"); <see cref="ChevronName"/> is the label that must not.</summary>
        public static readonly StyledProperty<string> ChevronToolTipProperty =
            AvaloniaProperty.Register<TraySplitToggle, string>(nameof(ChevronToolTip));

        /// <summary>Accessible name of the chevron half. Separate from <see cref="ChevronToolTip"/>
        /// because the two drift: a tooltip that explains why the chevron is inert is helpful, whereas an
        /// accessible name that changes wording as the state changes is a label an assistive client
        /// cannot rely on (spec §10 lists these names exactly).</summary>
        public static readonly StyledProperty<string> ChevronNameProperty =
            AvaloniaProperty.Register<TraySplitToggle, string>(nameof(ChevronName));

        /// <summary>The strip's axis, pushed in by the tray. Vertical stacks the chevron under the
        /// toggle; nothing is rebuilt, the template restyles itself.</summary>
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TraySplitToggle, Orientation>(nameof(Orientation), Orientation.Horizontal);

        private Button _toggle;
        private Button _chevron;
        private Border _pill;

        static TraySplitToggle()
        {
            ControlThemes.EnsureRegistered();
        }

        public bool IsOn
        {
            get => GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        public bool HasChevron
        {
            get => GetValue(HasChevronProperty);
            set => SetValue(HasChevronProperty, value);
        }

        public bool IsChevronEnabled
        {
            get => GetValue(IsChevronEnabledProperty);
            set => SetValue(IsChevronEnabledProperty, value);
        }

        public bool IsStatusOnly
        {
            get => GetValue(IsStatusOnlyProperty);
            set => SetValue(IsStatusOnlyProperty, value);
        }

        public double? Level
        {
            get => GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public TrayGlyph OnGlyph
        {
            get => GetValue(OnGlyphProperty);
            set => SetValue(OnGlyphProperty, value);
        }

        public TrayGlyph OffGlyph
        {
            get => GetValue(OffGlyphProperty);
            set => SetValue(OffGlyphProperty, value);
        }

        public string ToggleToolTip
        {
            get => GetValue(ToggleToolTipProperty);
            set => SetValue(ToggleToolTipProperty, value);
        }

        public string ChevronToolTip
        {
            get => GetValue(ChevronToolTipProperty);
            set => SetValue(ChevronToolTipProperty, value);
        }

        public string ChevronName
        {
            get => GetValue(ChevronNameProperty);
            set => SetValue(ChevronNameProperty, value);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        /// <summary>The toggle half was clicked. The state has NOT changed — see the class remarks.</summary>
        public event EventHandler ToggleClicked;

        /// <summary>The chevron half was clicked (the owner opens its menu).</summary>
        public event EventHandler ChevronClicked;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            if (_toggle != null)
                _toggle.Click -= OnToggleClick;
            if (_chevron != null)
                _chevron.Click -= OnChevronClick;

            base.OnApplyTemplate(e);

            _toggle = e.NameScope.Find<Button>("PART_Toggle");
            _chevron = e.NameScope.Find<Button>("PART_Chevron");
            _pill = e.NameScope.Find<Border>("_pill");

            if (_toggle != null)
                _toggle.Click += OnToggleClick;
            if (_chevron != null)
                _chevron.Click += OnChevronClick;

            UpdatePill();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsOnProperty || change.Property == LevelProperty ||
                change.Property == IsStatusOnlyProperty || change.Property == OrientationProperty)
                UpdatePill();

            // The toggle half's peer reads IsOn off this control on demand, but an assistive client only
            // re-reads when it is told to, so the push has to come from here — the one place that knows
            // the state moved.
            if (change.Property == IsOnProperty && _toggle is TrayToggleHalfButton half)
                half.NotifyToggleStateChanged(IsOn);
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            ToggleClicked?.Invoke(this, EventArgs.Empty);
        }

        private void OnChevronClick(object sender, RoutedEventArgs e)
        {
            ChevronClicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The pill's width — the one thing this control writes rather than styles, since it is a number
        /// and not a state. Off and status-only both mean "this bar is a light, not a meter": the pill
        /// fills the track and only its colour carries the state. An on, metered toggle maps its level
        /// over the track, floored at the 8 px minimum so a silent-but-live source still reads as live.
        /// A level above 1 simply runs past the track, which clips it.
        /// <para>
        /// The extent is the pill's Height in a row (the track stands up beside the icon and fills from
        /// the bottom) and its Width in a column (it lies under the icon and fills from the left); the
        /// other dimension is released to NaN so the theme's Stretch alignment fills it. Both are local
        /// values, which is why neither can live in a style.
        /// </para>
        /// </summary>
        private void UpdatePill()
        {
            if (_pill == null)
                return;

            var extent = !IsOn || IsStatusOnly
                ? TrayTokens.TrackWidth
                : Math.Max(TrayTokens.LevelMinWidth, (Level ?? 0) * TrayTokens.TrackWidth);

            if (Orientation == Orientation.Horizontal)
            {
                _pill.Width = double.NaN;
                _pill.Height = extent;
            }
            else
            {
                _pill.Height = double.NaN;
                _pill.Width = extent;
            }
        }
    }
}
