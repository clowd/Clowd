using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Pages;
using WPFUI.Controls;

namespace Clowd.UI
{
    public class MainWindowNavigationItem : NavigationItem
    {
        private class FakePage : Page
        { }

        public SettingsPageTab PageType
        {
            get { return _pageType; }
            set
            {
                _pageType = value;
                if (!App.IsDesignMode)
                {
                    Instance = GetPanelForTag(value);
                    Tag = PageTag = value.ToString();

                    // this has to be a non-null class that derives from 'Page'
                    // but it won't be used, because we've also set 'Instance'
                    Page = typeof(FakePage);
                }
            }
        }

        private SettingsPageTab _pageType;

        private Page GetPanelForTag(SettingsPageTab tag)
        {
            Func<Window> getWindow = () => Window.GetWindow(this);

            switch (tag)
            {
                //case MainWindowPage.NewItem:
                //    return new NewItemPage();
                //    break;
                case SettingsPageTab.RecentSessions:
                    return new RecentSessionsPage();
                //case MainWindowPage.Uploads:
                //    break;
                case SettingsPageTab.SettingsGeneral:
                    return new GeneralSettingsPage();
                case SettingsPageTab.SettingsHotkeys:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Hotkeys).GetSettingsPanel();
                case SettingsPageTab.SettingsCapture:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Capture).GetSettingsPanel();
                case SettingsPageTab.SettingsEditor:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Editor).GetSettingsPanel();
                case SettingsPageTab.SettingsUploads:
                    return new UploadSettingsPage();
                case SettingsPageTab.SettingsVideo:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Video).GetSettingsPanel();
                case SettingsPageTab.About:
                    return new AboutPage();
                default:
                    return null;
            }
        }
    }

    public partial class MainWindow : SystemThemedWindow, ISettingsPage
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public void Open(SettingsPageTab? selectedTab)
        {
            Show();
            PlatformWindow?.Activate();
            if (selectedTab != null)
            {
                RootNavigation.Navigate(selectedTab.ToString());
            }
        }
    }
}
