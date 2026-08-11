using System;
using Avalonia.Controls;
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
    /// simplest thing that keeps the values and the bound property the same type.
    /// </summary>
    public partial class InspectorPanel : UserControl
    {
        public InspectorPanel()
        {
            InitializeComponent();

            comboAlign.ItemsSource = Enum.GetValues<TextAlign>();
            comboEntryKind.ItemsSource = Enum.GetValues<TransitionKind>();
            comboExitKind.ItemsSource = Enum.GetValues<TransitionKind>();
            comboEntryEasing.ItemsSource = Enum.GetValues<TransitionEasing>();
            comboExitEasing.ItemsSource = Enum.GetValues<TransitionEasing>();
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
    }
}
