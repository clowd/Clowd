using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.UI.Helpers;

namespace Clowd.UI.Dialogs
{
    /// <summary>
    /// The single window backing all NiceDialog message prompts (notice / prompt / yes-no).
    /// Replaces the WinForms TaskDialog used by the WPF build (decision table #49).
    /// </summary>
    public partial class MessageDialog : Window
    {
        /// <summary>
        /// True when the affirmative button was clicked, false for the negative button or
        /// Escape, and null when the window was closed by other means.
        /// </summary>
        public bool? Result { get; private set; }

        public MessageDialog() : this(NiceDialogIcon.None, "", null, "OK", null)
        { }

        public MessageDialog(NiceDialogIcon icon, string content, string mainInstruction,
                             string trueTxt, string falseTxt,
                             NiceDialogIcon footerIcon = NiceDialogIcon.None, string footerTxt = null)
        {
            InitializeComponent();
            Icon = AppStyles.AppIcon;

            ContentText.Text = content ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(mainInstruction))
            {
                MainInstructionText.Text = mainInstruction;
                MainInstructionText.Foreground = new SolidColorBrush(AppStyles.AccentColor);
                MainInstructionText.IsVisible = true;
            }

            var iconInfo = GetIconInfo(icon);
            if (iconInfo != null)
            {
                IconHost.Background = new SolidColorBrush(iconInfo.Value.Color);
                IconGlyph.Text = iconInfo.Value.Glyph;
                IconHost.IsVisible = true;
            }

            TrueButton.Content = string.IsNullOrWhiteSpace(trueTxt) ? "OK" : trueTxt;
            TrueButton.Click += (_, _) => CloseWithResult(true);

            if (!string.IsNullOrWhiteSpace(falseTxt))
            {
                FalseButton.Content = falseTxt;
                FalseButton.IsVisible = true;
                FalseButton.Click += (_, _) => CloseWithResult(false);
            }

            if (!string.IsNullOrWhiteSpace(footerTxt))
            {
                FooterText.Text = footerTxt;
                var footerInfo = GetIconInfo(footerIcon);
                if (footerInfo != null)
                    FooterIconShape.Fill = new SolidColorBrush(footerInfo.Value.Color);
                else
                    FooterIconShape.IsVisible = false;
                FooterPanel.IsVisible = true;
            }

            Opened += (_, _) => TrueButton.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // The WPF TaskDialog always had AllowCancel = true; Escape resolves to "not true".
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseWithResult(false);
                return;
            }

            base.OnKeyDown(e);
        }

        private void CloseWithResult(bool result)
        {
            Result = result;
            Close(result);
        }

        private readonly record struct IconInfo(string Glyph, Color Color);

        private static IconInfo? GetIconInfo(NiceDialogIcon icon)
        {
            return icon switch
            {
                NiceDialogIcon.Information => new IconInfo("i", Color.FromRgb(0x00, 0x78, 0xD7)),
                NiceDialogIcon.Warning => new IconInfo("!", Color.FromRgb(0xE8, 0xA0, 0x00)),
                NiceDialogIcon.Error => new IconInfo("✕", Color.FromRgb(0xE8, 0x11, 0x23)),
                NiceDialogIcon.Shield => new IconInfo("!", Color.FromRgb(0x00, 0x78, 0xD7)),
                NiceDialogIcon.ShieldBlueBar => new IconInfo("!", Color.FromRgb(0x00, 0x78, 0xD7)),
                NiceDialogIcon.ShieldGrayBar => new IconInfo("!", Color.FromRgb(0x76, 0x76, 0x76)),
                NiceDialogIcon.ShieldWarningYellowBar => new IconInfo("!", Color.FromRgb(0xE8, 0xA0, 0x00)),
                NiceDialogIcon.ShieldErrorRedBar => new IconInfo("!", Color.FromRgb(0xE8, 0x11, 0x23)),
                NiceDialogIcon.ShieldSuccessGreenBar => new IconInfo("✓", Color.FromRgb(0x10, 0x7C, 0x10)),
                _ => null,
            };
        }
    }
}
