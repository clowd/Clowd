using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml;
using Clowd.Localization.Resources;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace Clowd.Avalonia.Markup
{
    public class LocalizeExtension : MarkupExtension
    {
        public string Key { get; set; }

        public double? PluralValue { get; set; }

        //public LocalizeExtension()
        //{
        //}

        public LocalizeExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var expr = $"[{Key}]";
            if (PluralValue.HasValue)
            {
                expr += $"[{PluralValue.Value}]";
            }

            var binding = new ReflectionBindingExtension(expr)
            {
                Mode = BindingMode.OneWay,
                Source = Strings.Instance,
            };

            return binding.ProvideValue(serviceProvider);
        }
    }
}
