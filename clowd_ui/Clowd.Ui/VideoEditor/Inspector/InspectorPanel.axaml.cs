using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing;
using Clowd.UI.Dialogs;
using Clowd.UI.Helpers;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Inspector
{
    /// <summary>
    /// The editor's right-hand property sidebar: the sections of
    /// <see cref="SelectedItemViewModel"/>, each shown only when the selected item has that kind of
    /// property. The panel binds to the view model (the window assigns it as this control's
    /// DataContext) — never to the session or the model, which the view model exists to mediate.
    ///
    /// The enum drop-downs are filled here rather than in XAML: the project builds with compiled
    /// bindings off and no x:DataType, so an <c>{x:Static}</c>-free code-behind assignment is the
    /// simplest thing that keeps the values and the bound property the same type. The font button
    /// and colour well are also driven from here — one previews its own typeface and the other
    /// needs a string→brush conversion, and both open pickers, which is imperative territory
    /// either way.
    /// </summary>
    public partial class InspectorPanel : UserControl
    {
        private SelectedItemViewModel _vm;

        public InspectorPanel()
        {
            InitializeComponent();

            comboAlign.ItemsSource = Enum.GetValues<TextAlign>();
            comboEntryKind.ItemsSource = Enum.GetValues<TransitionKind>();
            comboExitKind.ItemsSource = Enum.GetValues<TransitionKind>();
            comboEntryEasing.ItemsSource = Enum.GetValues<TransitionEasing>();
            comboExitEasing.ItemsSource = Enum.GetValues<TransitionEasing>();
            ddSpeed.ItemsSource = SelectedItemViewModel.SpeedOptions;

            // enum reset-dot defaults live here, not in XAML: an attribute would be the string
            // "None", which neither equality nor the reset write-back can hand to an enum binding
            dotEntryKind.DefaultValue = TransitionKind.None;
            dotExitKind.DefaultValue = TransitionKind.None;
            dotEntryEasing.DefaultValue = SelectedItemViewModel.DefaultTransitionEasing;
            dotExitEasing.DefaultValue = SelectedItemViewModel.DefaultTransitionEasing;
            dotSpeed.DefaultValue = SelectedItemViewModel.DefaultSpeedOption;

            btnFont.Click += async (_, _) => await PickFontAsync();
            btnDesync.Click += async (_, _) => await ConfirmDesyncAsync();
            colorWell.PointerPressed += ColorWell_PointerPressed;
            miniColor.Cancelled += (_, _) => colorPopup.IsOpen = false;

            DataContextChanged += (_, _) => AttachViewModel(DataContext as SelectedItemViewModel);
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // the mini picker's "pop out to the full dialog" needs an owner window
            miniColor.ParentWindow = TopLevel.GetTopLevel(this) as Window;
        }

        /// <summary>Puts the caret in the text card's own box, with the placeholder text selected so
        /// the first keystroke replaces it — where the toolbar's Add Text leaves the user. A no-op
        /// when the text section is not the one on show.</summary>
        public void FocusText()
        {
            if (!txtText.IsEffectivelyVisible)
                return;

            txtText.Focus();
            txtText.SelectAll();
        }

        private void AttachViewModel(SelectedItemViewModel vm)
        {
            if (_vm != null)
                _vm.PropertyChanged -= ViewModel_PropertyChanged;

            _vm = vm;

            if (_vm != null)
                _vm.PropertyChanged += ViewModel_PropertyChanged;

            RefreshFontButton();
            RefreshColorWell();
            RefreshTrackIcons();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SelectedItemViewModel.FontFamily):
                    RefreshFontButton();
                    break;
                case nameof(SelectedItemViewModel.TextColorHex):
                    RefreshColorWell();
                    break;
                case nameof(SelectedItemViewModel.TrackHidden):
                case nameof(SelectedItemViewModel.TrackMuted):
                    RefreshTrackIcons();
                    break;
            }
        }

        /// <summary>The font button previews its own answer: the family name rendered in that very
        /// face, or the placeholder for the renderer's default.</summary>
        private void RefreshFontButton()
        {
            var family = _vm?.FontFamily;
            var hasFamily = !String.IsNullOrWhiteSpace(family);

            txtFontName.Text = hasFamily ? family : "Default font";
            txtFontName.FontFamily = hasFamily ? FontUtil.CreateSafe(family) : FontFamily.Default;
        }

        private void RefreshColorWell()
        {
            var hex = _vm?.TextColorHex;
            txtColorHex.Text = String.IsNullOrEmpty(hex) ? "#FFFFFFFF" : hex;

            if (Color.TryParse(txtColorHex.Text, out var color))
                colorSwatch.Fill = new SolidColorBrush(color);
        }

        /// <summary>The hide/mute buttons flip their glyphs with the state (eye/eye-off,
        /// speaker/speaker-off) — the icon says what the track is, the press says what to do.</summary>
        private void RefreshTrackIcons()
        {
            btnHide.IconPath = TimelineIcons.Find(_vm?.TrackHidden == true ? "IconEyeOff" : "IconEye");
            btnMute.IconPath = TimelineIcons.Find(_vm?.TrackMuted == true ? "IconSpeakerDisabled" : "IconSpeakerEnabled");
        }

        private async System.Threading.Tasks.Task PickFontAsync()
        {
            var vm = _vm;
            if (vm == null || TopLevel.GetTopLevel(this) is not Window owner)
                return;

            var dialog = new FontDialog(
                String.IsNullOrWhiteSpace(vm.FontFamily) ? "Segoe UI" : vm.FontFamily,
                familyOnly: true);

            await dialog.ShowDialog<bool?>(owner);

            if (dialog.SelectedFont != null)
                vm.FontFamily = dialog.SelectedFont.TextFontFamilyName;
        }

        private void ColorWell_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            var vm = _vm;
            if (vm == null)
                return;

            if (!Color.TryParse(vm.TextColorHex ?? "", out var current))
                current = Colors.White;

            miniColor.Reset(current, c =>
                vm.TextColorHex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
            colorPopup.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>Desync is one-way, so it gets a real explanation and a yes/no — not a bare
        /// button that quietly severs the row from its recording.</summary>
        private async System.Threading.Tasks.Task ConfirmDesyncAsync()
        {
            var vm = _vm;
            if (vm == null)
                return;

            var confirmed = await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning,
                "This track's position in the timeline is currently synced with the original " +
                "recording: it can be split, trimmed and cropped, but not moved. Desyncing lets " +
                "you move it freely — but it cannot be re-linked, so it will be permanently out " +
                "of sync with the other tracks from this recording.",
                "Desync this object?");

            var command = (System.Windows.Input.ICommand)vm.CommandUnlink;
            if (confirmed && command.CanExecute(null))
                command.Execute(null);
        }
    }
}
