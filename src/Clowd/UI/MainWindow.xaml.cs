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
    public enum MainWindowPage
    {
        NewItem,
        RecentSessions,
        Uploads,
        SettingsGeneral,
        SettingsHotkeys,
        SettingsCapture,
        SettingsEditor,
        SettingsUploads,
        SettingsVideo,
        About,
    }

    public class FakePage : Page { }

    public class MainWindowNavigationItem : NavigationItem
    {
        public MainWindowPage PageType
        {
            get { return _pageType; }
            set
            {
                _pageType = value;
                if (!App.IsDesignMode)
                {
                    Instance = GetPanelForTag(value);
                    Tag = value.ToString();

                    // this has to be a non-null class that derives from 'Page'
                    // but it won't be used, because we've also set 'Instance'
                    Type = typeof(FakePage); 
                }
            }
        }

        private MainWindowPage _pageType;

        private Page GetPanelForTag(MainWindowPage tag)
        {
            Func<Window> getWindow = () => Window.GetWindow(this);

            switch (tag)
            {
                //case MainWindowPage.NewItem:
                //    return new NewItemPage();
                //    break;
                case MainWindowPage.RecentSessions:
                    return new RecentSessionsPage();
                //case MainWindowPage.Uploads:
                //    break;
                case MainWindowPage.SettingsGeneral:
                    return new GeneralSettingsPage();
                case MainWindowPage.SettingsHotkeys:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Hotkeys).GetSettingsPanel();
                case MainWindowPage.SettingsCapture:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Capture).GetSettingsPanel();
                case MainWindowPage.SettingsEditor:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Editor).GetSettingsPanel();
                case MainWindowPage.SettingsUploads:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Uploads).GetSettingsPanel();
                case MainWindowPage.SettingsVideo:
                    return new SettingsControlFactory(getWindow, SettingsRoot.Current.Video).GetSettingsPanel();
                case MainWindowPage.About:
                    return new AboutPage();
                default:
                    return null;
            }
        }
    }

    public partial class MainWindow : SystemThemedWindow
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public static void ShowWindow(MainWindowPage? focusedPage = null)
        {
            var main = App.Current.Windows.Cast<Window>().Select(w => w as MainWindow).Where(w => w != null).FirstOrDefault();
            if (main != null)
            {
                main.PlatformWindow.Activate();
            }
            else
            {
                main = new MainWindow();
                main.Show();
                main.PlatformWindow.Activate();
            }

            if (focusedPage != null)
            {
                main.RootNavigation.Navigate(focusedPage.ToString());
            }
        }
    }
}
