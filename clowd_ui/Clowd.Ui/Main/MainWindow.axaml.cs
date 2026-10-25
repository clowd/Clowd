using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Config;
using Clowd.UI.Helpers;
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
        private SettingsPageTab? _selectedTab;

        public MainWindow()
        {
            InitializeComponent();
            SettingsRoot.Current.Hotkeys.PropertyChanged += OnHotkeyPropertyChanged;
            SettingsRoot.Current.General.PropertyChanged += OnGeneralPropertyChanged;
            BindRecordingMode();
            InitializeNavBadges();
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
                || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var w)
                || !double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var h))
                return;

            if (w < MinWidth || h < MinHeight)
                return;

            var rect = new Avalonia.PixelRect(x, y, (int)w, (int)h);
            if (!Clowd.UI.Helpers.DesktopScreens.All(this).Any(s => s.WorkingArea.Intersects(rect)))
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

        private void NewVideo_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Clowd.UI.VideoEditor.VideoEditorWindow.ShowBlankProject();
        }

        private void NewCapture_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Clowd.App.Current.StartCapture();
        }

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not NavMenuItem item || item.Tag is not string tag)
                return;

            if (!Enum.TryParse<SettingsPageTab>(tag, out var tab))
                return;

            _selectedTab = tab;
            // System's and About's nav entries are renamed while they carry a badge (RenderNavBadges);
            // the pages themselves keep their own names.
            PageTitle.Text = tab switch
            {
                SettingsPageTab.SettingsSystem => "System",
                SettingsPageTab.About => "About",
                _ => item.Header as string ?? tab.ToString(),
            };
            SetPageIntro(tab);

            var page = GetPageForTab(tab);
            PageHost.Content = page;

            // a page that carries its own header control (the Recent filter) hands the same instance
            // over every time it is selected; the slot is emptied for the pages that carry none.
            PageHeaderContent.Content = (page as IPageHeaderContent)?.HeaderContent;
        }

        private void SetPageIntro(SettingsPageTab tab)
        {
            var intro = tab switch
            {
                SettingsPageTab.SettingsSystem =>
                    "Choose how Clowd looks and behaves.",
                SettingsPageTab.SettingsHotkeys =>
                    "Global shortcuts work from anywhere, even while Clowd is in the background. An action can have more than one.",
                SettingsPageTab.SettingsCapture => GetCaptureIntroText(),
                SettingsPageTab.SettingsRecording =>
                    "Changes apply to your next recording.",
                SettingsPageTab.SettingsShareRegion =>
                    "A shared region mirrors part of your screen into a window a meeting app can share.",
                SettingsPageTab.SettingsDrawOnScreen =>
                    "These settings style the keys shown by the Draw on Screen toolbar's keyboard button.",
                SettingsPageTab.SettingsEditor =>
                    "Choose how the image and video editors look and how long their sessions are kept.",
                SettingsPageTab.SettingsUploads =>
                    "Enable the destinations you want, then click a file type to make one the default for it.",
                _ => null,
            };

            PageIntro.Text = intro;
            PageIntro.IsVisible = !String.IsNullOrEmpty(intro);
        }

        private static string GetCaptureIntroText()
        {
            // the first of the action's gestures: the caption names one key, not the whole list
            var gesture = SettingsRoot.Current.Hotkeys.CaptureRegionShortcut.FirstOrDefault()?.ToString();
            return String.IsNullOrEmpty(gesture)
                ? "These settings apply to the capture overlay, opened from the tray menu."
                : $"These settings apply to the capture overlay, opened with {gesture}.";
        }

        /// <summary>The nav's status badges: the System entry turns into a red alert reading
        /// "Permission Needed" while a macOS permission is missing (the Permissions group heads that
        /// page), and About turns yellow and reads "Update Available" while there is an update to
        /// download or restart into. A badged entry's label is semibold in a softer step of the icon's
        /// color (ClowdNav*TextBrush), and its icon drops the nav's 0.8 dimming.
        /// Both sources are static and outlive this window, so the handlers come off in
        /// <see cref="OnClosed"/>; permissions are also re-read on activation, since they are
        /// granted over in System Settings.</summary>
        private void InitializeNavBadges()
        {
            UpdateService.Default.StateChanged += OnNavBadgeSourceChanged;
            if (MacPermissions.IsRelevant)
            {
                MacPermissions.StateChanged += OnNavBadgeSourceChanged;
                Activated += OnActivatedRenderNavBadges;
            }

            RenderNavBadges();
        }

        private void OnNavBadgeSourceChanged(object sender, EventArgs e) => Dispatcher.UIThread.Post(RenderNavBadges);

        private void OnActivatedRenderNavBadges(object sender, EventArgs e) => RenderNavBadges();

        private IDisposable _systemIconBrush;
        private IDisposable _aboutIconBrush;

        private void RenderNavBadges()
        {
            var permissionMissing = MacPermissions.IsRelevant
                                    && !(MacPermissions.IsGranted(MacPermission.ScreenRecording)
                                         && MacPermissions.IsGranted(MacPermission.Accessibility));
            SystemNavItem.Header = NavHeader(MacPermissions.NavLabel(permissionMissing), permissionMissing ? "ClowdNavDangerTextBrush" : null);
            SystemNavIcon.Data = (Geometry)this.FindResource(permissionMissing ? "IconNavAttention" : "IconNavGeneral");
            SetIconBrush(SystemNavIcon, ref _systemIconBrush, permissionMissing ? "SemiColorDanger" : null);

            var updateWaiting = UpdateService.Default.State
                is UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.ReadyToRestart;
            AboutNavItem.Header = NavHeader(updateWaiting ? "Update Available" : "About", updateWaiting ? "ClowdNavWarningTextBrush" : null);
            AboutNavIcon.Data = (Geometry)this.FindResource(updateWaiting ? "IconNavUpdate" : "IconNavAbout");
            SetIconBrush(AboutNavIcon, ref _aboutIconBrush, updateWaiting ? "SemiYellow5" : null);
        }

        /// <summary>A nav entry's label: the plain string normally, or — badged — a semibold TextBlock
        /// in the badge brush. A TextBlock rather than styling the item, because Ursa's header
        /// presenter sets its own foreground per state and would win over the item's. The brush is a
        /// dynamic resource so it follows the theme, and is released when the block leaves the tree.</summary>
        private static object NavHeader(string text, string brushKey)
        {
            if (brushKey == null)
                return text;

            var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold };
            block.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brushKey));
            return block;
        }

        /// <summary>Tints a nav icon with a theme brush (followed across light/dark) at full opacity,
        /// or hands it back to the nav's own styling when <paramref name="resourceKey"/> is null.</summary>
        private void SetIconBrush(PathIcon icon, ref IDisposable binding, string resourceKey)
        {
            binding?.Dispose();
            binding = null;
            icon.ClearValue(TemplatedControl.ForegroundProperty);
            icon.ClearValue(OpacityProperty);

            if (resourceKey != null)
            {
                binding = icon.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable(resourceKey));
                icon.Opacity = 1;
            }
        }

        /// <summary>Recording Off hides the video editor everywhere, this window's "Video" button
        /// included; the image button then takes the whole row (the row is "*,8,*", so spanning
        /// all three columns is what makes it full width). Bindings, not a subscription: this
        /// window is recreated every time it is reopened, the settings object never is.</summary>
        private void BindRecordingMode()
        {
            var recording = SettingsRoot.Current.Recording;
            NewVideoButton.Bind(IsVisibleProperty, new Binding(nameof(SettingsRecording.IsEnabled)) { Source = recording });
            NewImageButton.Bind(Grid.ColumnSpanProperty, new Binding(nameof(SettingsRecording.IsEnabled))
            {
                Source = recording,
                Converter = new FuncValueConverter<bool, int>(enabled => enabled ? 1 : 3),
            });
        }

        private void OnHotkeyPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_selectedTab == SettingsPageTab.SettingsCapture
                && (String.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsHotkey.CaptureRegionShortcut)))
            {
                PageIntro.Text = GetCaptureIntroText();
            }
        }

        private Control GetPageForTab(SettingsPageTab tab)
        {
            if (_pages.TryGetValue(tab, out var cached))
                return cached;

            Func<Window> getWindow = () => this;

            Control created = tab switch
            {
                SettingsPageTab.RecentSessions => new RecentSessionsPage(),
                SettingsPageTab.SettingsSystem => CreateSystemPage(),
                // saves nothing itself: every hotkey change is saved by HotkeyManager as it is made
                SettingsPageTab.SettingsHotkeys => new HotkeySettingsPage(),
                SettingsPageTab.SettingsCapture => CreateFactoryPage(getWindow, SettingsRoot.Current.Capture),
                SettingsPageTab.SettingsRecording => CreateFactoryPage(getWindow, SettingsRoot.Current.Recording),
                SettingsPageTab.SettingsShareRegion => CreateFactoryPage(getWindow, SettingsRoot.Current.ShareRegion),
                SettingsPageTab.SettingsDrawOnScreen => CreateFactoryPage(getWindow, SettingsRoot.Current.DrawOnScreen),
                SettingsPageTab.SettingsEditor => CreateFactoryPage(getWindow, SettingsRoot.Current.Editor),
                SettingsPageTab.SettingsUploads => CreateUploadsPage(),
                SettingsPageTab.About => CreateAboutPage(),
                _ => null,
            };

            if (created != null)
                _pages[tab] = created;

            return created;
        }

        private Control CreateSystemPage()
        {
            AttachAutoSave(SettingsRoot.Current.General);
            return new SystemSettingsPage();
        }

        private Control CreateAboutPage()
        {
            // the Updates group edits the update options, which are persisted in SettingsGeneral.
            AttachAutoSave(SettingsRoot.Current.General);
            return new AboutPage();
        }

        private Control CreateUploadsPage()
        {
            // SettingsUpload mirrors every provider edit (enable, defaults, credentials) into
            // ProviderConfig and raises PropertyChanged, so this persists all of them.
            AttachAutoSave(SettingsRoot.Current.Uploads);
            return new UploadSettingsPage();
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
                SentryConfig.CaptureHandled(ex, "settings.save");
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SaveWindowBounds();
            FlushPendingSave();

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SettingsRoot.Current.Hotkeys.PropertyChanged -= OnHotkeyPropertyChanged;
            SettingsRoot.Current.General.PropertyChanged -= OnGeneralPropertyChanged;
            UpdateService.Default.StateChanged -= OnNavBadgeSourceChanged;
            MacPermissions.StateChanged -= OnNavBadgeSourceChanged;
            base.OnClosed(e);
        }

        /// <summary>
        /// Rebuilds this window when the title bar setting is toggled, so the user sees their
        /// choice rather than being told to restart. A window's chrome is fixed in
        /// <see cref="SystemThemedWindow"/>'s constructor, so a new window is the only way to show
        /// it. Editors already open are deliberately left alone: they keep the look they were born
        /// with until the user closes them, which is what the setting's description promises.
        /// </summary>
        private void OnGeneralPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SettingsGeneral.ExtendIntoTitleBar))
                return;

            // Posted, not run inline: this fires from the toggle's own PropertyChanged, and closing
            // the window here would pull the control that raised it out of the tree mid-notify.
            // Reopening goes through PageManager so the singleton bookkeeping stays honest — Close
            // evicts the cached instance, and the next GetSettingsPage builds a fresh one.
            var tab = _selectedTab;
            Dispatcher.UIThread.Post(() =>
            {
                Close();
                PageManager.Current.GetSettingsPage().Open(tab);
            });
        }

        public void Open(SettingsPageTab? selectedTab = null)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();
            // Activate alone loses to the foreground lock when what asked was the tray menu or a
            // hotkey: the window turns active on its own thread but stays behind the app in front.
            WindowNativeExtensions.ForceForeground(this);

            if (selectedTab != null)
                SelectTab(selectedTab.Value);
        }

        /// <summary>Opens the window on the Recent tab and walks the user to
        /// <paramref name="session"/>'s row — selected, scrolled into view and pulsed. Used by
        /// actions that create a Recents entry from elsewhere (the video editor's Render button),
        /// where the entry is the only visible sign anything happened.</summary>
        public void OpenRecents(SessionInfo session)
        {
            // selecting the tab creates the page synchronously (OnNavSelectionChanged), so the
            // cache lookup below finds it even on the first open.
            Open(SettingsPageTab.RecentSessions);

            if (_pages.TryGetValue(SettingsPageTab.RecentSessions, out var page)
                && page is RecentSessionsPage recents)
            {
                recents.FocusSession(session);
            }
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
