using System;
using System.Collections.Generic;
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
    public partial class RecentSessionsPage : Page
    {
        public RecentSessionsPage()
        {
            InitializeComponent();
            SessionUtil.WeakSessionsUpdated += SessionUtil_SessionsUpdated;
            UpdateSessions();
            listView.SelectionChanged += ListView_SelectionChanged;
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            multiCommandBar.Visibility = listView.SelectedItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SessionUtil_SessionsUpdated(object sender, EventArgs e)
        {
            //UpdateSessions();
        }

        private void UpdateSessions()
        {
            var sessions = SessionUtil.GetSavedSessions().ToList();
            var source = new CollectionViewSource();
            source.Source = sessions;
            source.GroupDescriptions.Add(new PropertyGroupDescription()
            {
                Converter = new RecentTimeGroupKeyConverter(),
                PropertyName = nameof(SessionInfo.Created)
            });
            source.SortDescriptions.Add(new System.ComponentModel.SortDescription()
            {
                Direction = System.ComponentModel.ListSortDirection.Descending,
                PropertyName = nameof(SessionInfo.Created)
            });
            listView.ItemsSource = source.View;
        }

        private async void DeleteSelectedFlyoutClicked(object sender, RoutedEventArgs e)
        {
            var f = ModernWpf.Controls.FlyoutService.GetFlyout(cmdBtnDeleteSelected) as ModernWpf.Controls.Flyout;
            f?.Hide();

            bool itemOpen = false;
            foreach (SessionInfo session in listView.SelectedItems)
            {
                if (session.ActiveWindowId != null)
                {
                    itemOpen = true;
                }
                else
                {
                    session.Delete();
                }
            }

            if (itemOpen)
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Information, "One or more selected items are currently open and can not be deleted.");
        }

        private void OpenItemClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SessionInfo session)
                session.Open();
        }

        private void CopyItemClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SessionInfo session)
                session.Copy();
        }

        private void DeleteItemClicked(object sender, RoutedEventArgs e)
        {
            // only one item can be selected here
            DeleteSelectedFlyoutClicked(sender, e);
        }

        private void ViewDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement el && el.DataContext is SessionInfo session)
                session.Open();
        }
    }
}
