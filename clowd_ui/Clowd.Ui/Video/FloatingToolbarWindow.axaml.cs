using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>
    /// The floating recording toolbar. Behavior ports the WPF FloatingButtonWindow +
    /// VideoCaptureWindow button strip (drag handle rotates on click / moves on drag with a 5px
    /// threshold, manual interaction disables auto-placement, below → right → left → inside
    /// placement cascade, live audio level bars on MIC/SPK); visuals match the wgpu capture
    /// overlay's button panel (clowd_capture_wgpu ui/gpu/panel.rs — Cascadia Code labels,
    /// canvas-fitted icons, transparent gaps; see CaptureToolButton.axaml for the mapping).
    /// Deliberately decoupled from VideoCapturePage — it only raises events and persists
    /// the mic/speaker toggles; the page wires the events and drives state via the Set* methods.
    /// On Windows WS_EX_NOACTIVATE keeps button clicks from stealing focus from the recorded app;
    /// on macOS a plain Avalonia window still activates the app on click (deferred, risk §6.11).
    /// </summary>
    public partial class FloatingToolbarWindow : Window
    {
        public event EventHandler StartClicked;
        public event EventHandler FinishClicked;
        public event EventHandler CancelClicked;
        public event EventHandler SettingsClicked;
        public event EventHandler<bool> MicToggled;
        public event EventHandler<bool> SpeakerToggled;

        private readonly DispatcherTimer _saveDebounce;
        private readonly SettingsRecording _settings;

        // start-button glyphs: play (declared in XAML) and the reload glyph shown in the RESTART
        // state. Sizes differ because the two icons have different canvas ratios (see
        // CaptureToolButton.axaml — IconSize fits the glyph inside the fixed 26px slot).
        private readonly Geometry _iconPlay;
        private readonly Geometry _iconReload;
        private const double PlayIconSize = 16;
        private const double ReloadIconSize = 20;
        private ScreenRect _region;
        private bool _micEnabled;
        private bool _spkEnabled;
        private bool _hasStatusText;

        // drag handle state machine (WPF FloatingButtonWindow.SetupDragHandle)
        private bool _manuallyPositioned;
        private bool _mouseDown;
        private bool _dragging;
        private PixelPoint _mouseDownPt;
        private PixelPoint _initialPos;

        // audio level meters (WPF VideoCaptureWindow GetLevelVisual visuals, fed by the page
        // from obs-express's 100ms levels feed via SetAudioLevels)
        private Control _micBar, _spkBar;
        private Rectangle _micFill, _spkFill;

        public FloatingToolbarWindow()
        {
            InitializeComponent();

            // gray panel backdrop (#373737) comes from RootBorder's XAML background;
            // buttons are transparent (accent when Primary) so the panel shows through.

            // never steal focus from the app being recorded; no taskbar/alt-tab entry
            WindowNativeExtensions.AddExStyles(this, WindowNativeExtensions.WS_EX_NOACTIVATE | WindowNativeExtensions.WS_EX_TOOLWINDOW);

            // tunnel: Button's class handler marks bubbling pointer events handled, so plain
            // XAML subscriptions on the drag button would never fire.
            BtnDrag.AddHandler(PointerPressedEvent, DragHandlePressed, RoutingStrategies.Tunnel);
            BtnDrag.AddHandler(PointerMovedEvent, DragHandleMoved, RoutingStrategies.Tunnel);
            BtnDrag.AddHandler(PointerReleasedEvent, DragHandleReleased, RoutingStrategies.Tunnel);
            BtnDrag.PointerCaptureLost += DragHandleCaptureLost;

            UpdateToolTipPlacement();

            // nothing in the settings graph saves itself, and the Recording page's auto-save only
            // attaches when that page is opened — the toolbar persists its own toggles (debounced).
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                SaveSettings();
            };

            _iconPlay = BtnStart.IconPath;
            if (Application.Current?.TryGetResource("IconUndo", Application.Current.ActualThemeVariant, out var reload) == true)
                _iconReload = reload as Geometry;

            _settings = SettingsRoot.Current.Recording;
            _micEnabled = _settings.CaptureMicrophone;
            _spkEnabled = _settings.CaptureSpeaker;
            BtnMic.ShowAlternateIcon = _micEnabled;
            BtnSpeaker.ShowAlternateIcon = _spkEnabled;

            (_micBar, _micFill) = BuildMeterBar();
            (_spkBar, _spkFill) = BuildMeterBar();
            BtnMic.Overlay = _micBar;
            BtnSpeaker.Overlay = _spkBar;
            UpdateMeterVisibility();

            // the OPTIONS button opens the same settings this toolbar writes: without this, the
            // cached toggles above go stale and the next MIC/SPK click would flip the *old* value
            // back over the user's choice. Subscribed after the meter bars exist — the handler
            // refreshes them.
            _settings.PropertyChanged += OnSettingsChanged;

            ScalingChanged += (s, e) => Dispatcher.UIThread.Post(PositionNearRegion, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Sets the start button label ("WAIT…" → "START", or "RESTART" once a settings change has
        /// invalidated the pending capturer). <paramref name="restart"/> also swaps the play glyph
        /// for the reload one — WPF parity with the IconUndo/MustReload binding.
        /// </summary>
        public void SetPrimaryText(string text, bool restart = false)
        {
            BtnStart.Text = text;

            // keep the play glyph if the reload resource could not be resolved — a missing icon
            // must not leave the button blank.
            var showReload = restart && _iconReload != null;
            BtnStart.IconPath = showReload ? _iconReload : _iconPlay;
            BtnStart.IconSize = showReload ? ReloadIconSize : PlayIconSize;
        }

        /// <summary>Swaps START for FINISH while recording is rolling.</summary>
        public void SetRecordingState(bool recording)
        {
            BtnStart.IsVisible = !recording;
            BtnFinish.IsVisible = recording;

            if (!recording)
                _hasStatusText = false;
        }

        /// <summary>Sets the drag handle's status text (timer / FPS); null or empty restores
        /// "DRAG ME" (which also remains until the first status arrives — WPF parity).</summary>
        public void SetStatusText(string text)
        {
            _hasStatusText = !String.IsNullOrEmpty(text);
            BtnDrag.Text = _hasStatusText ? text : "DRAG ME";
        }

        /// <summary>
        /// Shows the toolbar placed via the original WPF cascade (centered below the region →
        /// vertical right → vertical left → horizontally inside near its bottom), clamped to the
        /// monitor bounds. The region is in physical px on Windows / CG points on macOS — the
        /// same space Avalonia PixelPoint positioning uses.
        /// </summary>
        public void ShowNear(ScreenRect region)
        {
            _region = region;
            _manuallyPositioned = false;

            if (!IsVisible)
            {
                ParkOnRegionScreen(region);
                Show();

                // Show() has already run the initial layout pass, so MainPanel.Bounds is real by
                // now — place the strip before the first frame can be presented at the parking pixel.
                PositionNearRegion();
            }

            // position after the size-to-content layout pass so Bounds is real
            Dispatcher.UIThread.Post(PositionNearRegion, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Pre-show parking spot, and the only thing that makes SizeToContent produce a correctly
        /// scaled window on a non-100% monitor. Window.ShowCore sizes the platform window during
        /// Show() using the scaling of Screens.ScreenFromPoint(Position) (WindowStartupLocation is
        /// Manual here), and the Win32 impl seeds its DPI from the monitor nearest the window rect.
        /// Parking off the virtual desktop resolved both lookups to no screen / the top-left
        /// monitor, so on a scaled target monitor the toolbar was created at the wrong scale and the
        /// buttons were clipped until a monitor change forced a WM_DPICHANGED to rescale it.
        /// The target monitor's last pixel resolves both lookups to the right monitor while keeping
        /// all but one pixel of the strip off-screen for the frame before PositionNearRegion runs.
        /// </summary>
        private void ParkOnRegionScreen(ScreenRect region)
        {
            var screen = Screens.ScreenFromPoint(new PixelPoint(region.Center.X, region.Center.Y)) ?? Screens.Primary;
            if (screen == null)
                return;

            Position = new PixelPoint(screen.Bounds.Right - 1, screen.Bounds.Bottom - 1);
        }

        /// <summary>Mirrors settings edited elsewhere (the recording settings page) back into the
        /// toolbar: the capture toggles drive the MIC/SPK glyphs and the level-bar visibility.</summary>
        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null
                or "" or nameof(SettingsRecording.CaptureMicrophone) or nameof(SettingsRecording.CaptureSpeaker)))
                return;

            _micEnabled = _settings.CaptureMicrophone;
            _spkEnabled = _settings.CaptureSpeaker;
            BtnMic.ShowAlternateIcon = _micEnabled;
            BtnSpeaker.ShowAlternateIcon = _spkEnabled;
            UpdateMeterVisibility();
        }

        protected override void OnClosed(EventArgs e)
        {
            _settings.PropertyChanged -= OnSettingsChanged;

            // flush a pending debounced save so a quick toggle-then-finish isn't lost
            if (_saveDebounce.IsEnabled)
            {
                _saveDebounce.Stop();
                SaveSettings();
            }

            base.OnClosed(e);
        }

        /// <summary>Port of FloatingButtonWindow_LayoutUpdated: all math in physical px on the
        /// monitor containing the region's center, clamped to its FULL bounds (not the working
        /// area — original behavior). Skipped once the user has dragged or rotated the strip.
        /// The short/long-edge formulation is orientation-independent, so a single pass both
        /// picks the orientation and computes the final position.</summary>
        private void PositionNearRegion()
        {
            if (_region == null || !IsVisible || _manuallyPositioned)
                return;

            // logical → capture space: physical px on Windows; on macOS the region and Position
            // are CG points == logical units, so no scaling applies even on Retina.
            var scaling = OperatingSystem.IsMacOS() ? 1.0 : RenderScaling;
            var panelWidth = (int)Math.Ceiling(MainPanel.Bounds.Width * scaling);
            var panelHeight = (int)Math.Ceiling(MainPanel.Bounds.Height * scaling);
            if (panelWidth <= 0 || panelHeight <= 0)
                return;

            var screen = Screens.ScreenFromPoint(new PixelPoint(_region.Center.X, _region.Center.Y)) ?? Screens.Primary;
            if (screen == null)
                return;

            var b = screen.Bounds;
            var screenBounds = new ScreenRect(b.X, b.Y, b.Width, b.Height);

            var selection = _region.Intersect(screenBounds);
            if (selection.IsEmpty())
                selection = _region;

            var minDistance = (int)Math.Ceiling(2 * scaling);
            var maxDistance = (int)Math.Ceiling(15 * scaling);

            var bottomSpace = Math.Max(screenBounds.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(screenBounds.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - screenBounds.Left, 0) - minDistance;

            var shortEdge = Math.Min(panelWidth, panelHeight);
            var longEdge = Math.Max(panelWidth, panelHeight);

            int indLeft, indTop;

            if (bottomSpace >= shortEdge)
            {
                MainPanel.Orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - longEdge / 2;
                indTop = Math.Min(screenBounds.Bottom, selection.Bottom + maxDistance + shortEdge) - shortEdge;
            }
            else if (rightSpace >= shortEdge)
            {
                MainPanel.Orientation = Orientation.Vertical;
                indLeft = Math.Min(screenBounds.Right, selection.Right + maxDistance + shortEdge) - shortEdge;
                indTop = selection.Bottom - longEdge;
            }
            else if (leftSpace >= shortEdge)
            {
                MainPanel.Orientation = Orientation.Vertical;
                indLeft = Math.Max(selection.Left - maxDistance - shortEdge, 0);
                indTop = selection.Bottom - longEdge;
            }
            else // inside capture rect
            {
                MainPanel.Orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - longEdge / 2;
                indTop = selection.Bottom - shortEdge - maxDistance * 2;
            }

            var horizontalSize = MainPanel.Orientation == Orientation.Horizontal ? longEdge : shortEdge;

            if (indLeft < screenBounds.Left)
                indLeft = screenBounds.Left;
            else if (indLeft + horizontalSize > screenBounds.Right)
                indLeft = screenBounds.Right - horizontalSize;

            Position = new PixelPoint(indLeft, indTop);
        }

        /// <summary>
        /// Anchors the button tooltips to the strip instead of to the pointer, along whichever
        /// edge the current orientation leaves free.
        /// </summary>
        /// <remarks>
        /// Avalonia's default is <see cref="PlacementMode.Pointer"/> with a 20px vertical offset,
        /// which parks the tip's own top-level window just below the cursor. Nudging the mouse
        /// down before clicking — exactly what you do to grab the drag handle — then puts that
        /// popup under the pointer, so the press lands on the popup rather than on the button.
        /// ToolTipService closes the tip from its raw-input hook on the same button-down, which
        /// tears the PopupRoot down mid-dispatch (Avalonia logs "PlatformImpl is null, couldn't
        /// handle input"), and the click is simply lost: the first drag does nothing and the
        /// second — with no tip open yet — works. Anchoring to the control keeps the tip clear of
        /// both the cursor and the neighbouring buttons, which is why the side follows the
        /// rotation rather than being fixed.
        /// </remarks>
        private void UpdateToolTipPlacement()
        {
            var placement = MainPanel.Orientation == Orientation.Horizontal
                ? PlacementMode.Bottom
                : PlacementMode.Right;

            foreach (var btn in MainPanel.Children.OfType<CaptureToolButton>())
            {
                ToolTip.SetPlacement(btn, placement);

                // the default offset only exists to clear the cursor; anchored to the button
                // there is nothing to clear, and a floating gap reads as a detached label.
                ToolTip.SetHorizontalOffset(btn, 0);
                ToolTip.SetVerticalOffset(btn, 0);
            }
        }

        // -- drag handle: click rotates, drag past 5px (logical) moves; both count as manual --

        private void DragHandlePressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(BtnDrag);

            // the tip is advice about how to start an interaction, so it has no business appearing
            // during one — the hover timer would otherwise pop it up under the pointer mid-drag
            ToolTip.SetIsOpen(BtnDrag, false);
            ToolTip.SetServiceEnabled(BtnDrag, false);

            _manuallyPositioned = true;
            _mouseDown = true;
            _dragging = false;
            _mouseDownPt = this.PointToScreen(e.GetPosition(this));
            _initialPos = Position;
            e.Handled = true;
        }

        private void DragHandleMoved(object sender, PointerEventArgs e)
        {
            if (!_mouseDown)
                return;

            var pos = this.PointToScreen(e.GetPosition(this));
            var deltaX = pos.X - _mouseDownPt.X;
            var deltaY = pos.Y - _mouseDownPt.Y;

            var dragDelta = 5 * (OperatingSystem.IsMacOS() ? 1.0 : RenderScaling);
            if (Math.Abs(deltaX) > dragDelta || Math.Abs(deltaY) > dragDelta)
                _dragging = true;

            if (_dragging)
                Position = new PixelPoint(_initialPos.X + deltaX, _initialPos.Y + deltaY);

            e.Handled = true;
        }

        private void DragHandleReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!_mouseDown)
                return;

            // end the drag before releasing capture: Capture(null) raises PointerCaptureLost
            // synchronously, and the handler for it would otherwise clear _dragging out from
            // under the rotate check below, turning every drag into a rotation as well.
            var wasDragging = _dragging;
            EndDrag();
            e.Pointer.Capture(null);

            if (!wasDragging)
            {
                // click without drag: rotate horizontal ⇄ vertical in place (top-left anchored;
                // SizeToContent re-lays the strip out along the new axis)
                MainPanel.Orientation = MainPanel.Orientation == Orientation.Horizontal
                    ? Orientation.Vertical
                    : Orientation.Horizontal;
                UpdateToolTipPlacement();
            }

            e.Handled = true;
        }

        /// <summary>
        /// Losing capture without a release (the window hidden mid-drag, another window taking
        /// the pointer) never runs <see cref="DragHandleReleased"/>: without this the strip stays
        /// glued to the pointer for the rest of the recording — <see cref="DragHandleMoved"/>
        /// only tests <c>_mouseDown</c>, not whether the button is still held — and the tooltip
        /// stays switched off.
        /// </summary>
        private void DragHandleCaptureLost(object sender, PointerCaptureLostEventArgs e)
        {
            if (_mouseDown)
                EndDrag();
        }

        private void EndDrag()
        {
            _mouseDown = false;
            _dragging = false;
            ToolTip.SetServiceEnabled(BtnDrag, true);
        }

        // -- buttons --

        private void StartButtonClicked(object sender, RoutedEventArgs e)
        {
            StartClicked?.Invoke(this, EventArgs.Empty);
        }

        private void FinishButtonClicked(object sender, RoutedEventArgs e)
        {
            FinishClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SettingsButtonClicked(object sender, RoutedEventArgs e)
        {
            SettingsClicked?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButtonClicked(object sender, RoutedEventArgs e)
        {
            CancelClicked?.Invoke(this, EventArgs.Empty);
        }

        private void MicClicked(object sender, RoutedEventArgs e)
        {
            _micEnabled = !_micEnabled;
            BtnMic.ShowAlternateIcon = _micEnabled;
            _settings.CaptureMicrophone = _micEnabled;
            QueueSettingsSave();
            UpdateMeterVisibility();
            MicToggled?.Invoke(this, _micEnabled);
        }

        private void SpeakerClicked(object sender, RoutedEventArgs e)
        {
            _spkEnabled = !_spkEnabled;
            BtnSpeaker.ShowAlternateIcon = _spkEnabled;
            _settings.CaptureSpeaker = _spkEnabled;
            QueueSettingsSave();
            UpdateMeterVisibility();
            SpeakerToggled?.Invoke(this, _spkEnabled);
        }

        // -- audio level meters (visible only while that source is enabled, WPF parity) --

        /// <summary>The WPF AudioLevelProgressBarStyle: a 2px vertical bar on the button's left
        /// edge — white 0.6-opacity track, accent fill growing bottom-up.</summary>
        private static (Control bar, Rectangle fill) BuildMeterBar()
        {
            var track = new Rectangle { Fill = Brushes.White, Opacity = 0.6 };
            // driven by ScaleY, not Height: an explicit Height feeds the button's measure
            // pass (the overlay presenter spans the contentGrid rows) and inflates the Auto
            // label row when the meter peaks, jiggling the button content
            var fill = new Rectangle
            {
                Fill = AppStyles.AccentBackgroundBrush,
                RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(1, 0),
            };

            var bar = new Grid
            {
                Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(3, 6),
                IsHitTestVisible = false,
                IsVisible = false,
            };
            bar.Children.Add(track);
            bar.Children.Add(fill);
            return (bar, fill);
        }

        /// <summary>Drives the MIC/SPK bar fills from obs-express's 100 ms levels feed (the page
        /// forwards <see cref="ObsLevels"/>). Peak dBFS; null means that source does not exist
        /// or the capturer was torn down — the fill empties rather than freezing.</summary>
        public void SetAudioLevels(double? micDb, double? spkDb)
        {
            SetMeterFill(_micFill, micDb);
            SetMeterFill(_spkFill, spkDb);
        }

        private static void SetMeterFill(Rectangle fill, double? db)
        {
            // dBFS → percent, same -60 dB floor mapping the old WASAPI/CoreAudio listeners used
            var percent = db == null ? 0d : Math.Clamp(db.Value / 60d * 100d + 100d, 0d, 100d);
            ((ScaleTransform)fill.RenderTransform!).ScaleY = percent / 100d;
        }

        private void UpdateMeterVisibility()
        {
            _micBar.IsVisible = _micEnabled;
            _spkBar.IsVisible = _spkEnabled;
            if (!_micEnabled)
                SetMeterFill(_micFill, null);
            if (!_spkEnabled)
                SetMeterFill(_spkFill, null);
        }

        private void QueueSettingsSave()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void SaveSettings()
        {
            try
            {
                SettingsService.Save(SettingsRoot.Current);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to save recording toggle settings: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "video.save-toggle-settings");
            }
        }
    }
}
