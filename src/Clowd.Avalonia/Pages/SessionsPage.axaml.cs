using Avalonia.Controls;
using PropertyChanged.SourceGenerator;

namespace Clowd.Avalonia.Pages
{
    partial class CookiesModel
    {
        [Notify] private int _numberOfCookies;
    }

    public partial class SessionsPage : UserControl
    {
        CookiesModel _model = new CookiesModel();

        public SessionsPage()
        {
            this.DataContext = _model;
            InitializeComponent();
            this.MoreCookies.Click += MoreCookies_Click;
        }

        private void MoreCookies_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            _model.NumberOfCookies++;
        }
    }
}
