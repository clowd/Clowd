using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Pages;
using Clowd.Util;
using ModernWpf.Controls;
using ModernWpf.Media.Animation;

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

    public partial class MainWindow : SystemThemedWindow
    {
        private Dictionary<MainWindowPage, FrameworkElement> _tagCache = new Dictionary<MainWindowPage, FrameworkElement>();

        public MainWindow()
        {
            InitializeComponent();

            // set DrillIn page transition
            var trans = new NavigationThemeTransition();
            trans.DefaultNavigationTransitionInfo = new DrillInNavigationTransitionInfo();
            var col = new TransitionCollection();
            col.Add(trans);
            ContentFrame.ContentTransitions = col;
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
                var page = main.FindVisualChildrenOfType<NavigationViewItem>().FirstOrDefault(f => (f.Tag as MainWindowPage?) == focusedPage);
                if (page != null)
                    page.IsSelected = true;
            }
        }

        private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = args.SelectedItem as NavigationViewItem;
            if (selectedItem == null)
                return;

            sender.Header = selectedItem.Content as string;

            var tag = selectedItem.Tag as MainWindowPage?;
            if (!tag.HasValue)
                return;

            if (_tagCache.TryGetValue(tag.Value, out var panel))
            {
                ContentFrame.Navigate(panel);
            }
            else
            {
                var item = GetPanelForTag(tag.Value);
                if (item != null)
                {
                    _tagCache[tag.Value] = item;
                    ContentFrame.Navigate(item);
                }
            }
        }

        private FrameworkElement GetPanelForTag(MainWindowPage tag)
        {
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
                    return new SettingsControlFactory(this, SettingsRoot.Current.Hotkeys).GetSettingsPanel();
                case MainWindowPage.SettingsCapture:
                    return new SettingsControlFactory(this, SettingsRoot.Current.Capture).GetSettingsPanel();
                case MainWindowPage.SettingsEditor:
                    return new SettingsControlFactory(this, SettingsRoot.Current.Editor).GetSettingsPanel();
                case MainWindowPage.SettingsUploads:
                    return new SettingsControlFactory(this, SettingsRoot.Current.Uploads).GetSettingsPanel();
                case MainWindowPage.SettingsVideo:
                    return new SettingsControlFactory(this, SettingsRoot.Current.Video).GetSettingsPanel();
                case MainWindowPage.About:
                    return new AboutPage();
                default:
                    return null;
            }

            //var typesToCheck = new[]
            //{
            //    "Clowd.UI." + tag,
            //    "Clowd.UI.Pages." + tag,
            //    "Clowd.UI." + tag + "Page",
            //    "Clowd.UI.Pages." + tag + "Page",
            //};

            //foreach(var t in typesToCheck)
            //{
            //    var type = Type.GetType(t);
            //    if (type != null && type.IsAssignableTo(typeof(FrameworkElement)))
            //        return (FrameworkElement)Activator.CreateInstance(type);
            //}

            //var settingsPage = typeof(SettingsRoot).GetProperties().FirstOrDefault(f => f.Name == tag);
            //if (settingsPage != null)
            //    return new SettingsControlFactory(this, settingsPage.GetValue(SettingsRoot.Current)).GetSettingsPanel();

            //return null;
        }
    }
}
