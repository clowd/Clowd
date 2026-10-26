using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Ursa.Controls;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The numeric field of the editor properties bar, in whichever chrome the host is wearing: a
    /// <see cref="CompactSpinner"/> (the dense, flat, 22px original) or a
    /// <see cref="ScrollableUrsaSpinner"/> (Ursa's numeric up/down, which is what the rest of the
    /// app's fields look like). Both live in the template; <see cref="IsModern"/> picks one, and
    /// the host sets it from a style — the editor's chrome class does it, so a field does not have
    /// to know the setting exists.
    /// <para>
    /// The two take their value differently. CompactSpinner has a DisplayScale of its own, so it is
    /// simply bound through in model units. Ursa has no such notion, so this control scales
    /// everything it hands over — value, bounds and step — and unscales what comes back, which
    /// keeps the conversion in one place rather than in an override of Ursa's text/parse pair.
    /// </para>
    /// </summary>
    public class ThemedSpinner : TemplatedControl
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<ThemedSpinner, double>(nameof(Value), 0d, defaultBindingMode: BindingMode.TwoWay);

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly StyledProperty<double> SpinAmountProperty =
            AvaloniaProperty.Register<ThemedSpinner, double>(nameof(SpinAmount), 1d);

        public double SpinAmount
        {
            get => GetValue(SpinAmountProperty);
            set => SetValue(SpinAmountProperty, value);
        }

        public static readonly StyledProperty<double?> MinProperty =
            AvaloniaProperty.Register<ThemedSpinner, double?>(nameof(Min));

        public double? Min
        {
            get => GetValue(MinProperty);
            set => SetValue(MinProperty, value);
        }

        public static readonly StyledProperty<double?> MaxProperty =
            AvaloniaProperty.Register<ThemedSpinner, double?>(nameof(Max));

        public double? Max
        {
            get => GetValue(MaxProperty);
            set => SetValue(MaxProperty, value);
        }

        public static readonly StyledProperty<bool> SnapToWholeNumberProperty =
            AvaloniaProperty.Register<ThemedSpinner, bool>(nameof(SnapToWholeNumber));

        public bool SnapToWholeNumber
        {
            get => GetValue(SnapToWholeNumberProperty);
            set => SetValue(SnapToWholeNumberProperty, value);
        }

        public static readonly StyledProperty<string> SuffixProperty =
            AvaloniaProperty.Register<ThemedSpinner, string>(nameof(Suffix));

        public string Suffix
        {
            get => GetValue(SuffixProperty);
            set => SetValue(SuffixProperty, value);
        }

        /// <summary>What the stored value is multiplied by to be shown — 100 for a zoom held as a
        /// 1.0 factor and read as "100 %".</summary>
        public static readonly StyledProperty<double> DisplayScaleProperty =
            AvaloniaProperty.Register<ThemedSpinner, double>(nameof(DisplayScale), 1d);

        public double DisplayScale
        {
            get => GetValue(DisplayScaleProperty);
            set => SetValue(DisplayScaleProperty, value);
        }

        /// <summary>Which of the two spinners to show. Set from the host's chrome, not read from
        /// settings here: a field in a Compact bar is Compact because the bar is, and nothing about
        /// that decision belongs to the field.</summary>
        public static readonly StyledProperty<bool> IsModernProperty =
            AvaloniaProperty.Register<ThemedSpinner, bool>(nameof(IsModern));

        public bool IsModern
        {
            get => GetValue(IsModernProperty);
            set => SetValue(IsModernProperty, value);
        }

        private CompactSpinner _compact;
        private ScrollableUrsaSpinner _modern;

        // Guards the round trip: pushing a scaled value into Ursa raises its ValueChanged, which
        // would otherwise unscale and write straight back over the value we are mid-way through
        // publishing.
        private bool _syncing;

        static ThemedSpinner()
        {
            ControlThemes.EnsureRegistered();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_modern != null)
                _modern.ValueChanged -= OnModernValueChanged;

            _compact = e.NameScope.Find<CompactSpinner>("PART_Compact");
            _modern = e.NameScope.Find<ScrollableUrsaSpinner>("PART_Modern");

            if (_compact != null)
            {
                // Model units throughout: the compact spinner does its own display scaling.
                Bind(_compact, CompactSpinner.ValueProperty, nameof(Value), BindingMode.TwoWay);
                Bind(_compact, CompactSpinner.SpinAmountProperty, nameof(SpinAmount));
                Bind(_compact, CompactSpinner.MinProperty, nameof(Min));
                Bind(_compact, CompactSpinner.MaxProperty, nameof(Max));
                Bind(_compact, CompactSpinner.SnapToWholeNumberProperty, nameof(SnapToWholeNumber));
                Bind(_compact, CompactSpinner.SuffixProperty, nameof(Suffix));
                Bind(_compact, CompactSpinner.DisplayScaleProperty, nameof(DisplayScale));
            }

            if (_modern != null)
                _modern.ValueChanged += OnModernValueChanged;

            SyncModern();
            SyncVisibility();
        }

        private void Bind(AvaloniaObject target, AvaloniaProperty property, string path,
                          BindingMode mode = BindingMode.OneWay)
            => target.Bind(property, new Binding(path) { Source = this, Mode = mode });

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsModernProperty)
                SyncVisibility();
            else if (change.Property == ValueProperty || change.Property == MinProperty ||
                     change.Property == MaxProperty || change.Property == SpinAmountProperty ||
                     change.Property == DisplayScaleProperty || change.Property == SuffixProperty ||
                     change.Property == SnapToWholeNumberProperty)
                SyncModern();
        }

        private void SyncVisibility()
        {
            if (_compact != null)
                _compact.IsVisible = !IsModern;
            if (_modern != null)
                _modern.IsVisible = IsModern;
        }

        private void OnModernValueChanged(object sender, ValueChangedEventArgs<double> e)
        {
            if (_syncing || e.NewValue is not { } shown)
                return;

            SetCurrentValue(ValueProperty, shown / Scale);
        }

        /// <summary>Everything Ursa is given is in display units, since that is the only unit it
        /// understands — scaling the step and the bounds along with the value is what keeps a notch
        /// of its spinner worth the same as a notch of the compact one.</summary>
        private void SyncModern()
        {
            if (_modern == null)
                return;

            var scale = Scale;
            var wasSyncing = _syncing;
            _syncing = true;

            try
            {
                // Ursa clamps against these, so they go in before the value does.
                _modern.Minimum = Min is { } min ? min * scale : double.MinValue;
                _modern.Maximum = Max is { } max ? max * scale : double.MaxValue;
                _modern.Step = SpinAmount * scale;
                _modern.SnapToWholeNumber = SnapToWholeNumber;

                // One decimal: the values here are a zoom percentage and a handful of pixel
                // counts, where a second decimal is noise that only costs the field width. A
                // whole-number field asks for none at all rather than trailing an empty ".0".
                _modern.FormatString = SnapToWholeNumber ? "0" : "0.#";

                // The unit sits beside the number rather than inside it, so nothing has to strip
                // it back off when parsing. A TextBlock rather than a bare string: handed a string,
                // the theme's presenter supplies its own inset, and the field is narrow enough that
                // the digits need every pixel of it back.
                _modern.InnerRightContent = string.IsNullOrEmpty(Suffix)
                    ? null
                    : new TextBlock
                    {
                        Text = Suffix,
                        // Negative, and deliberately so: the inset is inside the text box's own
                        // template, where a style selector cannot reach it (one /template/ per
                        // selector), and it is wide enough at this field size to cost the number a
                        // whole digit. Margins add, so this is the only handle on it from out here.
                        Margin = new Thickness(-10, 0, 0, 0),
                        Opacity = 0.6,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };

                _modern.Value = Value * scale;
            }
            finally
            {
                _syncing = wasSyncing;
            }
        }

        /// <summary>A zero or NaN scale would take every value with it, so it reads as "unscaled".
        /// </summary>
        private double Scale
        {
            get
            {
                var scale = DisplayScale;
                return scale == 0 || double.IsNaN(scale) ? 1d : scale;
            }
        }
    }
}
