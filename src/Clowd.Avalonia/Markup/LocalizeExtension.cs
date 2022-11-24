using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml;
using Clowd.Localization.Resources;

namespace Clowd.Avalonia.Markup
{
    public class LocalizeExtension : MarkupExtension
    {
        public string Key { get; set; }

        //public LocalizeExtension()
        //{
        //}

        public LocalizeExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            //var path = new CompiledBindingPathBuilder()
            //new CompiledBindingExtension()

            var binding = new ReflectionBindingExtension($"[{Key}]")
            {
                Mode = BindingMode.OneWay,
                Source = Strings.Instance,
            };

            return binding.ProvideValue(serviceProvider);
        }
    }
}
