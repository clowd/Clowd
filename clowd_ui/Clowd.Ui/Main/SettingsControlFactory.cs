using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Clowd.Config;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;

namespace Clowd.UI.Config
{
    /// <summary>
    /// Builds a reflection-driven settings page (label + editor rows) for a settings category
    /// object. Direct port of the WPF SettingsControlFactory; WPF-only editors translated per
    /// the decision table (#53 NumberBox -> NumericUpDown + NumericTypeConverter, folder
    /// browser -> StorageProvider, AudioDeviceInfo rows dropped with the video feature).
    /// </summary>
    public class SettingsControlFactory
    {
        private readonly object _obj;
        private readonly Func<Window> _wndFn;

        public SettingsControlFactory(Func<Window> wndFn, object obj)
        {
            _obj = obj;
            _wndFn = wndFn;
        }

        private IEnumerable<PropertyDescriptor> GetObjectProperties(object obj)
        {
            var instanceType = obj.GetType();

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(obj))
            {
                if (pd.ComponentType != instanceType)
                    continue;

                if (!pd.IsBrowsable)
                    continue;

                if (pd.IsReadOnly && GetFirstAttributeOrDefault<FlattenSettingsObjectAttribute>(pd) == null)
                    continue;

                yield return pd;
            }
        }

        public Control GetSettingsPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            int row = 0;
            AddRowsToGrid(ref row, grid);

            grid.RowDefinitions.Add(new RowDefinition(new GridLength(10)));

