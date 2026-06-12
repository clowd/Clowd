using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Pages;

namespace Clowd.UI
{
    public partial class MainWindow : SystemThemedWindow, ISettingsPage
    {
        // pages are created lazily and cached for the lifetime of the window (decision table #53).
        private readonly Dictionary<SettingsPageTab, Control> _pages = new();

        public MainWindow()
        {
            InitializeComponent();
            NavList.SelectionChanged += OnNavSelectionChanged;
            NavList.SelectedIndex = 0;
        }

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag)
                return;

            if (!Enum.TryParse<SettingsPageTab>(tag, out var tab))
                return;

            PageTitle.Text = item.Content as string ?? tab.ToString();
            PageHost.Content = GetPageForTab(tab);
        }

        private Control GetPageForTab(SettingsPageTab tab)
        {
            if (_pages.TryGetValue(tab, out var cached))
                return cached;

            Func<Window> getWindow = () => this;

            Control created = tab switch
            {
                SettingsPageTab.RecentSessions => new RecentSessionsPage(),
                SettingsPageTab.SettingsGeneral => new GeneralSettingsPage(),
                SettingsPageTab.SettingsHotkeys => new SettingsControlFactory(getWindow, SettingsRoot.Current.Hotkeys).GetSettingsPanel(),
                SettingsPageTab.SettingsCapture => new SettingsControlFactory(getWindow, SettingsRoot.Current.Capture).GetSettingsPanel(),
                SettingsPageTab.SettingsEditor => new SettingsControlFactory(getWindow, SettingsRoot.Current.Editor).GetSettingsPanel(),
                SettingsPageTab.SettingsUploads => new UploadsPlaceholderPage(),
                SettingsPageTab.SettingsVideo => new SettingsControlFactory(getWindow, SettingsRoot.Current.Video).GetSettingsPanel(),
                SettingsPageTab.About => new AboutPage(),
                _ => null,
            };

            if (created != null)
                _pages[tab] = created;

            return created;
        }

        public void Open(SettingsPageTab? selectedTab = null)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();

            if (selectedTab != null)
                SelectTab(selectedTab.Value);
        }

        private void SelectTab(SettingsPageTab tab)
        {
            var tag = tab.ToString();
            var item = NavList.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));
            if (item != null)
                NavList.SelectedItem = item;
        }
    }
}
