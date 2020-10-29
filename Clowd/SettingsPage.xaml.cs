using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
using Clowd.Installer.Features;
using Clowd.Utilities;
using PropertyChanged;
using PropertyTools.DataAnnotations;
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
        }

        private void Home_Clicked(object sender, RoutedEventArgs e)
        {
            TemplatedWindow.SetContent(this, new HomePage());
        }

        private void ResetSettingsClick(object sender, MouseButtonEventArgs e)
        {
            App.Current.ResetSettings();
            this.SelectedItem = App.Current.Settings;
            SelectedItem.SaveQuiet();
        }

        public void SetCurrentTab(SettingsCategory category)
        {
            this.PropertyGrid1.SelectedTabId = category.ToString();
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
            FrameworkElement SimpleControlBinding(FrameworkElement control, DependencyProperty dependency)
            {
                var bind = property.CreateBinding();
                bind.Source = property.TargetObject;
                control.SetBinding(dependency, bind);
                return control;
            }

            FrameworkElement ComboSelectBinding(Func<System.Collections.IEnumerable> items, string displayPath, bool canClear = true)
            {
                var bind = property.CreateBinding();
                bind.Source = property.TargetObject;

                var panel = new DockPanel();
                var reset = new Button();
                var combo = new ComboBox();

                if (canClear)
                {
                    DockPanel.SetDock(reset, Dock.Right);
                    reset.Margin = new Thickness(5, 0, 0, 0);
                    reset.Padding = new Thickness(10, 0, 10, 0);
                    reset.Content = "Clear";
                    reset.Click += (s, e) => { combo.SelectedIndex = -1; };
                    panel.Children.Add(reset);
                }

                combo.DisplayMemberPath = displayPath;
                combo.ItemsSource = items();
                combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
                combo.SelectionChanged += (s, e) => { App.Current?.Settings?.SaveQuiet(); };
                combo.SetBinding(ComboBox.SelectedItemProperty, bind);
                panel.Children.Add(combo);

                return panel;
            }

            FrameworkElement ButtonControl(string buttonText, RoutedEventHandler buttonClick, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
            {
                StackPanel panel = new StackPanel();
                panel.Orientation = Orientation.Vertical;

                var btn = new Button();
                btn.Padding = new Thickness(10, 0, 10, 0);
                btn.Margin = new Thickness(0, top == null ? 0 : 5, right == null ? 0 : 5, bottom == null ? 0 : 5);
                btn.Content = buttonText;
                btn.Click += buttonClick;

                if (top != null)
                    panel.Children.Add(top);

                if (right == null)
                {
                    panel.Children.Add(btn);
                }
                else
                {
                    StackPanel panel2 = new StackPanel();
                    panel2.Orientation = Orientation.Horizontal;
                    panel2.Children.Add(btn);
                    panel2.Children.Add(right);
                    panel.Children.Add(panel2);
                }

                if (bottom != null)
                    panel.Children.Add(bottom);

                return panel;
            }

            FrameworkElement PageBinding(FrameworkElement control, DependencyProperty dependency, string windowText, string buttonText, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
            {
                var page = SimpleControlBinding(control, dependency);
                var width = page.Width;
                var height = page.Height;
                var click = new RoutedEventHandler((s, e) =>
                {
                    var window = TemplatedWindow.GetWindow(page);
                    if (window != null)
                    {
                        var windowTarget = page.GetValue(dependency);
                        if (windowTarget == GetPropertyValue<object>())
                        {
                            window.MakeForeground();
                            return;
                        }
                    }

                    window = TemplatedWindow.CreateWindow(windowText, page);
                    window.Width = width;
                    window.Height = height;
                    window.Show();
                });

                return ButtonControl(buttonText, click, top, right, bottom);
            }

            T GetPropertyValue<T>() where T : class
            {
                var pinfo = property.GetDescriptor(property.PropertyName);
                var val = pinfo.GetValue(property.TargetObject) as T;
                return val;
            }

            if (property.Is(typeof(GlobalTrigger)))
            {
                return SimpleControlBinding(new GlobalTriggerEditor(), GlobalTriggerEditor.TriggerProperty);
            }

            if (property.Is(typeof(FFmpegSettings)))
            {
                return SimpleControlBinding(new FFMpegCodecSettingsEditor3(), FFMpegCodecSettingsEditor3.CodecSettingsProperty);
            }

            if (property.Is(typeof(FFmpegDirectShowAudioDevice)))
            {
                return ComboSelectBinding(() => FFmpegDirectShowAudioDevice.GetDevices(), nameof(FFmpegDirectShowAudioDevice.FriendlyName));
            }

            if (property.Is(typeof(IFeature)))
            {
                return new FeatureInstallerControl(GetPropertyValue<IFeature>());
            }

            if (property.Is(typeof(IUploadProvider)))
            {
                var propertyName = property.Descriptor.Name;
                var parsedEnum = (SupportedUploadType)Enum.Parse(typeof(SupportedUploadType), propertyName, true);
                return ComboSelectBinding(() => App.Current.Settings.UploadSettings.GetEnabledProviders(parsedEnum), nameof(IUploadProvider.Name));
            }

            if (property.Is(typeof(List<IUploadProvider>)))
            {
                var val = GetPropertyValue<List<IUploadProvider>>();

                var totalCount = val.Count();
                var enabledCount = val.Where(p => p.IsEnabled).Count();

                return PageBinding(
                    new UploadProviderSettingsEditor(),
                    UploadProviderSettingsEditor.ProvidersProperty,
                    "Configure Upload Providers",
                    "Configure",
                    new TextBlock()
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 5, 0, 0),
                        Text = $"{enabledCount}/{totalCount} available providers are enabled."
                    },
                    null,
                    new TextBlock()
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 0, 0, 10),
                        Text = $"First, configure upload providers and then select which provider you would like to be triggered for different file types. " +
                        $"Not all providers support all file types (ex. YouTube, Imgur). " +
                        $"Leaving the provider for a specific file type blank will ask you every time." +
                        Environment.NewLine +
                        $"0 providers are currently loaded by user-added modules."
                    }
                );
            }

            if (property.Is(typeof(FrameworkElement)))
            {
                var val = GetPropertyValue<FrameworkElement>();
                // if the control was used elsewhere prior, we need to reset the logical tree before using it again
                val.DisconnectFromLogicalParent();
                return val;
            }

            if (property.Is(typeof(AutoDictionary<,>)))
            {
                return ButtonControl("Reset", async (s, e) =>
                {
                    if (await NiceDialog.ShowYesNoPromptAsync(s as FrameworkElement, NiceDialogIcon.Warning, "Are you sure you wish to reset these settings to defaults?"))
                    {
                        var pinfo = property.GetDescriptor(property.PropertyName);
                        pinfo.SetValue(property.TargetObject, Activator.CreateInstance(property.ActualPropertyType));
                    }
                });
            }

            return base.CreateControl(property, options);
        }

        protected override FrameworkElement CreateDirectoryPathControl(PropertyItem property)
        {
            var control = (DirectoryPicker)base.CreateDirectoryPathControl(property);
            control.FolderBrowserDialogService = new BetterFolderBrowseDialog(control);
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
        private readonly FrameworkElement _parentControl;

        public BetterFolderBrowseDialog(FrameworkElement parentControl)
        {
            this._parentControl = parentControl;
        }
        public bool ShowFolderBrowserDialog(ref string directory, bool showNewFolderButton = true, string description = null, bool useDescriptionForTitle = true)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            dialog.Description = description;
            dialog.UseDescriptionForTitle = useDescriptionForTitle; // This applies to the Vista style dialog only, not the old dialog.
            dialog.ShowNewFolderButton = showNewFolderButton;
            dialog.SelectedPath = directory;

            var success = dialog.ShowDialog(TemplatedWindow.GetWindow(_parentControl)) == true;
            directory = dialog.SelectedPath;
            return success;
        }
    }
}
