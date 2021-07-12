using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Config;
using Clowd.Installer.Features;
using Clowd.UI.Config;
using Clowd.UI.Helpers;
using Clowd.Util;
using PropertyChanged;

namespace Clowd.UI.Config
{
    //public class SettingsControlFactory
    //{
    //    FrameworkElement SimpleControlBinding(FrameworkElement control, DependencyProperty dependency)
    //    {
    //        var bind = property.CreateBinding();
    //        bind.Source = property.TargetObject;
    //        control.SetBinding(dependency, bind);
    //        return control;
    //    }

    //    FrameworkElement ComboSelectBinding(Func<System.Collections.IEnumerable> items, string displayPath, bool canClear = true)
    //    {
    //        var bind = property.CreateBinding();
    //        bind.Source = property.TargetObject;

    //        var panel = new DockPanel();
    //        var reset = new Button();
    //        var combo = new ComboBox();

    //        if (canClear)
    //        {
    //            DockPanel.SetDock(reset, Dock.Right);
    //            reset.Margin = new Thickness(5, 0, 0, 0);
    //            reset.Padding = new Thickness(10, 0, 10, 0);
    //            reset.Content = "Clear";
    //            reset.Click += (s, e) => { combo.SelectedIndex = -1; };
    //            panel.Children.Add(reset);
    //        }

    //        combo.DisplayMemberPath = displayPath;
    //        combo.ItemsSource = items();
    //        combo.DropDownOpened += (s, e) => { combo.ItemsSource = items(); };
    //        combo.SelectionChanged += (s, e) => { App.Current?.Settings?.SaveQuiet(); };
    //        combo.SetBinding(ComboBox.SelectedItemProperty, bind);
    //        panel.Children.Add(combo);

    //        return panel;
    //    }

    //    FrameworkElement ButtonControl(string buttonText, RoutedEventHandler buttonClick, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
    //    {
    //        StackPanel panel = new StackPanel();
    //        panel.Orientation = Orientation.Vertical;

    //        var btn = new Button();
    //        btn.Padding = new Thickness(10, 0, 10, 0);
    //        btn.Margin = new Thickness(0, top == null ? 0 : 5, right == null ? 0 : 5, bottom == null ? 0 : 5);
    //        btn.Content = buttonText;
    //        btn.Click += buttonClick;

    //        if (top != null)
    //            panel.Children.Add(top);

    //        if (right == null)
    //        {
    //            panel.Children.Add(btn);
    //        }
    //        else
    //        {
    //            StackPanel panel2 = new StackPanel();
    //            panel2.Orientation = Orientation.Horizontal;
    //            panel2.Children.Add(btn);
    //            panel2.Children.Add(right);
    //            panel.Children.Add(panel2);
    //        }

    //        if (bottom != null)
    //            panel.Children.Add(bottom);

    //        return panel;
    //    }

    //    FrameworkElement PageBinding(FrameworkElement control, DependencyProperty dependency, string windowText, string buttonText, FrameworkElement top = null, FrameworkElement right = null, FrameworkElement bottom = null)
    //    {
    //        var page = SimpleControlBinding(control, dependency);
    //        var width = page.Width;
    //        var height = page.Height;
    //        var click = new RoutedEventHandler((s, e) =>
    //        {
    //            var window = TemplatedWindow.GetWindow(page);
    //            if (window != null)
    //            {
    //                var windowTarget = page.GetValue(dependency);
    //                if (windowTarget == GetPropertyValue<object>())
    //                {
    //                    window.GetPlatformWindow().Activate();
    //                    return;
    //                }
    //            }

    //            window = TemplatedWindow.CreateWindow(windowText, page);
    //            window.Width = width;
    //            window.Height = height;
    //            window.Show();
    //        });

