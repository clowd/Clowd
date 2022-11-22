using System;
using System.Linq;
using Avalonia.Controls;
using Clowd.Avalonia.Pages;
using Clowd.Config;
using FluentAvalonia.UI.Controls;

namespace Clowd.Avalonia.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            DataContext = SettingsRoot.Current;
            InitializeComponent();
            Nav.ItemInvoked += OnNavigationViewItemInvoked;
            FrameView.Navigate(typeof(SessionsPage));
        }

        private void OnNavigationViewItemInvoked(object sender, NavigationViewItemInvokedEventArgs e)
        {
            if (e.InvokedItemContainer is NavigationViewItem nvi)
            {
                var pageType = Type.GetType("Clowd.Avalonia.Pages." + nvi.Tag);
                if (pageType != null)
                {
                    FrameView.Navigate(pageType, null, e.RecommendedNavigationTransitionInfo);
                }
                else
                {
                    nvi.IsEnabled = false;
                }
            }
        }
    }
}
