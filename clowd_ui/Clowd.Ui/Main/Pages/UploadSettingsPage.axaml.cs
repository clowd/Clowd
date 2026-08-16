using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
// Avalonia 12 moved TryGetTextAsync and friends off IClipboard into extension methods here.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Dialogs;
using Clowd.UI.Helpers;
using Ursa.Controls;

// Ursa ships a Toast of its own; this page means the app's toast helper.
using Toast = Clowd.UI.Helpers.Toast;

namespace Clowd.UI.Pages
{
    public partial class UploadSettingsPage : UserControl, IPageHeaderContent
    {
        // the import/export strip shown beside the page title. The page owns the control but does
        // not host it — the window's header does (see IPageHeaderContent).
        private readonly Control _transferBar;

        public UploadSettingsPage()
        {
            InitializeComponent();
            DataContext = SettingsRoot.Current.Uploads;
            _transferBar = BuildTransferBar();
        }

        public Control HeaderContent => _transferBar;

        private Control BuildTransferBar()
        {
            var import = new Button { Content = "Import" };
            ToolTip.SetTip(import, "Read provider settings from a Clowd settings string on the clipboard");
            import.Click += ImportClicked;

            var export = new Button { Content = "Export" };
            ToolTip.SetTip(export, "Copy provider settings to the clipboard as a Clowd settings string");
            export.Click += ExportClicked;

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Children = { import, export },
            };
        }

        private async void ExportClicked(object sender, RoutedEventArgs e)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            var uploads = SettingsRoot.Current.Uploads;

            var items = uploads.Providers
                .Select(p => new ProviderMultiSelectDialog.Item
                {
                    Key = p.Provider.GetType().Name,
                    Name = p.Provider.Name,
                    Description = p.Provider.Description,
                    Icon = () => p.Provider.Icon,
                    // an enabled provider is the one the user has actually configured, so it is
                    // the one they most likely want to carry to another machine
                    IsCheckedByDefault = p.IsEnabled,
                })
                .ToArray();

            if (items.Length == 0)
            {
                Toast.Show(window, "There are no upload providers to export.", NotificationType.Warning);
                return;
            }

            var chosen = await ProviderMultiSelectDialog.ShowAsync(
                window,
                "Export upload providers",
                "Choose which providers to copy to the clipboard. The copied text is scrambled so it "
                + "means nothing to anyone who stumbles across it, but it is not encrypted with a "
                + "password — it still carries your credentials, so only pass it to people you trust.",
                "Copy",
                items);

            if (chosen == null || chosen.Length == 0)
                return;

            string text;
            try
            {
                var version = typeof(UploadSettingsPage).Assembly.GetName().Version?.ToString();
                var payload = uploads.ExportProviders(chosen, version, DateTimeOffset.UtcNow);
                text = UploadSettingsTransfer.Encode(payload);
            }
            catch (Exception ex)
            {
                SentryConfig.CaptureHandled(ex, "upload.settings-export");
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, ex.Message, "Export failed");
                return;
            }

            var clipboard = window?.Clipboard ?? Toast.GetPrimaryClipboard();
            if (clipboard == null)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "The clipboard is not available.", "Export failed");
                return;
            }

            await ClipboardImpl.SetClipboardText(clipboard, text);
            Toast.Show(window, chosen.Length == 1
                ? "Copied 1 provider to the clipboard."
                : $"Copied {chosen.Length} providers to the clipboard.");
        }

        private async void ImportClicked(object sender, RoutedEventArgs e)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            var uploads = SettingsRoot.Current.Uploads;

            var clipboard = window?.Clipboard ?? Toast.GetPrimaryClipboard();
            string text = null;
            if (clipboard != null)
            {
                try
                {
                    text = await clipboard.TryGetTextAsync();
                }
                catch
                {
                    // a clipboard held open by another app reads as "nothing of ours" here
                }
            }

            if (!UploadSettingsTransfer.TryDecode(text, out var payload) || payload.Providers.Count == 0)
            {
                Toast.Show(window, "No provider settings found in the clipboard.", NotificationType.Warning);
                return;
            }

            var items = payload.Providers
                .Select(kvp =>
                {
                    var info = uploads.GetProviderByTypeName(kvp.Key);
                    Func<System.IO.Stream> icon = info == null ? null : () => info.Provider.Icon;
                    return new ProviderMultiSelectDialog.Item
                    {
                        Key = kvp.Key,
                        Name = info?.Provider.Name ?? kvp.Value.Name ?? kvp.Key,
                        Description = info != null
                            ? info.Provider.Description
                            : "Not supported by this version of Clowd.",
                        Icon = icon,
                        IsAvailable = info != null,
                        IsCheckedByDefault = true,
                    };
                })
                .OrderByDescending(i => i.IsAvailable)
                .ThenBy(i => i.Name, StringComparer.Ordinal)
                .ToArray();

            if (items.All(i => !i.IsAvailable))
            {
                Toast.Show(
                    window,
                    "The clipboard holds Clowd provider settings, but none of those providers exist in this version.",
                    NotificationType.Warning);
                return;
            }

            var chosen = await ProviderMultiSelectDialog.ShowAsync(
                window,
                "Import upload providers",
                "Choose which providers to import. Each one you tick has all of its settings "
                + "replaced by what is in the clipboard — including whether it is enabled and which "
                + "uploads it is the default for.",
                "Import",
                items);

            if (chosen == null || chosen.Length == 0)
                return;

            var imported = 0;
            foreach (var key in chosen)
            {
                if (payload.Providers.TryGetValue(key, out var entry) && uploads.ImportProvider(key, entry))
                    imported++;
            }

            Toast.Show(window, imported == 1
                ? "Imported 1 provider."
                : $"Imported {imported} providers.");
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
            // Use Ursa's Small class so the template's 20px arc is resized too; constraining only
            // the outer LoadingIcon to 14px clips the rotating arc into a square.
            button.Content = TestButtonContent(new LoadingIcon { Classes = { "Small" }, IsLoading = true }, "Testing...");

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
                SentryConfig.CaptureHandledNetwork(ex, "upload.provider-test");
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
