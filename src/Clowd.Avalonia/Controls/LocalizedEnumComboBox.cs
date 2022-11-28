using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Clowd.Avalonia.Markup;
using Clowd.Localization;
using DependencyPropertyGenerator;

namespace Clowd.Avalonia.Controls
{
    [DependencyProperty<StringsEnumKeys>("ResourceKey")]
    public partial class LocalizedEnumComboBox : ComboBox
    {
        public LocalizedEnumComboBox()
        {
            KeyUpdated();
        }

        partial void OnResourceKeyChanged()
        {
            KeyUpdated();
        }

        private void KeyUpdated()
        {
            var newValue = ResourceKey;
            Items = Strings.GetEnumValues(newValue);

            // from LocalizeExtesion.cs
            var m = new MultiBinding();
            m.Bindings.Add(Strings.GetCultureChangedObservable().ToBinding());
            m.Bindings.Add(new Binding() { Mode = BindingMode.OneWay });
            m.Converter = new MultiPluralValueConverter();
            m.ConverterParameter = newValue;

            ItemTemplate = new FuncDataTemplate<object>((value, namescope) =>
                new TextBlock
                {
                    [!TextBlock.TextProperty] = m,
                });
        }
    }
}
