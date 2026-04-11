using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Clowd.Ui.Views.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public static Task ShowAsync(Window owner, string title, string body)
    {
        var dlg = new MessageDialog
        {
            Title = title,
        };
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        return dlg.ShowDialog(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