    //        return ButtonControl(buttonText, click, top, right, bottom);
    //    }
    //    public override FrameworkElement CreateControl(PropertyItem property, PropertyControlFactoryOptions options)
    //    {
    //        T GetPropertyValue<T>() where T : class
    //        {
    //            var pinfo = property.GetDescriptor(property.PropertyName);
    //            var val = pinfo.GetValue(property.TargetObject) as T;
    //            return val;
    //        }

    //        if (property.Is(typeof(GlobalTrigger)))
    //        {
    //            return SimpleControlBinding(new GlobalTriggerEditor(), GlobalTriggerEditor.TriggerProperty);
    //        }

    //        if (property.Is(typeof(IAudioMicrophoneDevice)))
    //        {
    //            return ComboSelectBinding(Video.AudioDeviceManager.GetMicrophones, nameof(IAudioDevice.FriendlyName), false);
    //        }

    //        if (property.Is(typeof(IAudioSpeakerDevice)))
    //        {
    //            return ComboSelectBinding(Video.AudioDeviceManager.GetSpeakers, nameof(IAudioDevice.FriendlyName), false);
    //        }

    //        if (property.Is(typeof(IFeature)))
    //        {
    //            return new FeatureInstallerControl(GetPropertyValue<IFeature>());
    //        }

    //        if (property.Is(typeof(IUploadProvider)))
    //        {
    //            var propertyName = property.Descriptor.Name;
    //            var parsedEnum = (SupportedUploadType)Enum.Parse(typeof(SupportedUploadType), propertyName, true);
    //            return ComboSelectBinding(() => App.Current.Settings.UploadSettings.GetEnabledProviders(parsedEnum), nameof(IUploadProvider.Name));
    //        }

    //        if (property.Is(typeof(List<IUploadProvider>)))
    //        {
    //            var val = GetPropertyValue<List<IUploadProvider>>();

    //            var totalCount = val.Count();
    //            var enabledCount = val.Where(p => p.IsEnabled).Count();

    //            return PageBinding(
    //                new UploadProviderSettingsEditor(),
    //                UploadProviderSettingsEditor.ProvidersProperty,
    //                "Configure Upload Providers",
    //                "Configure",
    //                new TextBlock()
    //                {
    //                    TextWrapping = TextWrapping.Wrap,
    //                    Margin = new Thickness(0, 5, 0, 0),
    //                    Text = $"{enabledCount}/{totalCount} available providers are enabled."
    //                },
    //                null,
    //                new TextBlock()
    //                {
    //                    TextWrapping = TextWrapping.Wrap,
    //                    Foreground = Brushes.Gray,
    //                    Margin = new Thickness(0, 0, 0, 10),
    //                    Text = $"First, configure upload providers and then select which provider you would like to be triggered for different file types. " +
    //                    $"Not all providers support all file types (ex. YouTube, Imgur). " +
    //                    $"Leaving the provider for a specific file type blank will ask you every time." +
    //                    Environment.NewLine +
    //                    $"0 providers are currently loaded by user-added modules."
    //                }
    //            );
    //        }

    //        if (property.Is(typeof(FrameworkElement)))
    //        {
    //            var val = GetPropertyValue<FrameworkElement>();
    //            // if the control was used elsewhere prior, we need to reset the logical tree before using it again
    //            val.DisconnectFromLogicalParent();
    //            return val;
    //        }

    //        if (property.Is(typeof(AutoDictionary<,>)))
    //        {
    //            return ButtonControl("Reset", async (s, e) =>
    //            {
    //                if (await NiceDialog.ShowYesNoPromptAsync(s as FrameworkElement, NiceDialogIcon.Warning, "Are you sure you wish to reset these settings to defaults?"))
    //                {
    //                    var pinfo = property.GetDescriptor(property.PropertyName);
    //                    pinfo.SetValue(property.TargetObject, Activator.CreateInstance(property.ActualPropertyType));
    //                }
    //            });
    //        }

    //        return base.CreateControl(property, options);
    //    }


    //}
}
