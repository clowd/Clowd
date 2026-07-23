using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Helpers;
using Ursa.Controls;

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

        private const string TestButtonIdleContent = "Test";

        private async void ProviderTestClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: UploadProviderInfo info } button)
                return;

            // a click during the "Success" state starts a fresh test; cancel the pending revert
            // so the old flow can't reset the button contents underneath the new one.
            if (button.Tag is CancellationTokenSource pendingRevert)
            {
                pendingRevert.Cancel();
                button.Tag = null;
            }

            button.IsEnabled = false;
            button.Content = TestButtonContent(new LoadingIcon { Width = 14, Height = 14, IsLoading = true }, "Testing...");

            string failure = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await info.Provider.TestAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                failure = $"The {info.Provider.Name} upload test timed out after 30 seconds.";
            }
            catch (Exception ex)
            {
                failure = UploadProviderBase.DescribeError(ex);
            }
            finally
            {
                button.IsEnabled = true;
            }

            if (failure == null)
            {
                var check = new Path
                {
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = this.FindResource("SemiColorSuccess") as IBrush,
                    Data = this.FindResource("IconCheckmark") as Geometry,
                };
                button.Content = TestButtonContent(check, "Success");

                var revert = new CancellationTokenSource();
                button.Tag = revert;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), revert.Token);
                    button.Content = TestButtonIdleContent;
                    button.Tag = null;
                }
                catch (OperationCanceledException)
                { }
            }
            else
            {
                button.Content = TestButtonIdleContent;
                try
                {
                    await NiceDialog.ShowNoticeAsync(button, NiceDialogIcon.Error, failure, "Upload test failed");
                }
                catch
                { }
            }
        }

        private static StackPanel TestButtonContent(Control icon, string text)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
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

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Avalonia.Thickness(0, 12, 24, 16),
            };

            var test = new Button
            {
                Content = TestButtonIdleContent,
                DataContext = info,
            };
            test.Click += ProviderTestClicked;
            actions.Children.Add(test);

            var close = new Button { Content = "Close" };
            close.Classes.Add("Primary");
            close.Click += (_, _) => wnd.Close();
            actions.Children.Add(close);

            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            wnd.Content = root;

            if (owner != null)
                await wnd.ShowDialog(owner);
            else
                wnd.Show();
        }
    }
}
