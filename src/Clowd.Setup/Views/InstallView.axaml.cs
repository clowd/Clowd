using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Clowd.Setup.Views
{
    public partial class InstallView : UserControl
    {
        public InstallView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnInstallClick(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.SetContent(new DoWorkView(new CustomizeViewModel()));
        }

        private void OnCustomizeClick(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.SetContent(new CustomizeView());
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }
    }
}
