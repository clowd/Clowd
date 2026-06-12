using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Pages;
using Ursa.Controls;

namespace Clowd.UI
{
    public partial class MainWindow : SystemThemedWindow, ISettingsPage
    {
        // pages are created lazily and cached for the lifetime of the window (decision table #53).
        private readonly Dictionary<SettingsPageTab, Control> _pages = new();

        // UI-layer explicit-save policy: the settings data classes no longer auto-save, so every
        // category edited through this window is saved when one of its properties changes.
        private readonly HashSet<INotifyPropertyChanged> _autoSaveTargets = new();

        public MainWindow()
        {
            InitializeComponent();
            NavList.SelectionChanged += OnNavSelectionChanged;
            NavList.SelectedItem = NavList.Items.OfType<NavMenuItem>().FirstOrDefault();
        }

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not NavMenuItem item || item.Tag is not string tag)
                return;

            if (!Enum.TryParse<SettingsPageTab>(tag, out var tab))
                return;

            PageTitle.Text = item.Header as string ?? tab.ToString();
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
                SettingsPageTab.SettingsGeneral => CreateGeneralPage(),
                SettingsPageTab.SettingsHotkeys => CreateFactoryPage(getWindow, SettingsRoot.Current.Hotkeys),
                SettingsPageTab.SettingsCapture => CreateFactoryPage(getWindow, SettingsRoot.Current.Capture),
                SettingsPageTab.SettingsEditor => CreateFactoryPage(getWindow, SettingsRoot.Current.Editor),
                SettingsPageTab.SettingsUploads => new UploadsPlaceholderPage(),
                SettingsPageTab.About => new AboutPage(),
                _ => null,
            };

            if (created != null)
                _pages[tab] = created;

            return created;
        }

        private Control CreateGeneralPage()
        {
            AttachAutoSave(SettingsRoot.Current.General);
            return new GeneralSettingsPage();
        }

        private Control CreateFactoryPage(Func<Window> getWindow, object category)
        {
            var panel = new SettingsControlFactory(getWindow, category).GetSettingsPanel();
            AttachAutoSave(category);
            return panel;
        }

        /// <summary>
        /// Saves the settings file whenever a property of <paramref name="obj"/> changes (the
        /// factory's bindings write directly into the category object). Also subscribes one level
        /// of nested INPC property values (e.g. TimeOption), which the factory binds to directly.
        /// </summary>
        private void AttachAutoSave(object obj)
        {
            if (obj is not INotifyPropertyChanged inpc || !_autoSaveTargets.Add(inpc))
                return;

            inpc.PropertyChanged += (_, _) => SettingsService.Save(SettingsRoot.Current);

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(obj))
            {
                if (typeof(INotifyPropertyChanged).IsAssignableFrom(pd.PropertyType))
                    AttachAutoSave(pd.GetValue(obj));
            }
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
            var item = NavList.Items.OfType<NavMenuItem>().FirstOrDefault(i => Equals(i.Tag, tag));
            if (item != null)
                NavList.SelectedItem = item;
        }
    }
}
