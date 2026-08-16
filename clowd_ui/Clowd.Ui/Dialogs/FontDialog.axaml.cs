using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing;
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

        private readonly System.Collections.Generic.List<string> _allFamilies;

        /// <summary>
        /// Family-only variant, for callers whose model stores just a face name (the video
        /// editor's text card): the size/bold/italic column is hidden — showing controls whose
        /// values would be thrown away would be a lie — and the preview renders at a fixed
        /// comfortable size.
        /// </summary>
        public FontDialog(string fontFamily, bool familyOnly)
            : this(fontFamily, 16, FontStyle.Normal, FontWeight.Normal)
        {
            if (!familyOnly)
                return;

            SizeStyleHeader.IsVisible = false;
            SizeStylePanel.IsVisible = false;
        }

        public FontDialog(string fontFamily, double fontSize, FontStyle fontStyle, FontWeight fontWeight)
        {
            InitializeComponent();
            Icon = AppStyles.AppIcon;

            // a third-party font with broken metadata can carry a name Avalonia's typeface
            // parser rejects at render time (CLOWD-10) — drop those rather than list them
            _allFamilies = FontManager.Current.SystemFonts
                                      .Select(f => f.Name?.Trim())
                                      .Where(FontUtil.IsSafeFamilyName)
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                      .ToList();

            // each family renders in its own typeface as a mini preview
            FontList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((name, _) =>
                new TextBlock
                {
                    Text = name,
                    FontFamily = FontUtil.CreateSafe(name),
                });

            FontList.ItemsSource = _allFamilies;

            var match = _allFamilies.FirstOrDefault(n => string.Equals(n, fontFamily, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                FontList.SelectedItem = match;

            FilterBox.TextChanged += (_, _) => ApplyFilter();

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

            // Cmd+W is the macOS close gesture — cancels, same as Escape (issue #73)
            MacWindowShortcuts.AddCloseShortcut(this, () => Close(false));
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

        /// <summary>Narrows the family list to names containing the filter text, keeping the
        /// current selection when it survives the filter.</summary>
        private void ApplyFilter()
        {
            var filter = FilterBox.Text?.Trim();
            var selected = FontList.SelectedItem as string;

            var filtered = string.IsNullOrEmpty(filter)
                ? _allFamilies
                : _allFamilies.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            FontList.ItemsSource = filtered;

            if (selected != null && filtered.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                FontList.SelectedItem = selected;
                FontList.ScrollIntoView(selected);
            }
        }

        private double CurrentSize => (double)(SizeBox.Value ?? 12);

        private FontStyle CurrentStyle => ItalicToggle.IsChecked == true ? FontStyle.Italic : FontStyle.Normal;

        private FontWeight CurrentWeight => BoldToggle.IsChecked == true ? FontWeight.Bold : FontWeight.Normal;

        private void UpdatePreview()
        {
            if (FontList.SelectedItem is string family && !string.IsNullOrWhiteSpace(family))
                PreviewText.FontFamily = FontUtil.CreateSafe(family);

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
