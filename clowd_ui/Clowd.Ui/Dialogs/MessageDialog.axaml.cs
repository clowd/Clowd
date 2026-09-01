using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.UI.Helpers;

namespace Clowd.UI.Dialogs
{
    /// <summary>Which button of a <see cref="MessageDialog"/> the user pressed.</summary>
    public enum MessageDialogChoice
    {
        /// <summary>The negative button, Escape, or Cmd+W.</summary>
        Cancel = 0,

        /// <summary>The affirmative (default-focused) button.</summary>
        Primary,

        /// <summary>The optional third button, offered between the other two.</summary>
        Alternate,
    }

    /// <summary>
    /// The single window backing all NiceDialog message prompts (notice / prompt / yes-no).
    /// Replaces the WinForms TaskDialog used by the WPF build (decision table #49).
    /// </summary>
    public partial class MessageDialog : Window
    {
        /// <summary>
        /// True when the affirmative button was clicked, false for the negative button or
        /// Escape, and null when the window was closed by other means. A click on the optional
        /// third button resolves to false here — <see cref="Choice"/> is what tells the two apart.
        /// </summary>
        public bool? Result { get; private set; }

        /// <summary>Which button closed the window, for the callers that offer three of them.
        /// Escape, Cmd+W and the negative button all leave it <see cref="MessageDialogChoice.Cancel"/>.</summary>
        public MessageDialogChoice Choice { get; private set; } = MessageDialogChoice.Cancel;

        /// <summary>
        /// State of the optional verification checkbox (the "don't ask again" opt-out of the WPF
        /// TaskDialog), false when the caller asked for no checkbox.
        /// </summary>
        public bool IsVerificationChecked => VerificationCheck.IsChecked == true;

        public MessageDialog() : this(NiceDialogIcon.None, "", null, "OK", null)
        { }

        public MessageDialog(NiceDialogIcon icon, string content, string mainInstruction,
                             string trueTxt, string falseTxt,
                             NiceDialogIcon footerIcon = NiceDialogIcon.None, string footerTxt = null,
                             string altTxt = null, string verificationTxt = null)
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
            TrueButton.Click += (_, _) => CloseWithResult(MessageDialogChoice.Primary);

            if (!string.IsNullOrWhiteSpace(altTxt))
            {
                // three labelled buttons do not fit the default width; only this shape widens.
                Width = 500;
                AltButton.Content = altTxt;
                AltButton.IsVisible = true;
                AltButton.Click += (_, _) => CloseWithResult(MessageDialogChoice.Alternate);
            }

            if (!string.IsNullOrWhiteSpace(falseTxt))
            {
                FalseButton.Content = falseTxt;
                FalseButton.IsVisible = true;
                FalseButton.Click += (_, _) => CloseWithResult(MessageDialogChoice.Cancel);
            }

            if (!string.IsNullOrWhiteSpace(verificationTxt))
            {
                VerificationCheck.Content = verificationTxt;
                VerificationCheck.IsVisible = true;
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

            // Cmd+W is the macOS close gesture — same "not true" resolution as Escape (issue #73)
            MacWindowShortcuts.AddCloseShortcut(this, () => CloseWithResult(MessageDialogChoice.Cancel));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // The WPF TaskDialog always had AllowCancel = true; Escape resolves to "not true".
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseWithResult(MessageDialogChoice.Cancel);
                return;
            }

            base.OnKeyDown(e);
        }

        private void CloseWithResult(MessageDialogChoice choice)
        {
            // only the affirmative button is "true": a two-button caller reading Result must not
            // see a third-button click as a yes.
            var result = choice == MessageDialogChoice.Primary;
            Choice = choice;
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
