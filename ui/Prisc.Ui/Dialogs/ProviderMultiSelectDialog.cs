using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace Clowd.UI.Dialogs
{
    /// <summary>
    /// A checklist of upload providers, laid out like <see cref="ProviderSelectionDialog"/> but
    /// picking several at once. Shared by the export and import halves of the upload settings
    /// transfer, which differ only in their wording and in which rows are selectable.
    /// </summary>
    internal static class ProviderMultiSelectDialog
    {
        public sealed class Item
        {
            /// <summary>Provider type name — the key returned to the caller.</summary>
            public string Key { get; init; }

            public string Name { get; init; }

            public string Description { get; init; }

            /// <summary>Opens a fresh icon stream, or null when this build has no such provider.</summary>
            public Func<Stream> Icon { get; init; }

            /// <summary>False grays the row out and makes it unselectable — used for providers
            /// present in an imported string but unknown to this build.</summary>
            public bool IsAvailable { get; init; } = true;

            public bool IsCheckedByDefault { get; init; }
        }

        /// <summary>Shows the picker and returns the checked keys, or null if the user canceled.</summary>
        public static async Task<string[]> ShowAsync(
            Window owner, string title, string heading, string actionLabel, IReadOnlyList<Item> items)
        {
            var wnd = new SystemThemedWindow
            {
                Title = title,
                Width = 460,
                MinWidth = 460,
                MaxHeight = 640,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            };

            var boxes = new List<(Item Item, CheckBox Box)>();

            var list = new StackPanel { Spacing = 2 };

            foreach (var item in items)
            {
                var icon = new Image
                {
                    Width = 24,
                    Height = 24,
                    Stretch = Avalonia.Media.Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                try
                {
                    using var stream = item.Icon?.Invoke();
                    if (stream != null)
                        icon.Source = new Bitmap(stream);
                }
                catch {; }

                var text = new StackPanel { Margin = new Avalonia.Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = item.Name, FontWeight = Avalonia.Media.FontWeight.Bold });
                if (!String.IsNullOrEmpty(item.Description))
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = item.Description,
                        Opacity = 0.8,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    });
                }

                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(icon);
                content.Children.Add(text);

                var box = new CheckBox
                {
                    Content = content,
                    IsChecked = item.IsAvailable && item.IsCheckedByDefault,
                    IsEnabled = item.IsAvailable,
                    Padding = new Avalonia.Thickness(8, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                boxes.Add((item, box));
                list.Children.Add(box);
            }

            var selectAll = new CheckBox
            {
                Content = "Select all",
                IsThreeState = false,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                IsEnabled = boxes.Any(b => b.Item.IsAvailable),
            };

            var action = new Button
            {
                Content = actionLabel,
                MinWidth = 90,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = true,
            };
            action.Classes.Add("Primary");

            // guards the two-way sync between "Select all" and the individual rows, which would
            // otherwise chase each other (ticking a row updates the header, which re-ticks rows).
            var syncing = false;

            void RefreshActionState()
            {
                action.IsEnabled = boxes.Any(b => b.Box.IsChecked == true);
            }

            void RefreshSelectAll()
            {
                var available = boxes.Where(b => b.Item.IsAvailable).ToArray();
                syncing = true;
                selectAll.IsChecked = available.Length > 0 && available.All(b => b.Box.IsChecked == true);
                syncing = false;
            }

            foreach (var (_, box) in boxes)
            {
                box.IsCheckedChanged += (_, _) =>
                {
                    if (syncing)
                        return;

                    RefreshActionState();
                    RefreshSelectAll();
                };
            }

            selectAll.IsCheckedChanged += (_, _) =>
            {
                if (syncing)
                    return;

                var check = selectAll.IsChecked == true;
                syncing = true;
                foreach (var (item, box) in boxes)
                {
                    if (item.IsAvailable)
                        box.IsChecked = check;
                }

                syncing = false;
                RefreshActionState();
            };

            RefreshActionState();
            RefreshSelectAll();

            var panel = new StackPanel { Margin = new Avalonia.Thickness(24, 16, 24, 12), Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = heading,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.8,
            });
            panel.Children.Add(selectAll);
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 380,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = list,
            });

            string[] result = null;
            action.Click += (_, _) =>
            {
                result = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item.Key).ToArray();
                wnd.Close();
            };

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 80,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsCancel = true,
            };
            cancel.Click += (_, _) => wnd.Close();

            // footer strip matching MessageDialog / ProviderSelectionDialog (tinted bar, actions right)
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, action },
            };

            var footer = new Border
            {
                Child = buttons,
                Padding = new Avalonia.Thickness(16, 10),
            };
            footer.Bind(Border.BackgroundProperty, footer.GetResourceObservable("SemiColorFill0"));

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(panel);

            wnd.Content = root;

            if (owner != null && owner != wnd)
            {
                await wnd.ShowDialog(owner);
            }
            else
            {
                var tcs = new TaskCompletionSource();
                wnd.Closed += (_, _) => tcs.TrySetResult();
                wnd.Show();
                await tcs.Task;
            }

            return result;
        }
    }
}
