using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clowd.Installer.Features;
using Clowd.UI.Helpers;

namespace Clowd.UI.Config
{
    class FeatureInstallerControl : StackPanel
    {
        private readonly IFeature _feature;

        Button insButton;
        TextBlock insLabel;

        public FeatureInstallerControl(IFeature feature)
        {
            this._feature = feature;
            this.Orientation = Orientation.Horizontal;

            insButton = new Button();
            insButton.Content = "Install";
            insButton.IsEnabled = false;
            insButton.Click += InsButton_Click;
            Children.Add(insButton);

            insLabel = new TextBlock();
            insLabel.Text = "Not Available";
            insLabel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            insLabel.Margin = new Thickness(10, 0, 0, 0);
            insLabel.Foreground = Brushes.DarkRed;
            Children.Add(insLabel);

            Update();
        }

        private async void InsButton_Click(object sender, RoutedEventArgs e)
        {
            insButton.IsEnabled = false;
            insLabel.Text = "Working...";
            insLabel.Foreground = Brushes.DarkGoldenrod;

            var asset = Assembly.GetEntryAssembly().Location;
            var installed = _feature.CheckInstalled(asset);

            try
            {
                if (installed)
                {
                    await Task.Run(() => _feature.Uninstall(asset));
                }
                else
                {
                    await Task.Run(() => _feature.Install(asset));
                }
            }
            catch (Exception ex)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "An error has occured: " + ex.Message, "Unable to " + (installed ? "uninstall" : "install"));
            }

            Update();
        }

        private void Update()
        {
            if (_feature != null)
            {
                var installed = _feature.CheckInstalled(Assembly.GetEntryAssembly().Location);
                insButton.Content = installed ? "Uninstall" : "Install";
                insButton.IsEnabled = true;
                insLabel.Text = "Status: " + (installed ? "Installed" : "Not installed");
                insLabel.Foreground = installed ? Brushes.DarkGreen : Brushes.DarkRed;
            }
        }
    }
}
