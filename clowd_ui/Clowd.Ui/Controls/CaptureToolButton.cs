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

        /// <summary>
        /// Marks this button as a capture-source toggle (MIC / SPK / CAM), which is a claim about
        /// what it MEANS rather than how it looks: its two states are on and off, and the user has
        /// to be able to read which from across the desktop. The theme spends two things on that —
        /// a dimmed "off" glyph (the two glyphs otherwise differ by a slash alone, though the label
        /// stays fully opaque so the button is still identifiable) and a red/green light in the
        /// corner. <see cref="ShowAlternateIcon"/> is the state both of them read.
        /// </summary>
        public static readonly StyledProperty<bool> SourceToggleProperty =
            AvaloniaProperty.Register<CaptureToolButton, bool>(nameof(SourceToggle));

        public bool SourceToggle
        {
            get => GetValue(SourceToggleProperty);
            set => SetValue(SourceToggleProperty, value);
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
                // the capture accent, not the app theme's: these buttons are the overlay's button
                // panel rendered in Avalonia, and they carry white labels on the fill.
                Background = AppStyles.CaptureAccentBackgroundBrush;
            }
            else
            {
                // transparent — the host panel's gray backdrop shows through
                Background = Brushes.Transparent;
            }
        }

        // Matches the capture overlay's label rendering: Cascadia Code Regular 11px with a
        // 1.2 line height, white (clowd_capture ui/gpu/panel.rs LABEL_FONT_PX +
        // text.rs FAMILY_CODE). The ttf is the same file the overlay embeds.
        private static readonly FontFamily LabelFontFamily =
            new FontFamily("avares://Clowd.Ui/Assets/Fonts#Cascadia Code");

        private void OnTextChanged(string newValue)
        {
            var tb = new TextBlock();
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Bottom;
            tb.FontFamily = LabelFontFamily;
            tb.FontSize = 11;
            tb.LineHeight = 11 * 1.2;
            tb.FontWeight = FontWeight.Normal;
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
