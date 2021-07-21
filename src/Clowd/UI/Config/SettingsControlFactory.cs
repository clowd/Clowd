using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.Util;
using ModernWpf.Controls;

namespace Clowd.UI.Config
{
    public class SettingsControlFactory
    {
        private readonly object obj;
        private readonly Window wnd;

        public SettingsControlFactory(Window wnd, object obj)
        {
            this.obj = obj;
            this.wnd = wnd;
        }

        public FrameworkElement GetSettingsPanel()
        {
            var scroll = new ScrollViewerEx();

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
                rowLabel.Margin = new Thickness(24, 4, 0, 4);
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

            scroll.Content = grid;
            return scroll;
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
                        var whwnd = new WindowInteropHelper(wnd).Handle;
                        var dlg = new PlatformUtil.Windows.FolderBrowserDialog();
                        dlg.Title = "Pick a folder";
                        if (dlg.ShowDialog(whwnd))
                            pd.SetValue(obj, dlg.SelectedPath);
                    });
                    btn.Margin = new Thickness(10, 0, 0, 0);
                    btn.VerticalAlignment = VerticalAlignment.Center;
                    return DockBinding(txt, btn, Dock.Right);
                }
                else
                {
                    return txt;
                }
            }

            if (pd.Is(typeof(bool)))
                return SimpleControlBinding(new ToggleSwitch(), pd, ToggleSwitch.IsOnProperty);

            if (pd.PropertyType.IsEnum)
                return ComboSelectBinding(() => Enum.GetValues(type), pd, null, false);

            if ((int)tcode >= (int)TypeCode.Char && (int)tcode <= (int)TypeCode.Decimal)
            {
                return SimpleControlBinding(new NumberBox()
                {
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
                }, pd, NumberBox.ValueProperty);
            }

            if (pd.Is(typeof(Color)))
                return SimpleControlBinding(new Dialogs.ColorPicker.ColorPicker(), pd, Dialogs.ColorPicker.ColorPicker.SelectedColorProperty);

            if (pd.Is(typeof(IAudioMicrophoneDevice)))
                return ComboSelectBinding(Video.AudioDeviceManager.GetMicrophones, pd, nameof(IAudioDevice.FriendlyName), false);

            if (pd.Is(typeof(IAudioSpeakerDevice)))
                return ComboSelectBinding(Video.AudioDeviceManager.GetSpeakers, pd, nameof(IAudioDevice.FriendlyName), false);

            if (pd.Is(typeof(GlobalTrigger)))
                return SimpleControlBinding(new GlobalTriggerEditor(), pd, GlobalTriggerEditor.TriggerProperty);

            if (pd.Is(typeof(FrameworkElement)))
            {
                var val = (FrameworkElement)pd.GetValue(obj);// GetPropertyValue<FrameworkElement>();
                // if the control was used elsewhere prior, we need to reset the logical tree before using it again
                val.DisconnectFromLogicalParent();
                return val;
            }

            if (pd.Is(typeof(AutoDictionary<,>)))
            {
                return ButtonControl("Reset", async (s, e) =>
                {
                    if (await NiceDialog.ShowYesNoPromptAsync(s as FrameworkElement, NiceDialogIcon.Warning, "Are you sure you wish to reset these settings to defaults?"))
                    {
                        pd.SetValue(obj, Activator.CreateInstance(pd.PropertyType));
                    }
                });
            }

            if (pd.Is(typeof(Installer.Features.IFeature)))
                return new FeatureInstallerControl((Installer.Features.IFeature)pd.GetValue(obj));

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

        FrameworkElement DockBinding(FrameworkElement fill, FrameworkElement dock, Dock position)
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

        FrameworkElement ComboSelectBinding(Func<System.Collections.IEnumerable> items, PropertyDescriptor pd, string displayPath, bool canClear = true)
        {
            var bind = CreateBinding(pd.Name);

            var combo = new ComboBox();
            combo.DisplayMemberPath = displayPath;
            combo.ItemsSource = items();
            combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
            //combo.SelectionChanged += (s, e) => { App.Current?.Settings?.SaveQuiet(); };
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

        FrameworkElement ButtonControl(string buttonText, RoutedEventHandler buttonClick, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
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
