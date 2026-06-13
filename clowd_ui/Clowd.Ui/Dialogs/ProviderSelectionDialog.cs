using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Clowd.Config;

namespace Clowd.UI.Dialogs
{
    /// <summary>
    /// Replaces the WPF TaskDialog command-link prompt shown when an upload has no default
    /// provider for its content type: one button per enabled provider, plus an optional
    /// "set as default" checkbox.
    /// </summary>
    internal static class ProviderSelectionDialog
    {
        public sealed class Selection
        {
            public UploadProviderInfo Info { get; init; }
            public bool SetAsDefault { get; init; }
        }

        public static async Task<Selection> ShowAsync(SupportedUploadType type, UploadProviderInfo[] providers)
        {
            var wnd = new SystemThemedWindow
            {
                Title = $"{type} Upload",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CanResize = false,
            };

            UploadProviderInfo chosen = null;

            var panel = new StackPanel { Margin = new Avalonia.Thickness(24, 16), Spacing = 10 };

            panel.Children.Add(new TextBlock
            {
                Text = "Select an upload destination:",
                FontWeight = Avalonia.Media.FontWeight.Bold,
            });

            panel.Children.Add(new TextBlock
            {
                Text = $"You have not selected a default upload provider for '{type}', where would you like to send your file?",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.8,
            });

            var setDefault = new CheckBox { Content = $"Set choice as default for {type}" };

            foreach (var p in providers)
            {
                var icon = new Image { Width = 24, Height = 24, Stretch = Avalonia.Media.Stretch.Uniform };
                try
                {
                    using var stream = p.Provider.Icon;
                    if (stream != null)
                        icon.Source = new Bitmap(stream);
                }
                catch {; }

                var text = new StackPanel { Margin = new Avalonia.Thickness(10, 0, 0, 0) };
                text.Children.Add(new TextBlock { Text = p.Provider.Name, FontWeight = Avalonia.Media.FontWeight.Bold });
                text.Children.Add(new TextBlock { Text = p.Provider.Description, Opacity = 0.8, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(icon);
                content.Children.Add(text);

                var btn = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Avalonia.Thickness(10, 8),
                };
                var captured = p;
                btn.Click += (_, _) =>
                {
                    chosen = captured;
                    wnd.Close();
                };
                panel.Children.Add(btn);
            }

            panel.Children.Add(setDefault);

            var cancel = new Button
            {
                Content = "Cancel",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            cancel.Click += (_, _) => wnd.Close();
            panel.Children.Add(cancel);

            wnd.Content = panel;

            var owner = GetActiveWindow();
            if (owner != null && owner != wnd)
                await wnd.ShowDialog(owner);
            else
            {
                var tcs = new TaskCompletionSource();
                wnd.Closed += (_, _) => tcs.TrySetResult();
                wnd.Show();
                await tcs.Task;
            }

            if (chosen == null)
                return null;

            return new Selection { Info = chosen, SetAsDefault = setDefault.IsChecked == true };
        }

        private static Window GetActiveWindow()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.Windows.FirstOrDefault(w => w.IsActive)
                       ?? desktop.MainWindow
                       ?? desktop.Windows.FirstOrDefault();
            }

            return null;
        }
    }
}
