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
using System.Windows.Threading;
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

        public SettingsPage()
        {
            InitializeComponent();
            this.SelectedItem = App.Current.Settings;
            this.Unloaded += SettingsPage_Unloaded;
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            PerformSave();
        }

        private void PerformSave()
        {
            SelectedItem.SaveQuiet();
            App.Current.SetupTrayContextMenu();
        }

        private void Home_Clicked(object sender, RoutedEventArgs e)
        {
            PerformSave();
            TemplatedWindow.SetContent(this, new HomePage());
        }

        private void ResetSettingsClick(object sender, MouseButtonEventArgs e)
        {
            App.Current.ResetSettings();
            this.SelectedItem = App.Current.Settings;
            PerformSave();
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
                var editor = new GlobalTriggerEditor();
                editor.SetBinding(GlobalTriggerEditor.TriggerProperty, property.CreateBinding());
                return editor;
            }

            return base.CreateControl(property, options);
        }

        protected override FrameworkElement CreateDirectoryPathControl(PropertyItem property)
        {
            var control = (DirectoryPicker)base.CreateDirectoryPathControl(property);
            control.FolderBrowserDialogService = new BetterFolderBrowseDialog();
            return control;
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

    public class BetterFolderBrowseDialog : IFolderBrowserDialogService
    {
        public bool ShowFolderBrowserDialog(ref string directory, bool showNewFolderButton = true, string description = null, bool useDescriptionForTitle = true)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            dialog.Description = description;
            dialog.UseDescriptionForTitle = useDescriptionForTitle; // This applies to the Vista style dialog only, not the old dialog.
            dialog.ShowNewFolderButton = showNewFolderButton;
            dialog.SelectedPath = directory;

            var success = (bool)dialog.ShowDialog();
            directory = dialog.SelectedPath;
            return success;
        }
    }
}
