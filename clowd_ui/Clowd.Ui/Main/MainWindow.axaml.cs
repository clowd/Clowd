using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
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

        private DispatcherTimer _saveTimer;

        public MainWindow()
        {
            InitializeComponent();
            NavList.SelectionChanged += OnNavSelectionChanged;
            NavList.SelectedItem = NavList.Items.OfType<NavMenuItem>().FirstOrDefault(i => !i.IsSeparator);
            RestoreWindowBounds();
        }

        /// <summary>Restores the last window placement when it still intersects a connected
        /// screen; otherwise the default 800x600 CenterScreen applies.</summary>
        private void RestoreWindowBounds()
        {
            var saved = SettingsRoot.Current?.General?.MainWindowBounds;
            if (String.IsNullOrEmpty(saved))
                return;

            var parts = saved.Split(',');
            if (parts.Length != 4
                || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
                || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var w)
                || !double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var h))
                return;

            if (w < MinWidth || h < MinHeight)
                return;

            var rect = new Avalonia.PixelRect(x, y, (int)w, (int)h);
            if (!Screens.All.Any(s => s.WorkingArea.Intersects(rect)))
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new Avalonia.PixelPoint(x, y);
            Width = w;
            Height = h;
        }

        private void SaveWindowBounds()
        {
            if (SettingsRoot.Current?.General == null || WindowState != WindowState.Normal)
                return;

            SettingsRoot.Current.General.MainWindowBounds = String.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{Position.X},{Position.Y},{Width},{Height}");
        }

        private void NewImage_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            EditorWindow.ShowSession(null);
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
                SettingsPageTab.SettingsHotkeys => CreateFactoryPage(getWindow, SettingsRoot.Current.Hotkeys,
                    "Click a shortcut, then press the new key combination — Esc cancels. Use ✕ to remove a shortcut."),
                SettingsPageTab.SettingsCapture => CreateFactoryPage(getWindow, SettingsRoot.Current.Capture),
                SettingsPageTab.SettingsRecording => CreateFactoryPage(getWindow, SettingsRoot.Current.Recording,
                    "Recording settings apply to your next recording. Changes made while a recording is in progress take effect only after it finishes."),
                SettingsPageTab.SettingsEditor => CreateFactoryPage(getWindow, SettingsRoot.Current.Editor),
                SettingsPageTab.SettingsUploads => CreateUploadsPage(),
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

        private Control CreateUploadsPage()
        {
            // SettingsUpload mirrors every provider edit (enable, defaults, credentials) into
            // ProviderConfig and raises PropertyChanged, so this persists all of them.
            AttachAutoSave(SettingsRoot.Current.Uploads);
            return new UploadSettingsPage();
        }

        private Control CreateFactoryPage(Func<Window> getWindow, object category, string introText = null)
        {
            var panel = new SettingsControlFactory(getWindow, category).GetSettingsPanel(introText);
            AttachAutoSave(category);
            return panel;
        }

        /// <summary>
        /// Saves the settings file whenever a property of <paramref name="obj"/> changes (the
        /// factory's bindings write directly into the category object). Also subscribes one level
        /// of nested INPC property values (e.g. TimeOption), which the factory binds to directly.
        /// Saves are debounced: a single user action can raise several PropertyChanged events
        /// (e.g. changing the default upload provider syncs every provider), and a save failure
        /// must not throw back through the control's property setter.
        /// </summary>
        private void AttachAutoSave(object obj)
        {
            if (obj is not INotifyPropertyChanged inpc || !_autoSaveTargets.Add(inpc))
                return;

            inpc.PropertyChanged += (_, _) => QueueSave();

            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(obj))
            {
                if (typeof(INotifyPropertyChanged).IsAssignableFrom(pd.PropertyType))
                    AttachAutoSave(pd.GetValue(obj));
            }
        }

        private void QueueSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _saveTimer.Tick += (_, _) => FlushPendingSave();
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushPendingSave()
        {
            _saveTimer?.Stop();

            try
            {
                SettingsService.Save(SettingsRoot.Current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save settings: " + ex);
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SaveWindowBounds();
            FlushPendingSave();

            base.OnClosing(e);
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