            return new ScrollViewer
            {
                Padding = new Thickness(24, 10, 24, 24),
                Content = grid,
            };
        }

        private void AddRowsToGrid(ref int row, Grid grid)
        {
            foreach (PropertyDescriptor pd in GetObjectProperties(_obj))
            {
                if (GetFirstAttributeOrDefault<FlattenSettingsObjectAttribute>(pd) != null)
                {
                    var child = pd.GetValue(_obj);
                    new SettingsControlFactory(_wndFn, child).AddRowsToGrid(ref row, grid);
                }
                else
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    var rowLabel = new TextBlock();
                    rowLabel.VerticalAlignment = VerticalAlignment.Center;
                    rowLabel.HorizontalAlignment = HorizontalAlignment.Left;
                    rowLabel.Margin = new Thickness(0, 4, 0, 4);
                    rowLabel.Text = FromCamelCase(pd.DisplayName);
                    Grid.SetRow(rowLabel, row);
                    Grid.SetColumn(rowLabel, 0);

                    var rowContent = new Border();
                    rowContent.VerticalAlignment = VerticalAlignment.Center;
                    rowContent.HorizontalAlignment = HorizontalAlignment.Stretch;
                    rowContent.Child = GetRowForProperty(pd);
                    rowContent.Margin = new Thickness(24, 4, 4, 4);
                    Grid.SetRow(rowContent, row);
                    Grid.SetColumn(rowContent, 1);

                    grid.Children.Add(rowLabel);
                    grid.Children.Add(rowContent);

                    row++;
                }
            }
        }

        private Control GetRowForProperty(PropertyDescriptor pd)
        {
            var type = pd.PropertyType;
            var tcode = Type.GetTypeCode(pd.PropertyType);

            if (Is(pd, typeof(string)))
            {
                var txt = SimpleControlBinding(new TextBox(), pd, TextBox.TextProperty);
                if (pd.Name.EndsWith("Directory"))
                {
                    var btn = ButtonControl("Browse", "Secondary", async (s, e) =>
                    {
                        var storage = _wndFn()?.StorageProvider;
                        if (storage == null)
                            return;

                        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "Pick a folder",
                            AllowMultiple = false,
                        });

                        var path = picked?.FirstOrDefault()?.TryGetLocalPath();
                        if (!String.IsNullOrEmpty(path))
                            pd.SetValue(_obj, path);
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

            if (Is(pd, typeof(bool)))
                return SimpleControlBinding(new CheckBox(), pd, CheckBox.IsCheckedProperty);

            if (pd.PropertyType.IsEnum)
                return ComboSelectBinding(() => Enum.GetValues(type), pd, GetEnumDisplayString, false);

            if ((int)tcode >= (int)TypeCode.Char && (int)tcode <= (int)TypeCode.Decimal)
            {
                // decision table #53: WPFUI NumberBox -> NumericUpDown (decimal?) bridged to the
                // int/double settings property by NumericTypeConverter.
                return SimpleControlBinding(new NumericUpDown { MinWidth = 160 }, pd, NumericUpDown.ValueProperty,
                                            new NumericTypeConverter());
            }

            if (Is(pd, typeof(Color)))
            {
                var inner = new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
                inner.Bind(Border.BackgroundProperty, CreateBinding(pd.Name, new ColorToBrushConverter()));

                var border = new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    Child = inner,
                    Background = AppStyles.CheckerboardBrushSmall,
                    Width = 24,
                    Height = 24,
                };

                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                label.Bind(TextBlock.TextProperty, CreateBinding(pd.Name, new ColorToHexConverter()));

                var stack = StackCtrl(border, label);
                stack.Cursor = new Cursor(StandardCursorType.Hand);
                stack.PointerPressed += async (s, e) =>
                {
                    var result = await NiceDialog.ShowColorPromptAsync(_wndFn(), (Color)pd.GetValue(_obj));
                    pd.SetValue(_obj, result);
                };
                return stack;
            }

            if (Is(pd, typeof(GlobalTrigger)))
                return SimpleControlBinding(new GlobalTriggerEditor(), pd, GlobalTriggerEditor.TriggerProperty);

            if (Is(pd, typeof(Control)))
            {
                return (Control)pd.GetValue(_obj);
            }

            if (Is(pd, typeof(AutoDictionary<,>)))
            {
                return ButtonControl("Reset", "Danger", async (s, e) =>
                {
                    if (await NiceDialog.ShowYesNoPromptAsync(s as Visual, NiceDialogIcon.Warning,
                            "Are you sure you wish to reset these settings to defaults?"))
                    {
                        pd.SetValue(_obj, Activator.CreateInstance(pd.PropertyType));
                    }
                });
            }

            if (Is(pd, typeof(TimeOption)))
            {
                var child = new SettingsControlFactory(_wndFn, pd.GetValue(_obj));
                var pdNum = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Number));
                var pdUnit = pd.GetChildProperties().OfType<PropertyDescriptor>().FirstOrDefault(t => t.Name == nameof(TimeOption.Unit));
                var ctNum = child.SimpleControlBinding(new TextBox { MinWidth = 60 }, pdNum, TextBox.TextProperty);
                var ctUnit = child.ComboSelectBinding(() => Enum.GetValues(pdUnit.PropertyType), pdUnit, null, false);
                return StackCtrl(ctNum, ctUnit);
            }

            return new TextBlock { Text = pd.Name, VerticalAlignment = VerticalAlignment.Center };
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

        private static bool Is(PropertyDescriptor pd, Type type)
        {
            if (type.IsGenericTypeDefinition)
                return pd.PropertyType.IsGenericType && pd.PropertyType.GetGenericTypeDefinition() == type;

            return type.IsAssignableFrom(pd.PropertyType);
        }

        private static T GetFirstAttributeOrDefault<T>(PropertyDescriptor pd) where T : Attribute
        {
            return pd.Attributes.OfType<T>().FirstOrDefault();
        }

        private static string GetEnumDisplayString(object arg)
        {
            if (arg == null)
                return null;

            try
            {
                var valueAttributes = arg.GetType().GetField(arg.ToString())?.GetCustomAttributes(false);
                var desc = valueAttributes?.FirstOrDefault(a => a is DescriptionAttribute) as DescriptionAttribute;
                if (desc != null)
                {
                    return desc.Description;
                }
            }
            catch {; }

            return arg.ToString();
        }

        Binding CreateBinding(string bindingPath, IValueConverter converter = null)
        {
            return new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                Converter = converter,
                Source = _obj,
            };
        }

        Panel StackCtrl(params Control[] children)
        {
            var panel = new StackPanel();
            panel.Spacing = 10;
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Background = Brushes.Transparent; // hit-testable for PointerPressed
            foreach (var c in children)
                panel.Children.Add(c);

            return panel;
        }

        Control DockCtrl(Control fill, Control dock, Dock position)
        {
            var panel = new DockPanel();
            panel.VerticalAlignment = VerticalAlignment.Stretch;
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;

            DockPanel.SetDock(dock, position);
            panel.Children.Add(dock);
            panel.Children.Add(fill);
            return panel;
        }

        Control SimpleControlBinding(Control control, PropertyDescriptor pd, AvaloniaProperty property, IValueConverter converter = null)
        {
            control.Bind(property, CreateBinding(pd.Name, converter));
            return control;
        }

        Control ComboSelectBinding(Func<IEnumerable> items, PropertyDescriptor pd, Func<object, string> display = null, bool canClear = true)
        {
            display ??= o => o?.ToString();

            var combo = new ComboBox();
            combo.ItemTemplate = new FuncDataTemplate<object>((o, ns) => new TextBlock { Text = display(o) });
            combo.ItemsSource = items();
            combo.MinWidth = 160;
            combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
            combo.Bind(ComboBox.SelectedItemProperty, CreateBinding(pd.Name));

            if (canClear)
            {
                var panel = new DockPanel();
                var reset = new Button();
                reset.Classes.Add("Tertiary"); // Semi tertiary button for the low-emphasis clear action
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

        Control ButtonControl(string buttonText, string buttonClass, EventHandler<RoutedEventArgs> buttonClick, Control top = null,
            Control right = null, Control bottom = null)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            var btn = new Button();
            if (!String.IsNullOrEmpty(buttonClass))
                btn.Classes.Add(buttonClass); // Semi style class (Secondary/Tertiary/Danger/...)
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
    }
}
