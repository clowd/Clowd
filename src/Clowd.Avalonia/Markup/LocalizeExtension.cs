using System;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Clowd.Localization;
using System.Collections.Generic;
using System.Globalization;

namespace Clowd.Avalonia.Markup
{
    public class LocalizeExtension : MarkupExtension
    {
        public StringsKeys Key { get; set; }

        public LocalizeExtension(StringsKeys key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var m = new MultiBinding();
            m.Bindings.Add(Strings.GetCultureChangedObservable().ToBinding());
            m.Converter = new MultiPluralValueConverter();
            m.ConverterParameter = Key.ToString();
            return m;
        }
    }

    public class LocalizePluralExtension : MarkupExtension
    {
        public StringsPluralKeys Key { get; set; }

        public string ValuePath { get; set; }

        public LocalizePluralExtension(StringsPluralKeys key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var m = new MultiBinding();
            m.Bindings.Add(Strings.GetCultureChangedObservable().ToBinding());
            m.Bindings.Add(new Binding(ValuePath) { Mode = BindingMode.OneWay });
            m.Converter = new MultiPluralValueConverter();
            m.ConverterParameter = Key.ToString();
            return m;
        }
    }

    public class MultiPluralValueConverter : IMultiValueConverter
    {
        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var key = (string)parameter;
                if (values.Count > 1)
                {
                    var cultu = values[0] as string;
                    var value = System.Convert.ToDouble(values[1]);
                    return Strings.GetPlural(key, value);
                }
                else
                {
                    return Strings.GetString(key);
                }
            }
            catch
            { }

            return BindingOperations.DoNothing;
        }
    }
}
