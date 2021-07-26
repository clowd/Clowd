using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clowd.Installer;
using Clowd.Installer.Features;

namespace Clowd.Setup.Views
{
    public class UninstallViewModel
    {
        public string InstallationDirectory { get; set; }
        public bool KeepSettings { get; set; }
    }

    public partial class UninstallSplashView : UserControl
    {
        public UninstallSplashView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            var info = ControlPanelInfo.GetInfo(Constants.ClowdAppName, RegistryQuery.CurrentUser);
            MainWindow.Current.SetContent(new DoWorkView(new UninstallViewModel
            {
                InstallationDirectory = info.InstallDirectory,
                KeepSettings = true,
            }));
        }

        private void OnUninstallDeleteEverythingClick(object sender, RoutedEventArgs e)
        {
            var info = ControlPanelInfo.GetInfo(Constants.ClowdAppName, RegistryQuery.CurrentUser);
            MainWindow.Current.SetContent(new DoWorkView(new UninstallViewModel
            {
                InstallationDirectory = info.InstallDirectory,
                KeepSettings = false,
            }));
        }

        private void OnRepairClick(object sender, RoutedEventArgs e)
        {
            var info = ControlPanelInfo.GetInfo(Constants.ClowdAppName, RegistryQuery.CurrentUser);
            MainWindow.Current.SetContent(new DoWorkView(new CustomizeViewModel
            {
                InstallDirectory = info.InstallDirectory,
                FeatureShortcuts = false,
                FeatureAutoStart = true,
                FeatureContextMenu = true,
            }));
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }
    }
}
