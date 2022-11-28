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
            m.ConverterParameter = Key;
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
            m.ConverterParameter = Key;
            return m;
        }
    }

    public class LocalizeEnumExtension : MarkupExtension
    {
        public StringsEnumKeys Key { get; set; }

        public string ValuePath { get; set; }

        public LocalizeEnumExtension(StringsEnumKeys key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var m = new MultiBinding();
            m.Bindings.Add(Strings.GetCultureChangedObservable().ToBinding());

            if (String.IsNullOrEmpty(ValuePath))
            {
                m.Bindings.Add(new Binding() { Mode = BindingMode.OneWay });
            }
            else
            {
                m.Bindings.Add(new Binding(ValuePath) { Mode = BindingMode.OneWay });
            }

            m.Converter = new MultiPluralValueConverter();
            m.ConverterParameter = Key;
            return m;
        }
    }

    public class MultiPluralValueConverter : IMultiValueConverter
    {
        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (parameter is StringsEnumKeys ek)
                {
                    if (values.Count > 1 && values[1] is not UnsetValueType)
                    {
                        var value = System.Convert.ToInt32(values[1]);
                        return Strings.GetEnum(ek, value);
                    }
                }
                else if (parameter is StringsPluralKeys ep)
                {
                    if (values.Count > 1 && values[1] is not UnsetValueType)
                    {
                        var value = System.Convert.ToDouble(values[1]);
                        return Strings.GetPlural(ep, value);
                    }
                }
                else if (parameter is StringsKeys sk)
                {
                    return Strings.GetString(sk);
                }
            }
            catch
            { }

            return BindingOperations.DoNothing;
        }
    }
}
