using System;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace Clowd.Localization
{
    /// <summary>
    /// <c>{loc:T Nav_About}</c> — a one-way binding to <c>Loc.Current["Nav_About"]</c> that re-reads
    /// itself when the language changes.
    /// <para>
    /// Usage: <c>xmlns:loc="using:Clowd.Localization"</c>, then
    /// <c>&lt;TextBlock Text="{loc:T Nav_About}" /&gt;</c>.
    /// </para>
    /// <para>
    /// The binding is built by hand as a *compiled* binding rather than returned as a reflection
    /// <c>Binding("[key]")</c>: the key is captured in a closure, so there is no path parsing at
    /// runtime and nothing for the trimmer to lose.
    /// </para>
    /// </summary>
    public class TExtension
    {
        public TExtension() { }

        public TExtension(string key)
        {
            Key = key;
        }

        /// <summary>Resource key, e.g. <c>Nav_About</c>. Keys are valid C# identifiers.</summary>
        public string Key { get; set; }

        public CompiledBindingExtension ProvideValue(IServiceProvider serviceProvider)
        {
            var key = Key;

            // A single-step path off Loc.Current: read the indexer for this key, and refresh
            // whenever Loc raises PropertyChanged("Item").
            var property = new ClrPropertyInfo(
                "Item",
                target => ((Loc)target)[key],
                setter: null,
                typeof(string));

            var path = new CompiledBindingPathBuilder()
                .Property(property, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
                .Build();

            return new CompiledBindingExtension(path)
            {
                Source = Loc.Current,
                Mode = BindingMode.OneWay,
            };
        }
    }
}
