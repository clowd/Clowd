using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.Controls;
using Clowd.Utilities;
using PropertyChanged;
using PropertyTools.Wpf;

namespace Clowd
{
    [ImplementPropertyChanged]
    public partial class SettingsPage : UserControl
    {
        public GeneralSettings SelectedItem { get; set; }

        private bool _closeRegistered;

        public SettingsPage()
        {
            InitializeComponent();
            this.SelectedItem = App.Current.Settings;
            // this is a work-around for now, to make sure the settings are saved when the window is closed.
            this.PreviewMouseDown += (s, e) => CheckRegistered();
            this.PreviewKeyDown += (s, e) => CheckRegistered();
        }

        private void CheckRegistered()
        {
            if (_closeRegistered)
                return;
            var win = TemplatedWindow.GetWindow(this);
            if (win != null)
            {
                _closeRegistered = true;
                win.Closed += (s, e) => SelectedItem.SaveQuiet();
            }

        }

        private void Home_Clicked(object sender, RoutedEventArgs e)
        {
            SelectedItem.SaveQuiet();
            TemplatedWindow.SetContent(this, new HomePage());
        }
    }

    public class DisplayModeToPropertyTabVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var val = (SettingsDisplayMode)value;
            if (val == SettingsDisplayMode.SinglePage)
                return PropertyTools.Wpf.TabVisibility.Collapsed;
            else
                return PropertyTools.Wpf.TabVisibility.VisibleIfMoreThanOne;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class AppSettingsItemFactory : DefaultPropertyItemFactory
    {
        protected override IEnumerable<PropertyItem> CreatePropertyItems(object instance, IPropertyGridOptions options)
        {
            var instanceType = instance.GetType();
            var properties = TypeDescriptor.GetProperties(instance);
            foreach (PropertyDescriptor pd in properties)
            {
                if (options.ShowDeclaredOnly && pd.ComponentType != instanceType)
                    continue;
                if (!pd.IsBrowsable())
                    continue;
                if (!options.ShowReadOnlyProperties && pd.IsReadOnly())
                    continue;
                if (options.RequiredAttribute != null && pd.GetFirstAttributeOrDefault(options.RequiredAttribute) == null)
                    continue;

                ExpandAsCategoryAttribute expanded;
                if (Type.GetTypeCode(pd.PropertyType) == TypeCode.Object
                    && (expanded = pd.GetFirstAttributeOrDefault<ExpandAsCategoryAttribute>()) != null)
                {
                    foreach (var xProp in this.CreatePropertyItems(pd.GetValue(instance), options))
                    {
                        if (xProp.Category == "Misc")
                        {
                            xProp.Category = xProp.Tab = expanded.Category;
                        }
                        yield return xProp;
                    }
                }
                else yield return this.CreatePropertyItem(pd, properties, instance);
            }
        }

        public override PropertyItem CreatePropertyItem(PropertyDescriptor pd, PropertyDescriptorCollection propertyDescriptors, object instance)
        {
            var b = base.CreatePropertyItem(pd, propertyDescriptors, instance);
            b.Tab = b.Category;
            return b;
        }
    }

    public class AppSettingsControlFactory : DefaultPropertyControlFactory
    {
        public override FrameworkElement CreateControl(PropertyItem property, PropertyControlFactoryOptions options)
        {
            if (property.Is(typeof(GlobalTrigger)))
            {
                var editor = new KeyGestureEditor();
                editor.SetBinding(KeyGestureEditor.TriggerProperty, property.CreateBinding());
                return editor;
            }

            return base.CreateControl(property, options);
        }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class ExpandAsCategoryAttribute : Attribute
    {
        public virtual string Category { get; private set; }
        public ExpandAsCategoryAttribute(string category)
        {
            Category = category;
        }
    }
}
