using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
    /// Which session the strip is being built for. This is a CONSTRUCTION choice, not a mode the
    /// strip can be switched into later: the recording profile owns settings the share profile
    /// must never touch (it seeds itself from <see cref="SettingsRecording"/>, audits the capture
    /// toggles against the attached devices and writes the result back), so the two halves are
    /// built by different code paths rather than by hiding buttons after the fact.
    /// </summary>
    public enum FloatingToolbarProfile
    {
        /// <summary>The full recording strip: DRAG, START/PAUSE, MIC, SPK, CAM, OPTIONS,
        /// CANCEL/FINISH, plus the three device-picker pills and the audio level meters.</summary>
        Recording,

        /// <summary>The share-region strip: DRAG, BLUR, CANCEL and nothing else. No settings are
        /// read or written, no devices are enumerated and no pills are shown.</summary>
        ShareRegion,
    }

    /// <summary>
    /// The floating recording toolbar. Behavior ports the WPF FloatingButtonWindow +
    /// VideoCaptureWindow button strip (drag handle rotates on click / moves on drag with a 5px
    /// threshold, manual interaction disables auto-placement, below → right → left → inside
    /// placement cascade, live audio level bars on MIC/SPK); visuals match the capture
    /// overlay's button panel (clowd_capture ui/gpu/panel.rs — Cascadia Code labels,
    /// canvas-fitted icons, transparent gaps; see CaptureToolButton.axaml for the mapping).
    /// Deliberately decoupled from VideoCapturePage — it only raises events and persists
    /// the mic/speaker toggles (and the device ids its own pickers write); the page wires the
    /// events and drives state via the Set* methods.
    /// On Windows WS_EX_NOACTIVATE keeps button clicks from stealing focus from the recorded app;
    /// on macOS a plain Avalonia window still activates the app on click (deferred, risk §6.11).
    /// The same chassis also serves a share-region session
    /// (<see cref="FloatingToolbarProfile.ShareRegion"/>), which keeps the drag handle, the status
    /// label and the placement cascade but replaces the recording controls with a single BLUR
    /// toggle — see the constructor.
    /// </summary>
    public partial class FloatingToolbarWindow : Window
    {
        public event EventHandler StartClicked;
        public event EventHandler PauseToggleClicked;
        public event EventHandler FinishClicked;
        public event EventHandler CancelClicked;
        public event EventHandler SettingsClicked;
        public event EventHandler<bool> MicToggled;
        public event EventHandler<bool> SpeakerToggled;

        /// <summary>Raised when the CAM button flips <see cref="SettingsRecording.CaptureWebcam"/>
        /// (already written by the time this fires, like the mic/speaker toggles). Never raised
        /// while recording — the button is disabled then — and never raised for a click that only
        /// opened the settings page because no camera has been picked yet.</summary>
        public event EventHandler<bool> WebcamToggled;

        /// <summary>Raised by the BLUR tile with the state it just flipped to (share profile only).
        /// The tile has already been repainted when this fires — the helper's ack is confirmation,
        /// not permission, and a mirrored region that keeps showing through for a round trip while
        /// the button waits to be told it may light up reads as a dead button.</summary>
        public event EventHandler<bool> BlurToggled;

        /// <summary>Which strip was built. Fixed at construction — see
        /// <see cref="FloatingToolbarProfile"/>.</summary>
        private readonly FloatingToolbarProfile _profile;

        private readonly DispatcherTimer _saveDebounce;

        /// <summary>The recording settings this strip mirrors and persists. Null on a share strip:
        /// nothing there reads or writes settings, and a null here is what makes that structural
        /// rather than a promise (see <see cref="InitializeRecordingControls"/>).</summary>
        private SettingsRecording _settings;

        /// <summary>Long edge of a device-picker pill — it runs along the strip's stacking axis,
        /// so it is the pill's width beside a horizontal row and its height beside a vertical one.</summary>
        private const double PillLong = 28;

        /// <summary>Short edge of a device-picker pill (the axis pointing away from the strip).</summary>
        private const double PillShort = 13;


        /// <summary>The lane the pills occupy outside the gray strip, reserved by
        /// <see cref="RootBorder"/>'s margin. They meet the strip edge-to-edge, so the lane is
        /// exactly one pill deep. Part of the window, so the placement math has to account for it —
        /// see <see cref="PositionNearRegion"/>.</summary>
        private const double PillLane = PillShort;

        private ScreenRect _region;
        private bool _micEnabled;
        private bool _spkEnabled;
        private bool _camEnabled;
        /// <summary>What the drag handle reads before the first status of a recording arrives.
        /// Statuses are 1 Hz, so leaving DRAG ME there parks the pre-recording label under a
        /// recording that is already rolling; this is the same mm:ss shape VideoCapturePage
        /// formats, so the first real status replaces it rather than changing the label's
        /// appearance.</summary>
        private const string ZeroStatusText = "00:00";

        private bool _hasStatusText;

        // share profile: whether the helper is currently obscuring the mirrored region, and
        // whether it can at all. _blurAvailable only ever goes false — the helper's GPU effect
        // failure is permanent for the life of that process — so the tile is retired, not
        // toggled off (SetBlurAvailable).
        private bool _blurEnabled;
        private bool _blurAvailable = true;

        // one-shot timer behind ShowStatusBlip; built lazily because only the share strip has
        // anything to say this way. While it runs the label belongs to the blip and incoming
        // statuses are recorded but not painted.
        private DispatcherTimer _statusBlip;
        private bool _blipHolding;

        private bool _recording;
        private bool _paused;
        // the recorder is still being built (or rebuilt): START cannot act yet, so it is locked
        // and un-pulsed until VideoCapturePage says otherwise.
        private bool _waiting = true;
        // which side of the strip the device pills float on: Bottom under a horizontal row,
        // Left/Right outboard of a vertical one (whichever side the placement cascade picked, so
        // the pills never point back over the region being recorded).
        private Dock _pillSide = Dock.Bottom;
        // the cameras this strip knows about. Filled asynchronously (enumeration costs a process
        // spawn) and replaced by the picker's refresh row; null until the first list lands, which
        // is why the CAM pill starts hidden rather than guessing.
        private List<CameraDeviceInfo> _cameras;
        // the last timer/FPS text, so the drag label can fall back to it when a pause ends
        private string _lastStatusText;

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

        /// <summary>
        /// Satisfies the XAML compiler's runtime-loader check (AVLN3001) and nothing else, exactly
        /// as <see cref="BorderWindow"/> does. Deliberately NOT a parameterless ctor that defaults
        /// to <see cref="FloatingToolbarProfile.Recording"/>: this window's recording half mutates
        /// the user's settings on construction (it unticks capture toggles whose devices are gone
        /// and queues a save) and spawns a camera enumeration, so a caller that merely forgot to
        /// say which strip it wanted would silently get all of that. Make them say it.
        /// </summary>
        [Obsolete("Runtime-loader signature only — use FloatingToolbarWindow(FloatingToolbarProfile).", error: true)]
        public FloatingToolbarWindow()
        {
            throw new NotSupportedException("FloatingToolbarWindow requires a profile.");
        }

        /// <summary>
        /// Builds the strip in two halves. The CHASSIS below is everything both profiles need —
        /// the window styles, the drag-handle state machine, the pill lane, the tooltip anchoring
        /// and the placement hooks — and the recording controls are a separate pass that a share
        /// session never runs. The split is not tidiness: the recording half reads and REWRITES
        /// the user's recording settings and enumerates cameras, and a share session doing either
        /// would be a toolbar for one feature quietly editing the configuration of another.
        /// </summary>
        public FloatingToolbarWindow(FloatingToolbarProfile profile)
        {
            _profile = profile;

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

            // hovering the handle is exactly when the move affordance is wanted back, so the
            // recording mark yields to the arrows for as long as the pointer is on it.
            BtnDrag.PointerEntered += (s, e) => UpdateDragIcon();
            BtnDrag.PointerExited += (s, e) => UpdateDragIcon();

            UpdatePillLane();
            UpdateToolTipPlacement();

            // the pills line up against tiles in a different container, so they are placed from
            // the arranged bounds rather than laid out — which means re-placing after every pass
            // that can move a tile (rotation, a label growing, a DPI change).
            LayoutUpdated += (s, e) => UpdatePillPositions();

            // nothing in the settings graph saves itself, and the Recording page's auto-save only
            // attaches when that page is opened — the toolbar persists its own toggles (debounced).
            // Inert on a share strip: nothing there ever starts it.
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                SaveSettings();
            };

            ScalingChanged += (s, e) => Dispatcher.UIThread.Post(PositionNearRegion, DispatcherPriority.Loaded);

            if (profile == FloatingToolbarProfile.Recording)
                InitializeRecordingControls();
            else
                InitializeShareControls();
        }

        /// <summary>
        /// The recording half: everything that touches <see cref="SettingsRecording"/>, the device
        /// managers or the level meters. What the constructor used to do inline (the settings read
        /// moved to the front, which is the one ordering change); it lives behind a profile check
        /// now because every line of it has a side effect a share session
        /// must not pay — <see cref="DisableCaptureWithoutDevice"/> rewrites the user's capture
        /// toggles and queues a save, the camera load spawns an enumeration, and the settings
        /// subscription outlives anything the share strip would unsubscribe.
        /// </summary>
        private void InitializeRecordingControls()
        {
            // first, and before anything that can await: LoadCamerasAsync's continuation calls
            // DisableCaptureWithoutDevice, which reads this.
            _settings = SettingsRoot.Current.Recording;
            DisableCaptureWithoutDevice();
            _micEnabled = _settings.CaptureMicrophone;
            _spkEnabled = _settings.CaptureSpeaker;
            _camEnabled = IsWebcamCaptured(_settings);
            BtnMic.ShowAlternateIcon = _micEnabled;
            BtnSpeaker.ShowAlternateIcon = _spkEnabled;
            BtnWebcam.ShowAlternateIcon = _camEnabled;

            // re-enumerate rather than take the process-wide cache: the strip opens long after the
            // app did, and a camera plugged in since then is exactly the one being reached for.
            // Native enumeration makes this ~60 ms, so it is affordable on every strip.
            _ = LoadCamerasAsync(CameraDeviceManager.RefreshAsync());

            UpdateDevicePills();

            // the strip opens in the WAIT state: START locked, unaccented and still.
            UpdatePrimaryState();

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
        }

        /// <summary>
        /// The share half: three tiles (DRAG, BLUR, CANCEL) and an empty pill lane.
        /// The pills are the trap here. <see cref="CaptureToolPill"/> is visible by default and the
        /// ONLY thing that ever hides one is <see cref="UpdateDevicePills"/>, which this profile
        /// never calls — so without the explicit hiding below the share strip would carry three
        /// dead chevrons plus a reserved lane that is transparent but still hit-testable, i.e. a
        /// band of window that eats clicks on whatever is behind it.
        /// <see cref="ApplyPillLane"/> then gives that lane's pixels back.
        /// </summary>
        private void InitializeShareControls()
        {
            // the recording tiles, collapsed rather than disabled: they are not "unavailable
            // right now", they are not part of this feature at all.
            BtnStart.IsVisible = false;
            BtnMic.IsVisible = false;
            BtnSpeaker.IsVisible = false;
            BtnWebcam.IsVisible = false;
            BtnSettings.IsVisible = false;

            BtnBlur.IsVisible = true;

            BtnMicDevice.IsVisible = false;
            BtnSpeakerDevice.IsVisible = false;
            BtnWebcamDevice.IsVisible = false;
            ApplyPillLane();
        }

        /// <summary>Whether this strip was built for a recording. The five Set* methods below all
        /// drive controls the share profile collapsed, so they answer this first and no-op rather
        /// than writing state into a hidden tile that could then leak out through a shared
        /// helper — <see cref="UpdateDragIcon"/> and <see cref="SetStatusText"/> both branch on
        /// <c>_recording</c>, and a share strip that had been told it was recording would wear the
        /// Clowd mark and a zeroed timer.</summary>
        private bool IsRecordingProfile => _profile == FloatingToolbarProfile.Recording;

        /// <summary>Sets the start button label ("WAIT…" → "START"). Settings changed during WAIT
        /// are pushed into the running recorder, so the button never becomes a reload. Ignored
        /// while recording — the button belongs to PAUSE/RESUME then — and on a share strip, which
        /// has no primary button.</summary>
        public void SetPrimaryText(string text)
        {
            if (!IsRecordingProfile)
                return;

            if (!_recording)
                BtnStart.Text = text;
        }

        /// <summary>Whether the recorder is still being built (the WAIT phase, and every respawn
        /// after it). START is dead during it — <see cref="VideoCapturePage.StartRecording"/> gates
        /// on the same flag — so the button says so instead of pulsing an invitation it cannot
        /// honor.</summary>
        public void SetWaiting(bool waiting)
        {
            if (!IsRecordingProfile)
                return;

            _waiting = waiting;
            UpdatePrimaryState();
        }

        /// <summary>
        /// The primary button can only act once the recorder is up (START) or once frames are
        /// flowing (PAUSE/RESUME) — and it only looks primary when it can. The accent fill and the
        /// button being pressable are the same condition on purpose: a WAIT tile that reads as the
        /// one thing to press, and then ignores the press, is worse than one that waits visibly.
        /// The pulse is narrower still — it belongs to START alone, as an invitation, so it is
        /// absent both while locked and on PAUSE.
        /// </summary>
        private void UpdatePrimaryState()
        {
            var live = _recording || !_waiting;

            BtnStart.IsEnabled = live;
            BtnStart.Primary = live;
            BtnStart.PulseBackground = live && !_recording;
        }

        /// <summary>The drag handle wears the Clowd mark for the duration of a recording — the
        /// strip is a status indicator far more of the time than it is something you reposition —
        /// and the move arrows while the pointer is on it, which is the moment the affordance
        /// matters. The mark's canvas has less padding than the tool glyphs, so it is drawn a
        /// little smaller to carry the same weight in the shared 26px icon slot.</summary>
        private void UpdateDragIcon()
        {
            var mark = _recording && !BtnDrag.IsPointerOver;
            BtnDrag.IconPath = (Geometry)this.FindResource(mark ? "IconClowd" : "IconToolNone");
            BtnDrag.IconSize = mark ? 22 : 26;
        }

        /// <summary>Modes the strip for a rolling recording: the primary button becomes
        /// PAUSE (its pre-start pulse stops), the trailing CANCEL button becomes FINISH —
        /// a rolling recording can be stopped and saved, but no longer discarded from here —
        /// the drag handle becomes the Clowd mark, and the CAM toggle and the three device
        /// pickers lock (see <see cref="UpdateRecordingLocks"/>).</summary>
        public void SetRecordingState(bool recording)
        {
            if (!IsRecordingProfile)
                return;

            _recording = recording;
            UpdateRecordingLocks();
            UpdatePrimaryState();
            UpdateDragIcon();

            if (recording)
            {
                BtnStart.Text = "PAUSE";
                BtnStart.IconPath = (Geometry)this.FindResource("IconPause");

                BtnCancel.Text = "FINISH";
                BtnCancel.IconPath = (Geometry)this.FindResource("IconStop");
                BtnCancel.IconSize = 15.2;

                // the icon flips to the Clowd mark on this same call; without this the label
                // lags a second behind it on DRAG ME.
                if (!_hasStatusText)
                    BtnDrag.Text = ZeroStatusText;
            }
            else
            {
                _hasStatusText = false;
                _paused = false;
                _lastStatusText = null;

                BtnStart.Text = "START";
                BtnStart.IconPath = (Geometry)this.FindResource("IconPlay");

                BtnCancel.Text = "CANCEL";
                BtnCancel.IconPath = (Geometry)this.FindResource("IconClose");
                BtnCancel.IconSize = 18;

                BtnDrag.Text = "DRAG ME";
            }
        }

        /// <summary>Flips the primary button between PAUSE and RESUME and pins the drag label to
        /// PAUSED for the duration (statuses stop while paused, so nothing else would say so).
        /// Only meaningful while recording.</summary>
        public void SetPausedState(bool paused)
        {
            if (!IsRecordingProfile || !_recording)
                return;

            _paused = paused;

            BtnStart.Text = paused ? "RESUME" : "PAUSE";
            BtnStart.IconPath = (Geometry)this.FindResource(paused ? "IconPlay" : "IconPause");

            // on resume the next status message (≤1 s away) takes over again; until then show the
            // last timer text rather than a stale PAUSED.
            BtnDrag.Text = paused ? "PAUSED" : (_lastStatusText ?? ZeroStatusText);
        }

        /// <summary>Sets the drag handle's status text (timer / FPS); null or empty falls back to
        /// a zeroed timer while recording and to "DRAG ME" before it. While paused the label stays
        /// PAUSED and the text is only remembered for the resume.
        /// Works on both profiles — the share strip uses it for the helper's fps, and its empty
        /// fallback is already the right one: <c>_recording</c> can never be set there
        /// (<see cref="SetRecordingState"/> no-ops), so clearing the text restores "DRAG ME"
        /// rather than a zeroed recording timer.</summary>
        public void SetStatusText(string text)
        {
            _hasStatusText = !String.IsNullOrEmpty(text);
            _lastStatusText = _hasStatusText ? text : null;

            // a blip is a one-off message on the same label the session writes once a second; it
            // still records the status underneath it, so the label goes back to a live value
            // rather than to DRAG ME when the blip expires.
            if (!_paused && !_blipHolding)
                BtnDrag.Text = _hasStatusText ? text : (_recording ? ZeroStatusText : "DRAG ME");
        }

        // -- BLUR (share profile) --

        /// <summary>
        /// Pushes the obscure state onto the tile without raising <see cref="BlurToggled"/> — the
        /// authoritative direction, for the helper's acks. Two of those matter: the ack for a
        /// toggle the user just made (a no-op repaint, and cheap insurance that the strip agrees
        /// with the process actually drawing the frames), and the UNSOLICITED
        /// <c>obscure/none</c> the helper emits when its GPU effect fails to build — a retraction
        /// of a blur the user asked for and can currently see is on. Pair that one with
        /// <see cref="SetBlurAvailable"/>(false): the failure is permanent for that process.
        /// </summary>
        public void SetBlurEnabled(bool on)
        {
            _blurEnabled = on;
            BtnBlur.ShowAlternateIcon = on;
        }

        /// <summary>
        /// Retires (or restores) the BLUR tile. Called with false when the helper says its effect
        /// pipeline is gone, which it never rebuilds — so this is a permanent retirement rather
        /// than a temporary lock, and the tile stays visible-but-dead on purpose: a button that
        /// vanishes mid-session reads as a bug, and the strip re-laying itself out around the gap
        /// would move CANCEL under the pointer.
        /// The only notice is a blip on the status label. Never a dialog: this can land in the
        /// middle of a live meeting the user is presenting to, where a modal is both a
        /// screen-sharing embarrassment and a thing they cannot dismiss without losing their place.
        /// </summary>
        public void SetBlurAvailable(bool available)
        {
            _blurAvailable = available;
            BtnBlur.IsEnabled = available;

            if (available)
                return;

            // whatever the user last asked for, nothing is being obscured now — say so, so the
            // dead tile is not left lit over a region that is being mirrored in the clear.
            SetBlurEnabled(false);
            ShowStatusBlip("NO BLUR");
        }

        private void BlurClicked(object sender, RoutedEventArgs e)
        {
            // IsEnabled already blocks this once the tile is retired; the check is here because
            // the consequence of getting it wrong is a blur command to a helper that has told us
            // it cannot honor one, and the strip would then be lit over a clear region.
            if (!_blurAvailable)
                return;

            SetBlurEnabled(!_blurEnabled);
            BlurToggled?.Invoke(this, _blurEnabled);
        }

        /// <summary>
        /// Says something on the drag handle's label for a few seconds and then hands the label
        /// back. The strip has no other surface for a message — it is seven 50px tiles — and the
        /// alternative for a share session (a dialog) is exactly what must not happen while a
        /// region is being mirrored into a meeting. Statuses arriving meanwhile are still recorded
        /// (see <see cref="SetStatusText"/>), just not painted, so the blip is not overwritten a
        /// few hundred milliseconds after it appears.
        /// </summary>
        private void ShowStatusBlip(string text)
        {
            if (_statusBlip == null)
            {
                _statusBlip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _statusBlip.Tick += (s, e) =>
                {
                    _statusBlip.Stop();
                    _blipHolding = false;
                    SetStatusText(_lastStatusText);
                };
            }

            _blipHolding = true;
            BtnDrag.Text = text;

            // restart rather than extend: a second message replaces the first outright.
            _statusBlip.Stop();
            _statusBlip.Start();
        }

        /// <summary>
        /// Shows the toolbar placed via the original WPF cascade (centered below the region →
        /// vertical right → vertical left → horizontally inside near its bottom), clamped to the
        /// monitor bounds. A share strip takes "centered above" ahead of the two vertical rungs,
        /// because everything inside its region is being mirrored (see
        /// <see cref="PositionNearRegion"/>). The region is in physical px on Windows / CG points
        /// on macOS — the same space Avalonia PixelPoint positioning uses.
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

        /// <summary>Mirrors settings edited elsewhere (the recording settings page, or the page
        /// reverting a webcam the recorder refused) back into the toolbar: the capture toggles
        /// drive the MIC/SPK/CAM glyphs and the level-bar visibility.</summary>
        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (null
                or "" or nameof(SettingsRecording.CaptureMicrophone) or nameof(SettingsRecording.CaptureSpeaker)
                or nameof(SettingsRecording.CaptureWebcam) or nameof(SettingsRecording.WebcamDeviceId)
                // gates the webcam entirely: switching composition off unlights CAM.
                or nameof(SettingsRecording.EnableComposition)))
                return;

            _micEnabled = _settings.CaptureMicrophone;
            _spkEnabled = _settings.CaptureSpeaker;
            // the CAM glyph tracks whether a webcam will actually be recorded, which needs a
            // device as well as the tick — picking one on the settings page is what lights the
            // button up after a click that could only send the user there.
            _camEnabled = IsWebcamCaptured(_settings);
            BtnMic.ShowAlternateIcon = _micEnabled;
            BtnSpeaker.ShowAlternateIcon = _spkEnabled;
            BtnWebcam.ShowAlternateIcon = _camEnabled;
            UpdateMeterVisibility();
        }

        /// <summary>A webcam is only captured when the box is ticked, a camera has been chosen and
        /// composition is on — <see cref="ObsArguments.WriteSettingsFile"/> writes an empty device
        /// id (i.e. no webcam source at all) when any of those is missing, so the button says the
        /// same rather than lighting up for a camera that will not be recorded.</summary>
        private static bool IsWebcamCaptured(SettingsRecording settings)
            => ObsArguments.UsesWebcam(settings);

        protected override void OnClosed(EventArgs e)
        {
            // null on a share strip, which never subscribed (and never has a save pending below).
            if (_settings != null)
                _settings.PropertyChanged -= OnSettingsChanged;

            _statusBlip?.Stop();

            // flush a pending debounced save so a quick toggle-then-finish isn't lost
            if (_saveDebounce.IsEnabled)
            {
                _saveDebounce.Stop();
                SaveSettings();
            }

            base.OnClosed(e);
        }

        /// <summary>Port of FloatingButtonWindow_LayoutUpdated: all math in physical px on the
        /// monitor containing the region's center. Skipped once the user has dragged or rotated
        /// the strip. The short/long-edge formulation is orientation-independent, so a single
        /// pass both picks the orientation and computes the final position.
        /// Unlike the WPF original this measures and clamps against the monitor's WORKING area
        /// rather than its full bounds, so the strip is never dealt a slot underneath the macOS
        /// dock / menu bar or the Windows taskbar, which are painted over it (issue #72). The
        /// selection itself still comes from the full bounds — the capture region legitimately
        /// covers the reserved strips, and clipping it would shift the centering.
        /// The share profile inserts an "above" rung between below and right, and only then falls
        /// back to sitting inside the region — see the cascade itself for why a mirrored region
        /// changes the stakes of that last rung.</summary>
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

            // the placeable area: the monitor minus whatever the shell reserves (dock, menu bar,
            // taskbar). Avalonia reports this in the same space as Bounds, so no conversion.
            var w = screen.WorkingArea;
            var workArea = w.Width > 0 && w.Height > 0
                ? new ScreenRect(w.X, w.Y, w.Width, w.Height).Intersect(screenBounds)
                : screenBounds;
            if (workArea.IsEmpty())
                workArea = screenBounds;

            var selection = _region.Intersect(screenBounds);
            if (selection.IsEmpty())
                selection = _region;

            var minDistance = (int)Math.Ceiling(2 * scaling);
            var maxDistance = (int)Math.Ceiling(15 * scaling);

            var bottomSpace = Math.Max(workArea.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(workArea.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - workArea.Left, 0) - minDistance;
            var topSpace = Math.Max(selection.Top - workArea.Top, 0) - minDistance;

            var shortEdge = Math.Min(panelWidth, panelHeight);
            var longEdge = Math.Max(panelWidth, panelHeight);

            // the device pills sit in a lane outside the gray strip, always on the short-edge axis
            // — so the window is one lane deeper than the strip, and every fits-here test below has
            // to ask for the room the WINDOW needs, not the strip. Read off the margin rather than
            // the constant: the lane is only reserved while there are pills in it.
            var lane = _pillSide switch
            {
                Dock.Left => RootBorder.Margin.Left,
                Dock.Right => RootBorder.Margin.Right,
                _ => RootBorder.Margin.Bottom,
            };
            var winShort = shortEdge + (int)Math.Ceiling(lane * scaling);

            // a mirrored region is photographed continuously, so anything the cascade parks inside
            // it is not a stray frame or two at the end of a recording — it is Clowd's own toolbar
            // broadcast into someone else's meeting for as long as the session lasts. The share
            // profile therefore gets a fourth outside-the-region rung (above) before it will
            // consider the inside one; ScrollStatusWindow.TryComputePlacement takes the same
            // detour for the same reason. The recording profile's cascade is untouched: for it,
            // "inside" costs a strip in the last frames before the region is cleared, which is
            // not worth changing a placement users have learned.
            var shareRegion = _profile == FloatingToolbarProfile.ShareRegion;

            int indLeft, indTop;

            if (bottomSpace >= winShort)
            {
                // below the selection: the lane hangs further below, away from what is recorded.
                SetLayout(Orientation.Horizontal, Dock.Bottom);
                indLeft = selection.Left + selection.Width / 2 - longEdge / 2;
                indTop = Math.Min(workArea.Bottom, selection.Bottom + maxDistance + winShort) - winShort;
            }
            else if (shareRegion && topSpace >= winShort)
            {
                // above the selection, share sessions only. It is preferred over the two vertical
                // rungs because a horizontal strip is the shape the user grabbed the handle on and
                // the one whose labels read at a glance, and it costs nothing here: the pill lane
                // it would push back INTO the region is empty on a share strip (no device pickers),
                // so there is nothing on that edge to overlap the mirrored rectangle.
                SetLayout(Orientation.Horizontal, Dock.Bottom);
                indLeft = selection.Left + selection.Width / 2 - longEdge / 2;
                indTop = Math.Max(selection.Top - maxDistance - winShort, workArea.Top);
            }
            else if (rightSpace >= winShort)
            {
                // to the right of the selection: the lane goes further right, for the same reason.
                SetLayout(Orientation.Vertical, Dock.Right);
                indLeft = Math.Min(workArea.Right, selection.Right + maxDistance + winShort) - winShort;
                indTop = selection.Bottom - longEdge;
            }
            else if (leftSpace >= winShort)
            {
                SetLayout(Orientation.Vertical, Dock.Left);
                indLeft = Math.Max(selection.Left - maxDistance - winShort, workArea.Left);
                indTop = selection.Bottom - longEdge;
            }
            else // inside capture rect
            {
                // Last resort, and for a share session a genuinely last one: reaching here means
                // the region has no room on ANY of its four sides, i.e. it covers essentially the
                // whole monitor. There is then nowhere on that monitor the strip would not be
                // mirrored, so refusing to place it would not keep it out of the meeting — it
                // would only cost the user the CANCEL button that ends the meeting's view of their
                // screen. Showing it always wins; the strip is never withheld.

                SetLayout(Orientation.Horizontal, Dock.Bottom);
                indLeft = selection.Left + selection.Width / 2 - longEdge / 2;
                // keep the gap measured from the placeable bottom edge, so a full-height
                // selection lands above the dock/taskbar rather than flush against it.
                indTop = Math.Min(selection.Bottom, workArea.Bottom) - winShort - maxDistance * 2;
            }

            // window, not strip: indLeft/indTop are where the whole thing goes, lane included.
            var horizontalSize = MainPanel.Orientation == Orientation.Horizontal ? longEdge : winShort;
            var verticalSize = MainPanel.Orientation == Orientation.Horizontal ? winShort : longEdge;

            if (indLeft < workArea.Left)
                indLeft = workArea.Left;
            else if (indLeft + horizontalSize > workArea.Right)
                indLeft = workArea.Right - horizontalSize;

            // the vertical clamp the WPF original never had: the two branches that anchor to the
            // selection's bottom edge (vertical placement, and the fallback inside the capture
            // rect) can otherwise run the strip off the bottom of the placeable area — which is
            // exactly where a full-height selection puts it on a machine with a bottom dock.
            if (indTop < workArea.Top)
                indTop = workArea.Top;
            else if (indTop + verticalSize > workArea.Bottom)
                indTop = workArea.Bottom - verticalSize;

            Position = new PixelPoint(indLeft, indTop);
        }

        /// <summary>The one place the strip's axis and its pill lane are set, so everything that
        /// hangs off them follows both the automatic placement cascade and a click on the drag
        /// handle. (The tooltip side used to follow only the latter, leaving auto-rotated strips
        /// with their tips on the wrong edge.)</summary>
        private void SetLayout(Orientation orientation, Dock pillSide)
        {
            // a horizontal strip has only one sensible lane; a vertical one is told which side the
            // placement cascade left free.
            if (orientation == Orientation.Horizontal)
                pillSide = Dock.Bottom;
            else if (pillSide == Dock.Bottom)
                pillSide = Dock.Right;

            if (MainPanel.Orientation == orientation && _pillSide == pillSide)
                return;

            MainPanel.Orientation = orientation;
            _pillSide = pillSide;
            UpdatePillLane();
            UpdateToolTipPlacement();
        }

        /// <summary>
        /// Reserves the lane the pills float in and shapes them for it: the lane is a margin on
        /// whichever side of the gray strip is free, and each pill lies along the strip's stacking
        /// axis (wide and short below a horizontal row, narrow and tall beside a vertical column)
        /// with its chevron facing out of the strip.
        /// </summary>
        private void UpdatePillLane()
        {
            var horizontal = MainPanel.Orientation == Orientation.Horizontal;

            foreach (var pill in PillLayer.Children.OfType<CaptureToolPill>())
            {
                pill.Width = horizontal ? PillLong : PillShort;
                pill.Height = horizontal ? PillShort : PillLong;
                pill.Direction = _pillSide;
            }

            ApplyPillLane();
            UpdatePillPositions();
        }

        /// <summary>Reserves the lane, on the side the placement cascade left free — but only while
        /// something is actually in it. A strip whose sources each have exactly one device (or one
        /// that is already recording) shows no pills at all, and an empty lane is not merely wasted
        /// window: it is transparent, so it would read as the strip hovering above where it sits,
        /// and it would still swallow clicks meant for what is underneath.</summary>
        private void ApplyPillLane()
        {
            var lane = PillLayer.Children.OfType<CaptureToolPill>().Any(p => p.IsVisible) ? PillLane : 0;

            RootBorder.Margin = _pillSide switch
            {
                Dock.Left => new Thickness(lane, 0, 0, 0),
                Dock.Right => new Thickness(0, 0, lane, 0),
                _ => new Thickness(0, 0, 0, lane),
            };
        }

        /// <summary>Butts each pill up against the tile it belongs to, on the lane side — they
        /// share an edge, which is what the pill's two square corners are for. Runs off every
        /// layout pass, so it is deliberately arithmetic only: no measure, no allocation.</summary>
        private void UpdatePillPositions()
        {
            PlacePill(BtnMicDevice, BtnMic);
            PlacePill(BtnSpeakerDevice, BtnSpeaker);
            PlacePill(BtnWebcamDevice, BtnWebcam);
        }

        private void PlacePill(CaptureToolPill pill, Control tile)
        {
            if (!pill.IsVisible)
                return;

            // the tile lives inside RootBorder/MainPanel, the pill in the canvas over the whole
            // window: nothing lines them up but this translation.
            var origin = tile.TranslatePoint(default, PillLayer);
            if (origin == null)
                return;

            var at = origin.Value;
            var bounds = tile.Bounds;
            // the theme's Width/Height, not the arranged Bounds: this runs from LayoutUpdated, and
            // reading a size that this pass is still producing would place the pill one frame late.
            var width = MainPanel.Orientation == Orientation.Horizontal ? PillLong : PillShort;
            var height = MainPanel.Orientation == Orientation.Horizontal ? PillShort : PillLong;

            switch (_pillSide)
            {
                case Dock.Left:
                    Canvas.SetLeft(pill, at.X - width);
                    Canvas.SetTop(pill, at.Y + (bounds.Height - height) / 2);
                    break;

                case Dock.Right:
                    Canvas.SetLeft(pill, at.X + bounds.Width);
                    Canvas.SetTop(pill, at.Y + (bounds.Height - height) / 2);
                    break;

                default:
                    Canvas.SetLeft(pill, at.X + (bounds.Width - width) / 2);
                    Canvas.SetTop(pill, at.Y + bounds.Height);
                    break;
            }
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
        /// both the cursor and the neighboring buttons, which is why the side follows the
        /// rotation rather than being fixed.
        /// </remarks>
        private void UpdateToolTipPlacement()
        {
            var placement = MainPanel.Orientation == Orientation.Horizontal
                ? PlacementMode.Bottom
                : PlacementMode.Right;

            // the device pills carry tips of their own, and sit in the canvas rather than the strip.
            foreach (var btn in MainPanel.Children.OfType<Button>().Concat(PillLayer.Children.OfType<Button>()))
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
                // the lane keeps the side it was last given; only the axis flips.
                SetLayout(MainPanel.Orientation == Orientation.Horizontal
                    ? Orientation.Vertical
                    : Orientation.Horizontal, _pillSide);
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
            // the same physical button: START before recording, PAUSE/RESUME after.
            if (_recording)
                PauseToggleClicked?.Invoke(this, EventArgs.Empty);
            else
                StartClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SettingsButtonClicked(object sender, RoutedEventArgs e)
        {
            SettingsClicked?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButtonClicked(object sender, RoutedEventArgs e)
        {
            // the same physical button: CANCEL (discard) before recording, FINISH (save) after.
            if (_recording)
                FinishClicked?.Invoke(this, EventArgs.Empty);
            else
                CancelClicked?.Invoke(this, EventArgs.Empty);
        }

        private void MicClicked(object sender, RoutedEventArgs e)
        {
            // a click is the moment a device that appeared since the strip opened (AirPods
            // connecting, a camera plugged in) has to be noticed, and the audit is a cheap
            // enumeration.
            UpdateDevicePills();

            if (!_micEnabled && !HasDevice(CaptureSource.Microphone))
            {
                TurnOnWithDevice(CaptureSource.Microphone);
                return;
            }

            SetMicEnabled(!_micEnabled);
        }

        private void SpeakerClicked(object sender, RoutedEventArgs e)
        {
            UpdateDevicePills();

            if (!_spkEnabled && !HasDevice(CaptureSource.Speaker))
            {
                TurnOnWithDevice(CaptureSource.Speaker);
                return;
            }

            SetSpeakerEnabled(!_spkEnabled);
        }

        /// <summary>
        /// CAM toggle. Unlike MIC/SPK this is not a mute: the recorder builds (or drops) a whole
        /// webcam source and a second encoder for it, which it will only do while it is still
        /// waiting — hence <see cref="UpdateRecordingLocks"/> locking the button once frames flow.
        /// Turning it on with no camera chosen opens the camera picker (as MIC/SPK do); with
        /// composition off there is no second video track for a camera to live in, which no
        /// dropdown can fix, so that one click still goes to the settings page.
        /// </summary>
        private void WebcamClicked(object sender, RoutedEventArgs e)
        {
            if (_recording)
                return;

            UpdateDevicePills();

            if (!_camEnabled && !_settings.EnableComposition)
            {
                // the page's own handler owns the navigation (the toolbar never touches PageManager).
                SettingsClicked?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_camEnabled && !HasDevice(CaptureSource.Webcam))
            {
                TurnOnWithDevice(CaptureSource.Webcam);
                return;
            }

            SetWebcamEnabled(!_camEnabled);
        }

        /// <summary>
        /// Turning a source on that has nothing to record from. With exactly one device there is no
        /// choice to put to the user — adopt it and let the click through. Otherwise ask, and let
        /// the pick complete the turn-on; that also covers having no device at all, where the menu
        /// is what says so (and, for cameras, what offers to look again).
        /// </summary>
        private void TurnOnWithDevice(CaptureSource source)
        {
            var only = SingleDevice(source);
            if (only != null)
                PickDevice(source, only, enable: true);
            else
                OpenDeviceMenu(source, enableOnPick: true);
        }

        private void SetMicEnabled(bool enabled)
        {
            _micEnabled = enabled;
            BtnMic.ShowAlternateIcon = enabled;
            _settings.CaptureMicrophone = enabled;
            QueueSettingsSave();
            UpdateMeterVisibility();
            MicToggled?.Invoke(this, enabled);
        }

        private void SetSpeakerEnabled(bool enabled)
        {
            _spkEnabled = enabled;
            BtnSpeaker.ShowAlternateIcon = enabled;
            _settings.CaptureSpeaker = enabled;
            QueueSettingsSave();
            UpdateMeterVisibility();
            SpeakerToggled?.Invoke(this, enabled);
        }

        private void SetWebcamEnabled(bool enabled)
        {
            _camEnabled = enabled;
            BtnWebcam.ShowAlternateIcon = enabled;
            _settings.CaptureWebcam = enabled;
            QueueSettingsSave();
            WebcamToggled?.Invoke(this, enabled);
        }

        /// <summary>Everything the recorder fixes when frames start flowing: the webcam is a
        /// pipeline element rather than a mute (there is no live equivalent of the audio mutes), and
        /// the device ids are read when the pipeline is built, so neither can change mid-recording.
        /// CAM is simply disabled, and the device pills go with it (see
        /// <see cref="UpdateDevicePills"/>). MIC/SPK themselves stay live: those really are
        /// mutes.</summary>
        private void UpdateRecordingLocks()
        {
            // MIC/SPK stay live while recording — they are mutes — but only where the recorder
            // actually built a source to mute. With no device there is nothing to unmute, and the
            // picker that would fix that is frozen too, so the toggle locks with it rather than
            // lighting up over silence.
            BtnMic.IsEnabled = !_recording || HasDevice(CaptureSource.Microphone);
            BtnSpeaker.IsEnabled = !_recording || HasDevice(CaptureSource.Speaker);

            BtnWebcam.IsEnabled = !_recording;

            UpdateDevicePills();
        }

        // -- device pickers (the pill floating beside each toggle) --

        /// <summary>The three sources the strip toggles, each with a device behind it.</summary>
        private enum CaptureSource
        {
            Microphone,
            Speaker,
            Webcam,
        }

        private void MicDeviceClicked(object sender, RoutedEventArgs e)
            => OpenDeviceMenu(CaptureSource.Microphone, enableOnPick: false);

        private void SpeakerDeviceClicked(object sender, RoutedEventArgs e)
            => OpenDeviceMenu(CaptureSource.Speaker, enableOnPick: false);

        private void WebcamDeviceClicked(object sender, RoutedEventArgs e)
            => OpenDeviceMenu(CaptureSource.Webcam, enableOnPick: false);

        /// <summary>
        /// Whether <paramref name="source"/> has a device that can actually be recorded right now.
        /// A stored id for a device that has since been unplugged counts as none: the recorder
        /// would open nothing and the user would find that out after the recording, not before.
        /// </summary>
        private bool HasDevice(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => HasAudioDevice(_settings.MicrophoneDeviceId, AudioDeviceManager.GetMicrophones()),
            // there is no output device to pick on macOS — ScreenCaptureKit hands over the whole
            // system mix — so speaker capture is never blocked on one there.
            CaptureSource.Speaker => OperatingSystem.IsMacOS()
                || HasAudioDevice(_settings.SpeakerDeviceId, AudioDeviceManager.GetSpeakers()),
            // cameras are never enumerated here — a click has to answer immediately — so this
            // reads the list the strip already has. Until it lands a stored id is taken at face
            // value; after it does, an id no longer among them is no device (DisableCaptureWithout-
            // Device runs again at that point, which is what makes the deferred check count).
            _ => !String.IsNullOrEmpty(_settings.WebcamDeviceId)
                 && (_cameras == null || _cameras.Any(c => c.DeviceId == _settings.WebcamDeviceId)),
        };

        private static bool HasAudioDevice(string deviceId, List<AudioDeviceInfo> devices)
        {
            // "default" is a pointer at whichever device the OS currently favours, and the
            // enumerator always offers it — so on a machine with no inputs at all it would read as
            // a selection and earn a track of silence. It only counts while something backs it.
            if (!devices.Any(d => d.DeviceId != AudioDeviceManager.DefaultDeviceId))
                return false;

            return !String.IsNullOrEmpty(deviceId) && devices.Any(d => d.DeviceId == deviceId);
        }

        /// <summary>
        /// The devices the user could actually choose between — which is not what the menu lists.
        /// "default" is a pointer at one of the others, never an alternative to them: with a single
        /// microphone attached, "Default" and that microphone ARE that microphone, so a source with
        /// one real device has nothing to pick and gets no pill. (The menu still offers "default"
        /// when it opens at all — following the system default is a real preference once there is
        /// more than one device to follow.)
        /// </summary>
        private List<string> RealDeviceIds(CaptureSource source)
        {
            switch (source)
            {
                case CaptureSource.Microphone:
                case CaptureSource.Speaker:
                    var audio = source == CaptureSource.Speaker
                        ? AudioDeviceManager.GetSpeakers()
                        : AudioDeviceManager.GetMicrophones();
                    return audio.Select(d => d.DeviceId)
                                .Where(id => id != AudioDeviceManager.DefaultDeviceId)
                                .ToList();

                default:
                    // null (not enumerated yet) is not "no cameras" — it is "we cannot say", which
                    // an empty list expresses well enough for both callers: no pill, and no
                    // shortcut, so a click opens the menu that does the waiting.
                    return _cameras?.Select(c => c.DeviceId).ToList() ?? new List<string>();
            }
        }

        /// <summary>The only device <paramref name="source"/> could record from, or null when there
        /// is a choice to make (or nothing to choose).</summary>
        private string SingleDevice(CaptureSource source)
        {
            var ids = RealDeviceIds(source);
            return ids.Count == 1 ? ids[0] : null;
        }

        /// <summary>
        /// A picker only earns its pill when there is a choice to make with it — more than one
        /// device behind it, and a recording that has not started. Otherwise the tile's own click is
        /// the whole interaction and a chevron would promise something that does not exist. Audio is
        /// enumerated here and now (a local call), so this also catches a device that has appeared
        /// since the strip opened, every time it runs.
        /// </summary>
        private void UpdateDevicePills()
        {
            // belt and braces. This is the ONLY method on the strip that reaches AudioDeviceManager
            // or CameraDeviceManager (through RealDeviceIds), so this one line is what makes device
            // enumeration structurally impossible on a share strip rather than merely unreached:
            // every caller below it is a recording control the share profile collapsed, but a
            // future one would have to defeat this to enumerate. The share strip's pills are hidden
            // once, in InitializeShareControls, and stay hidden.
            if (_profile != FloatingToolbarProfile.Recording)
                return;

            // once frames are flowing the device ids are fixed: VideoCapturePage drops a settings
            // change while IsRecording, so a picker could only write a value the recording will
            // never use. That is worth removing rather than graying out — a disabled control
            // suggests some state in which it works, and there is none until this recording ends.
            var pickable = !_recording;

            BtnMicDevice.IsVisible = pickable && RealDeviceIds(CaptureSource.Microphone).Count > 1;
            // never on macOS: ScreenCaptureKit hands over the whole system mix, so there is no
            // output device to choose (the same reason SpeakerDeviceId is [HiddenOnMacOS]).
            BtnSpeakerDevice.IsVisible = pickable && !OperatingSystem.IsMacOS()
                && RealDeviceIds(CaptureSource.Speaker).Count > 1;
            BtnWebcamDevice.IsVisible = pickable && RealDeviceIds(CaptureSource.Webcam).Count > 1;

            ApplyPillLane();
            UpdatePillPositions();
        }

        /// <summary>Takes the camera list the recorder produced, and lets the CAM pill appear (or
        /// stay away) now that there is something to count.</summary>
        private async Task LoadCamerasAsync(Task<List<CameraDeviceInfo>> pending)
        {
            try
            {
                _cameras = await pending;
            }
            catch (Exception ex)
            {
                // CameraDeviceManager never throws, so this is a task-scheduling failure only.
                Debug.WriteLine("Failed to list cameras for the toolbar: " + ex);
                _cameras = new List<CameraDeviceInfo>();
            }

            // the camera half of the open-time audit could not run in the constructor: there was no
            // list to check the stored id against yet. This is that moment.
            DisableCaptureWithoutDevice();
            UpdateDevicePills();
        }

        /// <summary>
        /// A capture toggle that survived from a previous session pointing at a device that is no
        /// longer there is a promise the recording cannot keep — it would come back with a silent
        /// track, or no track at all. Runs while the strip is being built (before the recorder is
        /// spawned, so the settings file it reads already agrees) — which is also why it writes the
        /// settings rather than only the buttons — and again when the camera list lands, since the
        /// webcam's id has nothing to be checked against until then.
        /// </summary>
        private void DisableCaptureWithoutDevice()
        {
            var changed = false;

            if (_settings.CaptureMicrophone && !HasDevice(CaptureSource.Microphone))
            {
                _settings.CaptureMicrophone = false;
                changed = true;
            }

            if (_settings.CaptureSpeaker && !HasDevice(CaptureSource.Speaker))
            {
                _settings.CaptureSpeaker = false;
                changed = true;
            }

            // composition being off is not a missing device — the camera rows are merely gated,
            // and the user's tick is still what they will get back when they switch it on again.
            if (_settings.CaptureWebcam && _settings.EnableComposition && !HasDevice(CaptureSource.Webcam))
            {
                _settings.CaptureWebcam = false;
                changed = true;
            }

            if (changed)
                QueueSettingsSave();
        }

        /// <summary>
        /// Drops the device picker for <paramref name="source"/> off that source's pill, or off the
        /// tile itself when there is no pill — a source with no device at all has none, and the
        /// menu is then the only thing that can say so.
        /// <paramref name="enableOnPick"/> is set when the menu was opened by a turn-on that had
        /// nowhere to record from: choosing a device then completes the click the user made.
        /// Opening the same menu from the pill is a device change only and leaves the toggle alone.
        /// </summary>
        private void OpenDeviceMenu(CaptureSource source, bool enableOnPick)
        {
            var pill = PillFor(source);
            var anchor = pill.IsVisible ? (Control)pill : TileFor(source);
            var flyout = new MenuFlyout
            {
                // the free edge, same reasoning (and the same two cases) as the tooltips
                Placement = MainPanel.Orientation == Orientation.Horizontal
                    ? PlacementMode.BottomEdgeAlignedLeft
                    : PlacementMode.RightEdgeAlignedTop,
            };

            if (source == CaptureSource.Webcam)
                // …and again on every open, so the list is current without the user having to ask
                // for a refresh. This is what replaced the explicit "Refresh camera list" row.
                FillCameraMenu(flyout, CameraDeviceManager.RefreshAsync(), enableOnPick);
            else
                FillAudioMenu(flyout, source, enableOnPick);

            flyout.ShowAt(anchor);
        }

        private CaptureToolPill PillFor(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => BtnMicDevice,
            CaptureSource.Speaker => BtnSpeakerDevice,
            _ => BtnWebcamDevice,
        };

        private CaptureToolButton TileFor(CaptureSource source) => source switch
        {
            CaptureSource.Microphone => BtnMic,
            CaptureSource.Speaker => BtnSpeaker,
            _ => BtnWebcam,
        };

        /// <summary>Audio enumeration is a local call (WASAPI / CoreAudio), so the menu is built
        /// complete. A stored device that is no longer connected is simply absent, leaving nothing
        /// checked — which is the truth: it is not what would be recorded.</summary>
        private void FillAudioMenu(MenuFlyout flyout, CaptureSource source, bool enableOnPick)
        {
            var isSpeaker = source == CaptureSource.Speaker;
            var devices = isSpeaker ? AudioDeviceManager.GetSpeakers() : AudioDeviceManager.GetMicrophones();
            var current = isSpeaker ? _settings.SpeakerDeviceId : _settings.MicrophoneDeviceId;

            // "default" alone is not an offer: it would point at nothing, and picking it would
            // turn the source on to record silence.
            if (!devices.Any(d => d.DeviceId != AudioDeviceManager.DefaultDeviceId))
            {
                flyout.Items.Add(new MenuItem { Header = isSpeaker ? "No speakers found" : "No microphones found", IsEnabled = false });
                return;
            }

            foreach (var device in devices)
                flyout.Items.Add(DeviceItem(device.FriendlyName, device.DeviceId, current, source, enableOnPick));
        }

        /// <summary>The enumeration is off-thread (a native call, but a click must never wait on
        /// one), so the menu opens on a placeholder and fills itself when the list lands. That is
        /// usually the same frame; it is visible only on the recorder fallback path.</summary>
        private void FillCameraMenu(MenuFlyout flyout, Task<List<CameraDeviceInfo>> pending, bool enableOnPick)
        {
            flyout.Items.Add(new MenuItem { Header = "Looking for cameras…", IsEnabled = false });
            _ = FillCameraMenuAsync(flyout, pending, enableOnPick);
        }

        private async Task FillCameraMenuAsync(MenuFlyout flyout, Task<List<CameraDeviceInfo>> pending, bool enableOnPick)
        {
            // shares the strip's one camera list, so an enumeration that lands while the menu is
            // open also settles whether the CAM pill belongs there at all.
            await LoadCamerasAsync(pending);
            var cameras = _cameras;

            // the menu may have been dismissed (or the whole window closed) while the recorder was
            // listing devices; refilling a detached flyout is harmless, and nothing here reopens it.
            flyout.Items.Clear();

            if (cameras.Count == 0)
                flyout.Items.Add(new MenuItem { Header = "No cameras found", IsEnabled = false });

            foreach (var camera in cameras)
                flyout.Items.Add(DeviceItem(camera.FriendlyName, camera.DeviceId, _settings.WebcamDeviceId, CaptureSource.Webcam, enableOnPick));
        }

        private MenuItem DeviceItem(string header, string deviceId, string currentId, CaptureSource source, bool enableOnPick)
        {
            var item = new MenuItem
            {
                Header = header,
                // radio rather than a checkmark: exactly one device is recorded per source, and
                // the group makes the menu say so on its own.
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "clowdToolbarDevice" + source,
                IsChecked = deviceId == currentId,
            };
            item.Click += (s, e) => PickDevice(source, deviceId, enableOnPick);
            return item;
        }

        /// <summary>Applies a device chosen from one of the pickers. Writing the settings property
        /// is the whole of it — VideoCapturePage turns the change into a <c>configure</c> on the
        /// waiting recorder, exactly as it does for the settings page — plus the deferred turn-on
        /// when this menu only opened because the source had no device to record from.</summary>
        private void PickDevice(CaptureSource source, string deviceId, bool enable)
        {
            switch (source)
            {
                case CaptureSource.Microphone:
                    _settings.MicrophoneDeviceId = deviceId;
                    if (enable && !_micEnabled)
                        SetMicEnabled(true);
                    break;

                case CaptureSource.Speaker:
                    _settings.SpeakerDeviceId = deviceId;
                    if (enable && !_spkEnabled)
                        SetSpeakerEnabled(true);
                    break;

                default:
                    // the device first, so the toggle that follows is already backed by a camera —
                    // the settings file writes an empty webcam_device for either half missing.
                    _settings.WebcamDeviceId = deviceId;
                    if (enable && !_camEnabled)
                        SetWebcamEnabled(true);
                    break;
            }

            QueueSettingsSave();
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
                Fill = AppStyles.CaptureAccentBackgroundBrush,
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
        /// or the capturer was torn down — the fill empties rather than freezing. No-ops on a
        /// share strip, which never built the bars.</summary>
        public void SetAudioLevels(double? micDb, double? spkDb)
        {
            if (!IsRecordingProfile)
                return;

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
