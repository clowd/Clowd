using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clowd.Ui.ViewModels.Pages;

namespace Clowd.Ui.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(Constants.GitHubUrl));
        }
    }
}
