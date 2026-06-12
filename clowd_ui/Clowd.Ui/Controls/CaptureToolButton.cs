using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    public class CaptureToolButton : Button
    {
        public static readonly StyledProperty<Geometry> IconPathProperty =
            AvaloniaProperty.Register<CaptureToolButton, Geometry>(nameof(IconPath));

        public Geometry IconPath
        {
            get => GetValue(IconPathProperty);
            set => SetValue(IconPathProperty, value);
        }

        public static readonly StyledProperty<Geometry> IconPathAlternateProperty =
            AvaloniaProperty.Register<CaptureToolButton, Geometry>(nameof(IconPathAlternate));

        public Geometry IconPathAlternate
        {
            get => GetValue(IconPathAlternateProperty);
            set => SetValue(IconPathAlternateProperty, value);
        }

        public static readonly StyledProperty<Control> OverlayProperty =
            AvaloniaProperty.Register<CaptureToolButton, Control>(nameof(Overlay));

        public Control Overlay
        {
            get => GetValue(OverlayProperty);
            set => SetValue(OverlayProperty, value);
        }

        public static readonly StyledProperty<bool> PulseBackgroundProperty =
            AvaloniaProperty.Register<CaptureToolButton, bool>(nameof(PulseBackground));

        public bool PulseBackground
        {
            get => GetValue(PulseBackgroundProperty);
            set => SetValue(PulseBackgroundProperty, value);
        }

        public static readonly StyledProperty<bool> ShowAlternateIconProperty =
            AvaloniaProperty.Register<CaptureToolButton, bool>(nameof(ShowAlternateIcon));

        public bool ShowAlternateIcon
        {
            get => GetValue(ShowAlternateIconProperty);
            set => SetValue(ShowAlternateIconProperty, value);
        }

        public static readonly StyledProperty<bool> ShowHoverProperty =
            AvaloniaProperty.Register<CaptureToolButton, bool>(nameof(ShowHover), true);

        public bool ShowHover
        {
            get => GetValue(ShowHoverProperty);
            set => SetValue(ShowHoverProperty, value);
        }

        public static readonly StyledProperty<double> IconSizeProperty =
            AvaloniaProperty.Register<CaptureToolButton, double>(nameof(IconSize), 26d);

        public double IconSize
        {
            get => GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<CaptureToolButton, string>(nameof(Text));

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly StyledProperty<bool> PrimaryProperty =
            AvaloniaProperty.Register<CaptureToolButton, bool>(nameof(Primary));

        public bool Primary
        {
            get => GetValue(PrimaryProperty);
            set => SetValue(PrimaryProperty, value);
        }

        public bool IsDragHandle { get; set; }

        public List<SimpleKeyGesture> Gestures { get; set; } = new List<SimpleKeyGesture>();

        public event EventHandler Executed;

        static CaptureToolButton()
        {
            ControlThemes.EnsureRegistered();
        }

        public bool ProcessKeyState(KeyModifiers modifiers, Key key)
        {
            foreach (var g in Gestures)
            {
                if (g.Key == key && g.Modifiers == modifiers)
                {
                    Executed?.Invoke(this, new EventArgs());
                    return true;
                }
            }

            return false;
        }

        protected override void OnClick()
        {
            base.OnClick();
            Executed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == PrimaryProperty)
                OnPrimaryChanged(change.GetNewValue<bool>());
            else if (change.Property == TextProperty)
                OnTextChanged(change.GetNewValue<string>());
        }

        private void OnPrimaryChanged(bool newValue)
        {
            if (newValue)
            {
                Background = AppStyles.AccentBackgroundBrush;
            }
            else
            {
                Background = AppStyles.IdealBackgroundBrush;
            }
        }

        private void OnTextChanged(string newValue)
        {
            var tb = new TextBlock();
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Bottom;
            tb.FontSize = 10;
            tb.FontWeight = FontWeight.DemiBold;
            tb.Foreground = Brushes.White;

            if (newValue != null)
            {
                var upper = newValue.ToUpper();
                var idx = upper.IndexOf('_');
                if (idx >= 0)
                {
                    tb.Inlines.Add(upper.Substring(0, idx));
                    tb.Inlines.Add(new Run() { TextDecorations = TextDecorations.Underline, Text = upper.Substring(idx + 1, 1) });
                    tb.Inlines.Add(upper.Substring(idx + 2));
                }
                else
                {
                    tb.Inlines.Add(upper);
                }

                this.Content = tb;
            }
        }
    }
}
