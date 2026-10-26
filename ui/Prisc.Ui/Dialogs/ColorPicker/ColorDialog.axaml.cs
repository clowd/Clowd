using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.UI.Converters;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The "Color Wheel" window (COLOR_VIEWER_REWORK.md): a top bar with the Initial / Selected
    /// colors, a rail of four tabs (Simple, Image, Palette, OKLCH) and the footer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two modes (brief §9). As a picker (<c>asDialog</c>) it opens on the caller's color with OK and
    /// Cancel, and OK returns <see cref="SelectedColor"/>. As a viewer (from the tray, or after a
    /// screen color pick) it often has no initial color, and its footer is a single Close. Either
    /// way the tabs' copy buttons are always live and never close the window.
    /// </para>
    /// <para>
    /// Selection model (brief §8): each tab reports its own Selected color
    /// (<see cref="ColorDialogTab.SelectedColor"/>); <see cref="SelectedColor"/> follows whichever
    /// tab is showing, and the Selected chip and OK follow it. Tabs move between
    /// each other through <see cref="ShowInSimple"/>, <see cref="ShowInPalette"/> and <see cref="ShowInOklch"/>.
    /// </para>
    /// </remarks>
    public partial class ColorDialog : SystemThemedWindow
    {
        /// <summary>Swatches, sliders and a hex field — chrome rather than prose, so the
        /// backdrop is worth more here than the sharper text an opaque window would give.</summary>
        protected override bool AllowMicaInLightTheme => true;

        /// <summary>The color the dialog was opened with, fixed for the session; null when there was
        /// none (a viewer opened from the tray).</summary>
        public Color? InitialColor { get; }

        /// <summary>The active tab's Selected color. While the active tab has no selection (the
        /// Image tab before an image is loaded) it keeps the last one it had.</summary>
        public Color SelectedColor { get; private set; }

        /// <summary>Raised whenever <see cref="SelectedColor"/> changes, live during drags too.</summary>
        public event EventHandler SelectedColorChanged;

        /// <summary>True for the picker (OK / Cancel), false for the viewer (Close).</summary>
        public bool IsDialogMode { get; }

        public bool? MyDialogResult { get; private set; }

        public ColorSimpleTab SimpleTab => simpleTab;
        public ColorImageTab ImageTab => imageTab;
        public ColorPaletteTab PaletteTab => paletteTab;
        public ColorOkLchTab OkLchTab => oklchTab;

        /// <summary>The tab showing in the content surface; setting it is the rail's click.</summary>
        public ColorDialogTabKind ActiveTab
        {
            get => (ColorDialogTabKind)Math.Max(0, rail.SelectedIndex);
            set => rail.SelectedIndex = (int)value;
        }

        // the XAML loader needs a parameterless constructor (AVLN3001 otherwise)
        public ColorDialog() : this(null)
        { }

        public ColorDialog(Color? initialColor, bool asDialog = true) : base(applyDefaultSize: false)
        {
            InitialColor = initialColor;
            IsDialogMode = asDialog;

            InitializeComponent();

            foreach (var tab in Tabs)
            {
                tab.Host = this;
                tab.SelectedColorChanged += (s, _) =>
                {
                    if (ReferenceEquals(s, ActiveTabControl))
                        SyncSelection();
                };
            }

            if (initialColor is { } initial)
            {
                simpleTab.SetColor(initial);
                oklchTab.SetColor(initial);
                PaintChip(initialChip, initialHex, initial);
            }

            btnInitial.Click += (_, _) => RestoreInitial();
            btnCopy.Click += async (_, _) => await CopySelectedHex();
            btnOpenPalette.Click += (_, _) => ShowInPalette(new HarmonyPalette(paletteTab.Preset, HsvColor.FromColor(SelectedColor)));
            btnOpenSimple.Click += (_, _) => ShowInSimple(SelectedColor);
            btnOpenOklch.Click += (_, _) => ShowInOklch(SelectedColor);
            UpdateOpenButtons();
            WireEyedropper();

            BuildFooter();

            rail.SelectedIndex = 0;
            rail.SelectionChanged += (_, _) => OnActiveTabChanged();
            rail.KeyDown += OnRailKeyDown;

            // the bar drags the window (it is the title bar on macOS, and sits right under it on
            // Windows), and on macOS carries the traffic lights, as the editors' bars do
            EnableTitleBarDrag(topBar);
            CenterMacTrafficLightsIn(topBar);
            tabNameText.Text = TabNames[0];
            foreach (var name in TabNames)
                tabNameHost.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.Bold, Opacity = 0 });

            AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

            SyncSelection();
        }

        // the tab an eyedropper drag is sampling into, from its start to its end
        private ColorDialogTab _eyedropperTab;

        private DispatcherTimer _copiedTimer;
        private Geometry _copyIcon;

        private ColorDialogTab[] Tabs => new ColorDialogTab[] { simpleTab, imageTab, paletteTab, oklchTab };

        private ColorDialogTab ActiveTabControl => Tabs[(int)ActiveTab];

        // what the top bar calls each tab: the rail's labels, in rail order
        private static readonly string[] TabNames = { "Simple", "Image", "Palette", "Oklch" };

        /// <summary>Switches to the Simple tab with <paramref name="color"/> as its current color
        /// (a palette bar's Go to Simple, the top bar's Initial and Open in Simple).</summary>
        public void ShowInSimple(Color color)
        {
            simpleTab.SetColor(color);
            ActiveTab = ColorDialogTabKind.Simple;
            SyncSelection();
        }

        /// <summary>Switches to the Oklch tab with <paramref name="color"/> as its current color
        /// (the top bar's Open in Oklch).</summary>
        public void ShowInOklch(Color color)
        {
            oklchTab.SetColor(color);
            ActiveTab = ColorDialogTabKind.OkLch;
            SyncSelection();
        }

        /// <summary>Switches to the Palette tab showing <paramref name="palette"/>
        /// (the Image tab's "To closest palette", or the selected color in the current preset).</summary>
        public void ShowInPalette(HarmonyPalette palette)
        {
            paletteTab.LoadHarmony(palette);
            ActiveTab = ColorDialogTabKind.Palette;
            SyncSelection();
        }

        /// <summary>Puts the initial color back as the current color, on the Simple tab.</summary>
        public void RestoreInitial()
        {
            if (InitialColor is { } initial)
                ShowInSimple(initial);
        }

        /// <summary>
        /// Confirms the picker with <see cref="SelectedColor"/>, recording it in the shared recent
        /// list: OK, Enter, and a double-clicked swatch. The viewer has nothing to confirm, so there
        /// this does nothing and the window stays open.
        /// </summary>
        public void Accept()
        {
            if (!IsDialogMode)
                return;

            RecentColorHistory.Add(SelectedColor);
            MyDialogResult = true;
            Close(true);
        }

        /// <summary>Puts <paramref name="text"/> on the clipboard (the tabs' Copy buttons).</summary>
        public Task CopyTextAsync(string text) => ClipboardImpl.SetClipboardText(Clipboard, text);

        /// <summary>
        /// Shows the dialog: modal (ShowDialog) when an owner is supplied, otherwise non-modal
        /// with the result delivered when the window closes.
        /// </summary>
        public Task<bool?> ShowAsync(Window owner)
        {
            if (owner != null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return ShowDialog<bool?>(owner);
            }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var tcs = new TaskCompletionSource<bool?>();
            Closed += (_, _) => tcs.TrySetResult(MyDialogResult);
            Show();
            Activate();
            return tcs.Task;
        }

        /// <summary>
        /// Picker: OK and Cancel, affirmative first on Windows (as every other Clowd dialog has it)
        /// and trailing on macOS. Viewer: Close alone. Enter is OK and Escape is Cancel or Close.
        /// </summary>
        private void BuildFooter()
        {
            footer.Children.Clear();

            if (!IsDialogMode)
            {
                btnClose.IsCancel = true;
                btnClose.Click += (_, _) => Close();
                footer.Children.Add(btnClose);
                return;
            }

            btnOK.IsDefault = true;
            btnCancel.IsCancel = true;
            btnOK.Click += (_, _) => Accept();
            btnCancel.Click += (_, _) =>
            {
                MyDialogResult = false;
                Close(false);
            };

            if (OperatingSystem.IsMacOS())
            {
                footer.Children.Add(btnCancel);
                footer.Children.Add(btnOK);
            }
            else
            {
                footer.Children.Add(btnOK);
                footer.Children.Add(btnCancel);
            }
        }

        private void OnActiveTabChanged()
        {
            var active = ActiveTabControl;
            foreach (var tab in Tabs)
                tab.IsVisible = ReferenceEquals(tab, active);

            tabNameText.Text = TabNames[(int)ActiveTab];
            UpdateOpenButtons();

            SyncSelection();
            active.OnShown();
        }

        /// <summary>The top bar's Open in Simple / Palette / Oklch: each is hidden on the tab it
        /// would open, so the Image tab shows all three and the others show the other two.</summary>
        private void UpdateOpenButtons()
        {
            btnOpenSimple.IsVisible = ActiveTab != ColorDialogTabKind.Simple;
            btnOpenPalette.IsVisible = ActiveTab != ColorDialogTabKind.Palette;
            btnOpenOklch.IsVisible = ActiveTab != ColorDialogTabKind.OkLch;
        }

        /// <summary>Takes the active tab's selection as the dialog's, and repaints the Selected chip.</summary>
        private void SyncSelection()
        {
            if (ActiveTabControl.SelectedColor is not { } color || color == SelectedColor && selectedChip.Background != null)
                return;

            SelectedColor = color;
            PaintChip(selectedChip, selectedHex, color);
            // Initial only when there is something to compare: shown beside an identical Selected
            // it is noise (and restoring it would do nothing)
            btnInitial.IsVisible = InitialColor is { } initial && initial != color;
            SelectedColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void PaintChip(Border chip, TextBlock hex, Color color)
        {
            chip.Background = new SolidColorBrush(color);
            hex.Text = ColorTextHelper.GetHex(color);
            // a translucent fill shows the light checkerboard through it, so judge the ink on that
            var a = color.A / 255d;
            byte Over(byte c) => (byte)Math.Round(c * a + 230 * (1 - a));
            hex.Foreground = ColorDialogInk.For(Color.FromRgb(Over(color.R), Over(color.G), Over(color.B)));
        }

        private async Task CopySelectedHex()
        {
            await CopyTextAsync(ColorTextHelper.GetHex(SelectedColor));

            // the icon turns into a check for a moment, as the Simple tab's copy buttons do
            if (_copiedTimer == null)
            {
                _copyIcon = btnCopy.IconPath;
                _copiedTimer = new DispatcherTimer { Interval = LineIcon.CopiedFeedbackDuration };
                _copiedTimer.Tick += (_, _) =>
                {
                    _copiedTimer.Stop();
                    btnCopy.IconPath = _copyIcon;
                };
            }

            _copiedTimer.Stop();
            if (this.TryFindResource("IconCheckmark", out var check) && check is Geometry geometry)
                btnCopy.IconPath = geometry;
            _copiedTimer.Start();
        }

        /// <summary>
        /// The top bar's eyedropper samples into the active tab, live while dragging: the Simple
        /// tab's color, or the Palette tab's base. The Image tab has no one color to set, so a drag
        /// started there switches to Simple first. Samples are always opaque: what was picked off
        /// the screen is what was seen, and a translucent brush would not reproduce it.
        /// </summary>
        private void WireEyedropper()
        {
            static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);

            btnEyedropper.Started += () =>
            {
                if (!ActiveTabControl.AcceptsEyedropper)
                    ActiveTab = ColorDialogTabKind.Simple;

                _eyedropperTab = ActiveTabControl;
                _eyedropperTab.BeginEyedropper();
            };
            btnEyedropper.Preview += c => _eyedropperTab?.PreviewEyedropper(Opaque(c));
            btnEyedropper.Picked += c => EndEyedropper(Opaque(c));
            btnEyedropper.Canceled += () => EndEyedropper(null);

            void EndEyedropper(Color? picked)
            {
                var tab = _eyedropperTab;
                _eyedropperTab = null;
                tab?.EndEyedropper(picked);
            }
        }

        /// <summary>The rail is a vertical tab list: Up and Down move between tabs (wrapping), Home
        /// and End jump to the ends, and focus follows the selection.</summary>
        private void OnRailKeyDown(object sender, KeyEventArgs e)
        {
            var count = rail.ItemCount;
            var index = rail.SelectedIndex;
            var next = e.Key switch
            {
                Key.Up => (index + count - 1) % count,
                Key.Down => (index + 1) % count,
                Key.Home => 0,
                Key.End => count - 1,
                _ => -1,
            };

            if (next < 0)
                return;

            rail.SelectedIndex = next;
            (rail.ContainerFromIndex(next) as InputElement)?.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }

        /// <summary>Escape goes to the active tab first (ending an eyedropper drag puts the color back
        /// rather than dismissing the dialog under it); only if the tab has nothing to end does it
        /// reach the Cancel / Close button.</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (btnEyedropper.IsSampling)
            {
                btnEyedropper.CancelSampling();
                e.Handled = true;
            }
            else if (ActiveTabControl.HandleEscape())
            {
                e.Handled = true;
            }
        }

        private void OnPreviewPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(true) == null)
            {
                // reset focus if clicking on anything other than a text field, so it commits
                focusSink.Focus();
            }
        }
    }
}
