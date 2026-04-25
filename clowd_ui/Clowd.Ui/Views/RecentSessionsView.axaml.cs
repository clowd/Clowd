using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Clowd.Ui.Models;
using Clowd.Ui.ViewModels.Pages;

namespace Clowd.Ui.Views;

public partial class RecentSessionsView : UserControl
{
    public RecentSessionsView()
    {
        InitializeComponent();
    }

    public RecentSessionsView(RecentSessionsViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnNewSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RecentSessionsViewModel vm)
            vm.NewSession();
    }

    private void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not RecentSessionsViewModel vm) return;
        if (sender is ListBox lb && lb.SelectedItem is EditorSession session)
            vm.OpenSession(session);
    }
}
