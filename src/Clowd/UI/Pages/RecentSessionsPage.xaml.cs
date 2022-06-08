using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    public partial class RecentSessionsPage : Page, INotifyPropertyChanged
    {
        private TrulyObservableCollection<SessionInfo> _sessions;

        public TrulyObservableCollection<SessionInfo> Sessions
        {
            get => _sessions;
            set
            {
                if (_sessions != value)
                {
                    _sessions = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sessions)));
                }
            }
        }

        public RecentSessionsPage()
        {
            Sessions = SessionManager.Current.Sessions;
            InitializeComponent();
            listView.SelectionChanged += ListView_SelectionChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var count = listView.SelectedItems.Count;
            ctxOpenItem.IsEnabled = count == 1;
            ctxCopyItem.IsEnabled = count == 1;
            ctxDeleteItem.IsEnabled = count >= 1;
        }

        private void OpenItemClicked(object sender, RoutedEventArgs e)
        {
            // only one item can be selected here
            var session = listView.SelectedItem as SessionInfo;
            if (session != null)
                SessionManager.Current.OpenSession(session);
        }

        private void CopyItemClicked(object sender, RoutedEventArgs e)
        {
            // only one item can be selected here
            var session = listView.SelectedItem as SessionInfo;
            if (session != null)
                SessionManager.Current.CopySession(session);
        }

        private async void DeleteItemClicked(object sender, RoutedEventArgs e)
        {
            // many items can be selected here
            bool itemOpen = false;
            foreach (SessionInfo session in listView.SelectedItems.OfType<SessionInfo>().ToArray())
            {
                if (session.OpenEditor != null)
                {
                    itemOpen = true;
                }
                else
                {
                    SessionManager.Current.DeleteSession(session);
                }
            }

            if (itemOpen)
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Information, "One or more selected items are currently open and can not be deleted.");
        }

        private void ViewDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement el && el.DataContext is SessionInfo session)
                SessionManager.Current.OpenSession(session);
        }
    }
}
