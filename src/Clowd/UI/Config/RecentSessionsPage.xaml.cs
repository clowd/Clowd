using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    public partial class RecentSessionsPage : Page
    {
        public TrulyObservableCollection<SessionInfo> Sessions { get; }

        public static readonly DependencyProperty ItemSizeProperty = DependencyProperty.Register(
            "ItemSize", typeof(Size), typeof(RecentSessionsPage), new PropertyMetadata(new Size(110, 75)));

        public Size ItemSize
        {
            get { return (Size)GetValue(ItemSizeProperty); }
            set { SetValue(ItemSizeProperty, value); }
        }

        public static readonly DependencyProperty ListModeProperty = DependencyProperty.Register(
            "ListMode", typeof(bool), typeof(RecentSessionsPage), new PropertyMetadata(true));

        public bool ListMode
        {
            get { return (bool)GetValue(ListModeProperty); }
            set { SetValue(ListModeProperty, value); }
        }

        public RecentSessionsPage()
        {
            Sessions = SessionManager.Current.Sessions;
            InitializeComponent();
            listView.SelectionChanged += ListView_SelectionChanged;
            GalleryList_Click(null, null);
        }

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

        private void GalleryModeReset(double sizeMult)
        {
            btnGalleryLarge.IsChecked = false;
            btnGallerySmall.IsChecked = false;
            btnGalleryMedium.IsChecked = false;
            btnGalleryList.IsChecked = false;

            if (sizeMult == 0)
            {
                ListMode = true;
                ItemSize = new Size(double.PositiveInfinity, 75);
                listView.ItemTemplate = (DataTemplate)FindResource("GalleryItemTemplateList");
            }
            else
            {
                ListMode = false;
                const double textHeight = 26;
                const double b = 11.11111d;
                ItemSize = new Size(b * 16d * sizeMult, b * 10d * sizeMult + textHeight);
                listView.ItemTemplate = (DataTemplate)FindResource("GalleryItemTemplateGrid");
            }
        }

        private void GalleryLarge_Click(object sender, RoutedEventArgs e)
        {
            GalleryModeReset(2);
            btnGalleryLarge.IsChecked = true;
        }

        private void GalleryMedium_Click(object sender, RoutedEventArgs e)
        {
            GalleryModeReset(1.5);
            btnGalleryMedium.IsChecked = true;
        }

        private void GallerySmall_Click(object sender, RoutedEventArgs e)
        {
            GalleryModeReset(1);
            btnGallerySmall.IsChecked = true;
        }

        private void GalleryList_Click(object sender, RoutedEventArgs e)
        {
            GalleryModeReset(0);
            btnGalleryList.IsChecked = true;
        }
    }
}
