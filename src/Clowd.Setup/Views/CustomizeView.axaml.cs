using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Clowd.Setup.Views
{
    public class CustomizeViewModel : ViewModelBase
    {
        public string InstallDirectory
        {
            get => _installDir;
            set => RaiseAndSetIfChanged(ref _installDir, value);
        }

        public bool FeatureAutoStart
        {
            get => _featureAutoStart;
            set => RaiseAndSetIfChanged(ref _featureAutoStart, value);
        }

        public bool FeatureContextMenu
        {
            get => _featureContextMenu;
            set => RaiseAndSetIfChanged(ref _featureContextMenu, value);
        }

        public bool FeatureShortcuts
        {
            get => _featureShortcuts;
            set => RaiseAndSetIfChanged(ref _featureShortcuts, value);
        }

        private string _installDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private bool _featureAutoStart = true;
        private bool _featureContextMenu = true;
        private bool _featureShortcuts = true;
    }

    public partial class CustomizeView : UserControl
    {
        public CustomizeView()
        {
            InitializeComponent();
            this.DataContext = new CustomizeViewModel();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var ctx = (CustomizeViewModel)this.DataContext;
            OpenFolderDialog f = new OpenFolderDialog();
            f.Directory = ctx.InstallDirectory;
            var result = await f.ShowAsync(MainWindow.Current);
            if (!String.IsNullOrWhiteSpace(result))
                ctx.InstallDirectory = result;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }

        private void OnNextClick(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.SetContent(new DoWorkView((CustomizeViewModel)this.DataContext));
        }
    }
}
