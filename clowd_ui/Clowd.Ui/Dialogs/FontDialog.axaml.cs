using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.UI.Helpers;

namespace Clowd.UI.Dialogs
{
    /// <summary>
    /// Custom font picker replacing the WinForms FontDialog (decision table #49): system font
    /// family list + size + bold/italic toggles + live preview. Sizes intentionally stay in px
    /// (the editor's native unit) — no px↔pt conversion is performed (§4 WP11).
    /// </summary>
    public partial class FontDialog : Window
    {
        /// <summary>Non-null only after the dialog was confirmed with OK.</summary>
        public NiceDialog.SelectedFont SelectedFont { get; private set; }

        public FontDialog() : this("Segoe UI", 12, FontStyle.Normal, FontWeight.Normal)
        { }

        public FontDialog(string fontFamily, double fontSize, FontStyle fontStyle, FontWeight fontWeight)
        {
            InitializeComponent();
            Icon = AppStyles.AppIcon;

            var names = FontManager.Current.SystemFonts
                                   .Select(f => f.Name)
                                   .Where(n => !string.IsNullOrWhiteSpace(n))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            FontList.ItemsSource = names;

            var match = names.FirstOrDefault(n => string.Equals(n, fontFamily, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                FontList.SelectedItem = match;

            // The WPF FontDialog enforced MinSize 8 / MaxSize 64 — mirrored by the NumericUpDown.
            SizeBox.Value = (decimal)Math.Clamp(double.IsFinite(fontSize) ? fontSize : 12d, 8d, 64d);

            // Like WPF (ToOpenTypeWeight() > 400), anything heavier than Normal lights up Bold.
            BoldToggle.IsChecked = fontWeight > FontWeight.Normal;
            ItalicToggle.IsChecked = fontStyle == FontStyle.Italic;

            FontList.SelectionChanged += (_, _) => UpdatePreview();
            SizeBox.ValueChanged += (_, _) => UpdatePreview();
            BoldToggle.IsCheckedChanged += (_, _) => UpdatePreview();
            ItalicToggle.IsCheckedChanged += (_, _) => UpdatePreview();

            OkButton.Click += (_, _) => Confirm();
            CancelButton.Click += (_, _) => Close(false);

            Opened += (_, _) =>
            {
                if (FontList.SelectedItem != null)
                    FontList.ScrollIntoView(FontList.SelectedItem);
            };

            UpdatePreview();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(false);
                return;
            }

            base.OnKeyDown(e);
        }

        private double CurrentSize => (double)(SizeBox.Value ?? 12);

        private FontStyle CurrentStyle => ItalicToggle.IsChecked == true ? FontStyle.Italic : FontStyle.Normal;

        private FontWeight CurrentWeight => BoldToggle.IsChecked == true ? FontWeight.Bold : FontWeight.Normal;

        private void UpdatePreview()
        {
            if (FontList.SelectedItem is string family && !string.IsNullOrWhiteSpace(family))
                PreviewText.FontFamily = new FontFamily(family);

            PreviewText.FontSize = CurrentSize;
            PreviewText.FontWeight = CurrentWeight;
            PreviewText.FontStyle = CurrentStyle;
        }

        private void Confirm()
        {
            var family = FontList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(family))
            {
                Close(false);
                return;
            }

            SelectedFont = new NiceDialog.SelectedFont
            {
                TextFontFamilyName = family,
                TextFontSize = CurrentSize,
                TextFontStyle = CurrentStyle,
                TextFontWeight = CurrentWeight,
            };

            Close(true);
        }
    }
}
