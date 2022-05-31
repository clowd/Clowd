using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.Config;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;
using Clowd.Video;

namespace Clowd.UI.Config
{
    public class SettingsControlFactory
    {
        private readonly object obj;
        private readonly Func<Window> wndFn;

        public SettingsControlFactory(Func<Window> wndFn, object obj)
        {
            this.obj = obj;
            this.wndFn = wndFn;
        }

        public Page GetSettingsPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(0, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var instanceType = obj.GetType();

            var props = TypeDescriptor.GetProperties(obj);
            int row = 0;

            foreach (PropertyDescriptor pd in props)
            {
                if (pd.ComponentType != instanceType)
                    continue;

                if (!pd.IsBrowsable())
                    continue;

                if (pd.IsReadOnly())
                    continue;

                grid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(0, GridUnitType.Auto) });

                var rowLabel = new Label();
                rowLabel.VerticalAlignment = VerticalAlignment.Center;
                rowLabel.HorizontalAlignment = HorizontalAlignment.Left;
                rowLabel.Margin = new Thickness(0, 4, 0, 4);
                rowLabel.Content = FromCamelCase(pd.DisplayName);
                Grid.SetRow(rowLabel, row);
                Grid.SetColumn(rowLabel, 0);

                var rowContent = new Border();
                rowContent.VerticalAlignment = VerticalAlignment.Center;
                rowContent.HorizontalAlignment = HorizontalAlignment.Stretch;
                rowContent.Child = GetRowForProperty(pd);
                rowContent.Margin = new Thickness(24, 4, 56, 4);
                Grid.SetRow(rowContent, row);
                Grid.SetColumn(rowContent, 1);

                grid.Children.Add(rowLabel);
                grid.Children.Add(rowContent);

                row++;
            }

