using Avalonia.Controls;
using Clowd.Ui.ViewModels;

namespace Clowd.Ui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // When the user clicks the X, hide the window and keep the app running
        // in the tray. The tray's "Open Clowd" item will Show() it again. Real
        // shutdowns (tray Exit, OS logoff) come through with a different
        // CloseReason and are allowed to proceed.
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
