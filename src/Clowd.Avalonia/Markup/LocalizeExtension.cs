using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Clowd.Localization.Resources;
using Avalonia.Data.Converters;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static Clowd.Localization.Resources.Strings;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Clowd.Avalonia.Markup
{
    public class LocalizeExtension : MarkupExtension
    {
        public string Key { get; set; }

        public string PluralPath { get; set; }

        public LocalizeExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var expr = new ReflectionBindingExtension($"[{Key}]")
            {
                Mode = BindingMode.OneWay,
                Source = Strings.Instance,
            };
            return expr.ProvideValue(serviceProvider);


            if (String.IsNullOrEmpty(PluralPath))
            {
               
            }

            var localStringBinding = new Binding($"[{Key}]")
            {
                Mode = BindingMode.OneWay,
                Source = Strings.Instance,
            };

            var valueBinding = new Binding(PluralPath) { Mode = BindingMode.OneWay };

            var m = new MultiBinding();
            m.Bindings.Add(localStringBinding);
            m.Bindings.Add(valueBinding);
            m.Converter = new MultiPluralValueConverter();
            return m;
        }

        public class MultiPluralValueConverter : IMultiValueConverter
        {
            public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            {
                try
                {
                    var format = values.First();
                    if (format is PluralableString plural)
                    {
                        var v = System.Convert.ToDouble(values[1]);
                        return plural[v];
                    }
                    else
                    {
                        return String.Format(format.ToString(), values.Skip(1));
                    }
                }
                catch
                { }

                return BindingOperations.DoNothing;
            }
        }
    }
}
