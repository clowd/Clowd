using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Clowd.Config;
using Clowd.UI.Config;

namespace Clowd.UI.Pages
{
    public partial class UploadSettingsPage : UserControl
    {
        public UploadSettingsPage()
        {
            InitializeComponent();
            DataContext = SettingsRoot.Current.Uploads;
        }

        private void DefaultChipClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag, DataContext: UploadProviderInfo info })
                return;

            if (!Enum.TryParse<SupportedUploadType>(tag, out var type))
                return;

            var uploads = SettingsRoot.Current.Uploads;
            if (info.DefaultFor.HasFlag(type))
                uploads.ClearDefaultProvider(info, type);
            else
                uploads.SetDefaultProvider(info, type);
        }

        private void ProviderSettingsClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: UploadProviderInfo info })
                OpenProviderSettings(info);
        }

        private void ProviderDoubleTapped(object sender, TappedEventArgs e)
        {
            // double-tap on the row opens settings, unless the tap landed on an
            // interactive child (switch / chip / button) which handles its own input.
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) == null && v.FindAncestorOfType<ToggleSwitch>(true) == null)
            {
                if (sender is Control { DataContext: UploadProviderInfo info })
                    OpenProviderSettings(info);
            }
        }

        private async void OpenProviderSettings(UploadProviderInfo info)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;

            var wnd = new SystemThemedWindow
            {
                Title = "Edit settings for " + info.Provider.Name,
                // wide enough that the longest label ("Disable checksum validation") plus a
                // full-width editor doesn't overflow the right edge.
                Width = 580,
                MinWidth = 580,
                MaxHeight = 600,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            Func<Window> getWnd = () => wnd;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // the factory panel has no outer padding of its own (the settings shell supplies it)
            var panel = new SettingsControlFactory(getWnd, info).GetSettingsPanel();
            panel.Margin = new Thickness(24, 16, 8, 0);
            root.Children.Add(panel);

            var close = new Button
            {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 0, 24, 16),
            };
            close.Classes.Add("Primary");
            close.Click += (_, _) => wnd.Close();
            Grid.SetRow(close, 1);
            root.Children.Add(close);

            wnd.Content = root;

            if (owner != null)
                await wnd.ShowDialog(owner);
            else
                wnd.Show();
        }
    }
}