            grid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(56) });

            return new Page
            {
                Content = new WPFUI.Controls.DynamicScrollViewer
                {
                    Padding = new Thickness(24, 0, 24, 24),
                    Content = grid
                }
            };
        }

        public FrameworkElement GetRowForProperty(PropertyDescriptor pd)
        {
            var type = pd.PropertyType;
            var tcode = Type.GetTypeCode(pd.PropertyType);

            if (pd.Is(typeof(string)))
            {
                var txt = SimpleControlBinding(new TextBox(), pd, TextBox.TextProperty);
                if (pd.Name.EndsWith("Directory"))
                {
                    var btn = ButtonControl("Browse", (s, e) =>
                    {
                        var whwnd = new WindowInteropHelper(wndFn()).Handle;
                        var dlg = new PlatformUtil.Windows.FolderBrowserDialog();
                        dlg.Title = "Pick a folder";
                        if (dlg.ShowDialog(whwnd))
                            pd.SetValue(obj, dlg.SelectedPath);
                    });
                    btn.Margin = new Thickness(10, 0, 0, 0);
                    btn.VerticalAlignment = VerticalAlignment.Center;
                    return DockCtrl(txt, btn, Dock.Right);
                }
                else
                {
                    return txt;
                }
            }

            if (pd.Is(typeof(bool)))
                return SimpleControlBinding(new CheckBox(), pd, CheckBox.IsCheckedProperty);

            if (pd.PropertyType.IsEnum)
                return ComboSelectBinding(() => Enum.GetValues(type), pd, null, false);

            if ((int)tcode >= (int)TypeCode.Char && (int)tcode <= (int)TypeCode.Decimal)
            {
                return SimpleControlBinding(new WPFUI.Controls.NumberBox()
                {
                    // properties?
                }, pd, WPFUI.Controls.NumberBox.ValueProperty);
            }

            if (pd.Is(typeof(Color)))
                return SimpleControlBinding(new Dialogs.ColorPicker.ColorPicker(), pd, Dialogs.ColorPicker.ColorPicker.SelectedColorProperty);

            if (pd.Is(typeof(AudioDeviceInfo)))
            {
                var templ = new ComboDisplayTemplateSelector<AudioDeviceInfo>((a) => AudioDeviceManager.GetFriendlyName(a));

                // TODO: This is a super big hack. Do this better.
                if (pd.Name.Contains("Microphone"))
                {
                    return ComboSelectBinding(AudioDeviceManager.GetMicrophones, pd, templ, false);
                }
                else
                {
                    return ComboSelectBinding(AudioDeviceManager.GetSpeakers, pd, templ, false);
                }
            }

            if (pd.Is(typeof(GlobalTrigger)))
                return SimpleControlBinding(new GlobalTriggerEditor(), pd, GlobalTriggerEditor.TriggerProperty);

            if (pd.Is(typeof(FrameworkElement)))
            {
                var val = (FrameworkElement)pd.GetValue(obj); // GetPropertyValue<FrameworkElement>();
                // if the control was used elsewhere prior, we need to reset the logical tree before using it again
                val.DisconnectFromLogicalParent();
                return val;
            }

            if (pd.Is(typeof(AutoDictionary<,>)))
            {
                return ButtonControl("Reset", async (s, e) =>
                {
                    if (await NiceDialog.ShowYesNoPromptAsync(s as FrameworkElement, NiceDialogIcon.Warning,
                            "Are you sure you wish to reset these settings to defaults?"))
                    {
                        pd.SetValue(obj, Activator.CreateInstance(pd.PropertyType));
                    }
                });
            }

            if (pd.Is(typeof(TimeOption)))
            {
                // TODO, make this easier to do.
                var child = new SettingsControlFactory(wndFn, pd.GetValue(obj));
                var pdNum = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Number));
                var pdUnit = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Unit));
                var ctNum = child.SimpleControlBinding(new TextBox(), pdNum, TextBox.TextProperty);
                var ctUnit = child.ComboSelectBinding(() => Enum.GetValues(pdUnit.PropertyType), pdUnit, null, false);
                return StackCtrl(ctNum, ctUnit);
            }

            //if (pd.Is(typeof(Setup.Features.IFeature)))
            //    return new FeatureInstallerControl((Setup.Features.IFeature)pd.GetValue(obj));

            return new Label() { Content = pd.Name };
        }

        public static string FromCamelCase(string variableName)
        {
            if (variableName.Contains(' '))
                return variableName;
            var sb = new StringBuilder();
            for (int i = 0; i < variableName.Length; i++)
            {
                if (i > 0 && char.IsUpper(variableName[i]) && !char.IsUpper(variableName[i - 1]))
                {
                    sb.Append(" ");
                    if (i == variableName.Length - 1 || char.IsUpper(variableName[i + 1]))
                    {
                        sb.Append(variableName[i]);
                    }
                    else
                    {
                        sb.Append(variableName[i].ToString(CultureInfo.InvariantCulture).ToLower());
                    }

                    continue;
                }

                sb.Append(variableName[i]);
            }

            return sb.ToString();
        }

        Binding CreateBinding(string bindingPath, UpdateSourceTrigger trigger = UpdateSourceTrigger.Default)
        {
            var binding = new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = trigger,
                ValidatesOnDataErrors = true,
                ValidatesOnExceptions = true,
                NotifyOnSourceUpdated = true,
                Source = obj,
            };

            return binding;
        }

        FrameworkElement StackCtrl(params FrameworkElement[] children)
        {
            var panel = new SimpleStackPanel();
            panel.Spacing = 10;
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            foreach (var c in children)
                panel.Children.Add(c);

            return panel;
        }

        FrameworkElement DockCtrl(FrameworkElement fill, FrameworkElement dock, Dock position)
        {
            var panel = new DockPanel();
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            DockPanel.SetDock(dock, position);
            panel.Children.Add(dock);
            panel.Children.Add(fill);
            return panel;
        }

        FrameworkElement SimpleControlBinding(FrameworkElement control, PropertyDescriptor pd, DependencyProperty dependency)
        {
            var bind = CreateBinding(pd.Name);
            control.SetBinding(dependency, bind);
            return control;
        }

        class ComboDisplayTemplateSelector<T> : DataTemplateSelector
        {
            private readonly Func<T, string> _factory;

            public ComboDisplayTemplateSelector(Func<T, string> factory)
            {
                _factory = factory;
            }

            public override DataTemplate SelectTemplate(object item, DependencyObject container)
            {
                var template = new DataTemplate();
                FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock));
                Binding binding = new Binding();
                binding.Source = item;
                binding.Converter = new ComboDisplayConverter<T>(_factory);
                text.SetBinding(TextBlock.TextProperty, binding);
                template.VisualTree = text;
                template.Seal();
                return template;
            }
        }

        class ComboDisplayConverter<T> : IValueConverter
        {
            private readonly Func<T, string> _factory;

            public ComboDisplayConverter(Func<T, string> factory)
            {
                _factory = factory;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return _factory((T)value);
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        FrameworkElement ComboSelectBinding(Func<System.Collections.IEnumerable> items, PropertyDescriptor pd, DataTemplateSelector template = null,
            bool canClear = true)
        {
            var bind = CreateBinding(pd.Name);

            var combo = new ComboBox();
            if (template != null)
                combo.ItemTemplateSelector = template;
            combo.ItemsSource = items();
            combo.MinWidth = 160;
            combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
            combo.SetBinding(ComboBox.SelectedItemProperty, bind);

            if (canClear)
            {
                var panel = new DockPanel();
                var reset = new Button();
                DockPanel.SetDock(reset, Dock.Right);
                reset.Margin = new Thickness(5, 0, 0, 0);
                reset.Padding = new Thickness(10, 0, 10, 0);
                reset.Content = "Clear";
                reset.Click += (s, e) => { combo.SelectedIndex = -1; };
                panel.Children.Add(reset);
                panel.Children.Add(combo);
                return panel;
            }
            else
            {
                return combo;
            }
        }

        FrameworkElement ButtonControl(string buttonText, RoutedEventHandler buttonClick, FrameworkElement top = null, FrameworkElement right = null,
            FrameworkElement bottom = null)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            var btn = new Button();
            //btn.Padding = new Thickness(10, 0, 10, 0);
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

        //FrameworkElement PageBinding(FrameworkElement control, PropertyDescriptor pd, DependencyProperty dependency, string windowText, string buttonText, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
        //{
        //    var page = SimpleControlBinding(control, pd, dependency);
        //    var width = page.Width;
        //    var height = page.Height;
        //    var click = new RoutedEventHandler((s, e) =>
        //    {
        //        var window = TemplatedWindow.GetWindow(page);
        //        if (window != null)
        //        {
        //            var windowTarget = page.GetValue(dependency);
        //            if (windowTarget == pd.GetValue(obj))
        //            {
        //                window.GetPlatformWindow().Activate();
        //                return;
        //            }
        //        }

        //        window = TemplatedWindow.CreateWindow(windowText, page);
        //        window.Width = width;
        //        window.Height = height;
        //        window.Show();
        //    });

        //    return ButtonControl(buttonText, click, top, right, bottom);
        //}
    }
}
