using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Clowd.Config;
using Clowd.UI.Config;

namespace Clowd.UI.Pages
{
    public partial class GeneralSettingsPage : UserControl
    {
        public GeneralSettingsPage()
        {
            InitializeComponent();
            DataContext = SettingsRoot.Current.General;

            BindEnumCombo(ThemeCombo, nameof(SettingsGeneral.Theme), typeof(AppTheme));
            BindEnumCombo(TrayClickCombo, nameof(SettingsGeneral.TrayClick), typeof(TrayClickAction));
        }

        /// <summary>Enum ComboBox with [Description] display names, same as the generated
        /// settings pages (SettingsControlFactory).</summary>
        private void BindEnumCombo(ComboBox combo, string propertyName, Type enumType)
        {
            combo.ItemTemplate = new FuncDataTemplate<object>((o, ns) =>
                new TextBlock { Text = SettingsControlFactory.GetEnumDisplayString(o) });
            combo.ItemsSource = Enum.GetValues(enumType);
            combo.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(propertyName)
            {
                Mode = Avalonia.Data.BindingMode.TwoWay,
            });
        }
    }
}
