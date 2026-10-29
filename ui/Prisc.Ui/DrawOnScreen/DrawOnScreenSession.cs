using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Reactive;
using Avalonia.Threading;
using Prisc.Config;
using Prisc.Drawing;
using Prisc.UI.Helpers;
using Prisc.Util;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>
    /// A draw-on-screen session: one <see cref="DrawOnScreenWindow"/> per monitor, the
    /// <see cref="DrawOnScreenToolbar"/> that drives them, one <see cref="DrawHistory"/> across their
    /// separate undo stacks, and the per-tool settings the canvases draw with (the session's own
    /// table — the editor's persisted tool settings are never read or written; only the palette
    /// colour and size, and a frozen session's drawing tool, are remembered across sessions, in
    /// <see cref="SettingsGeneral"/>). Single-instance via
    /// <see cref="ActiveInstance"/>, UI thread only, like the recording and share pages.
    /// <para>
    /// <see cref="Apply"/> is the one place the mode is written: every transition of
    /// <see cref="DrawOnScreenState"/> goes through it and lands on every canvas, so the toolbar
    /// (which only reads <see cref="State"/>) can never show a mode the canvases are not in.
    /// </para>
    /// <para>
    /// The hotkeys (<see cref="DrawHotkeys"/>) are live while the session has the keyboard — its toolbar
    /// or one of its canvases is focused (<see cref="IsFocused"/>) — and drawing never goes on without
    /// it: the keyboard leaving for another window drops the session to click-through
    /// (<see cref="OnFocusSettled"/>), so a tool in hand always means the keys work. Only clicking away
    /// from click-through, or closing, gives the keyboard up for good.
    /// </para>
    /// <para>
    /// With <see cref="DrawOnScreenState.ShowKeys"/> on, the keys pressed anywhere on the system are
    /// fed (<see cref="KeystrokeFeed"/>) into one <see cref="LiveKeystrokes"/> and drawn by the
    /// <see cref="KeystrokeOverlay"/> of the screen the mouse was on at the last press, styled live
    /// by <see cref="SettingsDrawOnScreen"/>.
    /// </para>
    /// <para>
    /// A frozen session (<see cref="ToggleOnScreenshot"/>, issues #111 and #114) grabs the monitor the
    /// mouse is on before anything is shown and draws over that picture instead of the live desktop,
    /// opening zoomed in on the mouse by <see cref="SettingsDrawOnScreen.ScreenshotZoom"/>; the other
    /// monitors get no window. It is drawing for
    /// its whole life (<see cref="DrawOnScreenState.Frozen"/>): with no desktop to click through to,
    /// Esc and the keyboard leaving close it instead.
    /// </para>
    /// </summary>
    public sealed class DrawOnScreenSession : IDrawOnScreenController
    {
        // display changes arrive in bursts (one per monitor, then the work areas); settle first
        private static readonly TimeSpan HotplugSettle = TimeSpan.FromMilliseconds(500);

        // the keyboard moves between the session's own windows (into the text tool's edit and back out)
        // by deactivating one before activating the other: a loss is only believed once this has passed
        private static readonly TimeSpan FocusSettle = TimeSpan.FromMilliseconds(150);

        /// <summary>The live session, or null. UI thread only.</summary>
        public static DrawOnScreenSession ActiveInstance { get; private set; }

        private readonly Dictionary<ToolType, SavedToolSettings> _settings = new();
        private readonly DrawHistory _history = new();
        private readonly List<Overlay> _overlays = new();
        private DrawOnScreenToolbar _toolbar;
        private DispatcherTimer _hotplug;

        // one-way latch: every terminal path sets it first, so a Closed raised by our own Close()
        // calls, or a late canvas event, cannot start a second teardown
        private bool _closing;

        // the canvases' hotkeys (the toolbar routes its own); see IsFocused for the settled focus
        private readonly DrawHotkeys _canvasHotkeys;
        private bool _focused;
        private DispatcherTimer _focusLoss;
        private IDisposable _foregroundWatch;

        // the keystroke overlay: the rows, the hook feeding them, the settings styling them, and
        // the one screen drawing them
        private readonly LiveKeystrokes _keys = new(OperatingSystem.IsMacOS());
        private KeystrokeFeed _feed;
        private SettingsDrawOnScreen _keySettings;
        private Overlay _keysOverlay;

        // over a screenshot taken as the session opened (see ToggleOnScreenshot)
        private readonly bool _frozen;

        private DrawOnScreenSession(bool frozen)
        {
            _frozen = frozen;
            _canvasHotkeys = new DrawHotkeys(this);
        }

        public DrawOnScreenState State { get; private set; } = DrawOnScreenState.Initial;

        /// <summary>Whether the session has the keyboard, settled: a gain counts at once, a loss only once
        /// <see cref="FocusSettle"/> has shown it is not the keyboard moving between our own windows.</summary>
        public bool IsFocused => _focused;

        public bool CanUndo => _history.CanUndo;

        public bool CanClear { get; private set; }

        public event EventHandler Changed;

        /// <summary>
        /// The tray action: starts a session, or brings the live one back to the front (its windows
        /// are topmost, but a later topmost window — another Prisc overlay, say — can have covered
        /// them). Only the toolbar's Close discards the ink.
        /// </summary>
        public static void Toggle() => Start(frozen: false);

        /// <summary>
        /// The Draw on Instant Screenshot action: starts a session drawn over a picture of the
        /// mouse's monitor taken right now, so whatever was on screen holds still to be drawn on. A session
        /// already up, frozen or not, is brought back to the front as by <see cref="Toggle"/>: its
        /// ink is never thrown away for a new one.
        /// </summary>
        public static void ToggleOnScreenshot() => Start(frozen: true);

        private static void Start(bool frozen)
        {
            Dispatcher.UIThread.VerifyAccess();

            if (ActiveInstance is { } active)
            {
                active.Raise();

                // the press found a session already up: point at its toolbar, wherever it was left, and
                // hand it the keyboard again
                active._toolbar?.ReplayIntro();
                active.TakeKeyboard();
                return;
            }

            // switched off in settings: the entry points are gone, and this backstops any caller
            // that still holds one (a strip built before the switch)
            if (SettingsRoot.Current?.DrawOnScreen is { IsEnabled: false })
                return;

            // unpermitted, macOS would freeze the bare desktop picture; Linux cannot grab at all
            if (frozen && !ScreenPixels.IsAvailable)
            {
                if (ScreenPixels.IsSupported)
                    _ = ScreenCapturePage.EnsureScreenRecordingPermissionAsync();
                return;
            }

            new DrawOnScreenSession(frozen).Open();
        }

        private void Open()
        {
            if (ActiveInstance != null)
                return;

            ActiveInstance = this;

            // before anything is built, so every canvas and the toolbar's first frame use the last
            // session's brush
            var general = SettingsRoot.Current?.General;
            _keySettings = SettingsRoot.Current?.DrawOnScreen;
            State = DrawOnScreenState.Restored(general?.DrawOnScreenColorIndex, general?.DrawOnScreenSizeIndex,
                _keySettings?.ShowKeystrokes ?? false);
            if (_frozen)
                State = State.Freeze(general?.DrawOnScreenshotTool);

            ApplyKeystrokeStyle();
            if (_keySettings != null)
                _keySettings.PropertyChanged += OnKeySettingsChanged;

            try
            {
                // The toolbar exists first and is SHOWN last. DesktopScreens needs a live window on
                // macOS (Avalonia's screen list hangs off Window.Screens; on Windows the argument is
                // ignored), and nothing else is up yet. Showing it after the canvases keeps it above
                // them: among topmost peers the later Show() wins, and RaiseTopmostNoActivate re-asserts
                // it below anyway.
                _toolbar = new DrawOnScreenToolbar(this);
                _toolbar.Closed += OnToolbarClosed;
                _toolbar.Opened += (s, e) => KeepToolbarOnTop();
                _toolbar.PropertyChanged += OnToolbarPropertyChanged;
                _toolbar.Screens.Changed += OnScreensChanged;
                // a click on the strip can bring the keyboard without IsActive changing (the strip was
                // already its thread's active window, just not in front), so look again after one
                _toolbar.AddHandler(InputElement.PointerPressedEvent,
                    (s, e) => Dispatcher.UIThread.Post(OnFocusMoved, DispatcherPriority.Background),
                    RoutingStrategies.Tunnel, handledEventsToo: true);

                var screens = DesktopScreens.All(_toolbar);
                if (screens.Count == 0 && DesktopScreens.Primary(_toolbar) is { } primary)
                    screens = new[] { primary };

                // the strip goes to the monitor the user is looking at, which the mouse is the best
                // guess of before any pointer event has reached us
                var hasCursor = CursorPosition.TryGet(out var cursor);
                var anchor = (hasCursor ? DesktopScreens.FromPoint(_toolbar, cursor) : null)
                    ?? DesktopScreens.Primary(_toolbar)
                    ?? screens.FirstOrDefault();

                // a frozen session covers only that monitor; the others stay the live desktop
                if (_frozen)
                    screens = anchor != null ? new[] { anchor } : Array.Empty<DesktopScreen>();

                // grabbed before the first canvas shows, which would otherwise be in the picture
                var backdrops = _frozen ? GrabScreens(screens) : null;
                if (_frozen && (backdrops == null || backdrops.Length == 0))
                {
                    _ = ShutdownAsync();
                    return;
                }

                for (var i = 0; i < screens.Count; i++)
                {
                    var overlay = AddOverlay(screens[i], backdrops?[i]);

                    // opened zoomed in on the mouse (the screen's middle if it could not be read)
                    if (_frozen && _keySettings is { } settings)
                    {
                        var bounds = screens[i].Bounds;
                        overlay.Window.ZoomBackdropAt(settings.ScreenshotZoom / 100.0,
                            hasCursor ? cursor : new PixelPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                    }
                }

                if (anchor != null)
                    _toolbar.ShowAtBottomOf(anchor.WorkingArea);
                else
                    _toolbar.Show();
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);

                // HasKeyboard reads the system foreground on Windows, so that is what must re-check it:
                // the windows' IsActive changes alone miss a foreground that leaves while IsActive is
                // out of step with it (as TakeKeyboard's refused first attempt leaves it), and the
                // strip then stays lit for good
                _foregroundWatch = WindowNativeExtensions.WatchForeground(OnFocusMoved);

                // opened from the tray or the hotkey, the session starts focused on its toolbar (its
                // hotkeys live). The toolbar opens without activation, and the hotkey that got us here
                // was swallowed by the keyboard hook, not delivered to us: see TakeKeyboard.
                TakeKeyboard();

                UpdateKeystrokes();
                OnChanged();
            }
            catch
            {
                // a half-built session must not stay the ActiveInstance: every later Toggle() would only
                // Raise() it, and any canvases already shown would be left with no toolbar to close them
                _ = ShutdownAsync();
                throw;
            }
        }

        // ====================================================================
        // IDrawOnScreenController
        // ====================================================================

        public void PickTool(ToolType tool)
        {
            CommitTextEdits();
            Apply(State.PickTool(tool));
        }

        public void ToggleClickThrough()
        {
            CommitTextEdits();
            Apply(State.ToggleClickThrough());
        }

        public void StopDrawing()
        {
            CommitTextEdits();
            Apply(State.StopDrawing());
        }

        public void ToggleHide()
        {
            CommitTextEdits();
            Apply(State.ToggleHide());
        }

        public void ToggleKeys() => Apply(State.ToggleKeys());

        public void SelectColor(int index)
        {
            CommitTextEdits();
            Apply(State.SelectColor(index));
        }

        public void SelectSize(int index)
        {
            CommitTextEdits();
            Apply(State.SelectSize(index));
        }

        public void Undo()
        {
            if (_closing)
                return;

            // committing an open edit records it first, so this undo takes the text back
            CommitTextEdits();
            _history.Undo();
            RecomputeCanClear();
            Apply(State.AfterUndoOrClear());
        }

        public void ClearAll()
        {
            if (_closing)
                return;

            CommitTextEdits();

            // every canvas's clear is its own history step; the group makes them one undo
            _history.BeginGroup();
            try
            {
                foreach (var overlay in _overlays)
                {
                    if (overlay.Canvas.GraphicsList.Count > 0)
                        overlay.Canvas.DeleteAll();
                }
            }
            finally
            {
                _history.EndGroup();
            }

            RecomputeCanClear();
            Apply(State.AfterUndoOrClear());
        }

        /// <summary>The select tool's Delete. Unlike clear, the mode is left alone: the select tool stays
        /// in hand for the next pick. A selection only lives while that tool is (see <see cref="PushState"/>).</summary>
        public void DeleteSelection()
        {
            if (_closing || !_overlays.Any(o => o.Canvas.SelectedCount > 0))
                return;

            // a selection on several monitors is one undo, as for clear
            _history.BeginGroup();
            try
            {
                foreach (var overlay in _overlays)
                {
                    if (overlay.Canvas.SelectedCount > 0)
                        overlay.Canvas.Delete();
                }
            }
            finally
            {
                _history.EndGroup();
            }

            RecomputeCanClear();
            OnChanged();
        }

        public void Close() => _ = ShutdownAsync();

        /// <summary>
        /// Ends the session: closes the toolbar and every canvas window programmatically (their
        /// Alt+F4 guard lets a programmatic Close() through) and discards the ink and its history.
        /// Idempotent. The app-exit path awaits this the way it awaits the recording and share
        /// pages'; there is nothing asynchronous to wait for here, the shape is shared for the caller.
        /// </summary>
        public Task ShutdownAsync()
        {
            if (_closing)
                return Task.CompletedTask;
            _closing = true;

            _hotplug?.Stop();
            _hotplug = null;
            _focusLoss?.Stop();
            _focusLoss = null;
            _foregroundWatch?.Dispose();
            _foregroundWatch = null;

            _feed?.Dispose();
            _feed = null;
            if (_keySettings != null)
                _keySettings.PropertyChanged -= OnKeySettingsChanged;

            if (_toolbar != null)
            {
                _toolbar.Closed -= OnToolbarClosed;
                _toolbar.PropertyChanged -= OnToolbarPropertyChanged;
                _toolbar.Screens.Changed -= OnScreensChanged;
                try { _toolbar.Close(); }
                catch { }
            }

            foreach (var overlay in _overlays.ToArray())
            {
                overlay.Detach();
                try { overlay.Window.Close(); }
                catch { }
            }
            _overlays.Clear();

            _history.Clear();

            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;

            return Task.CompletedTask;
        }

        // ====================================================================
        // State
        // ====================================================================

        /// <summary>The single writer of the mode: stores it, lands it on every canvas window, and
        /// tells the toolbar.</summary>
        private void Apply(DrawOnScreenState next)
        {
            if (_closing)
                return;

            var prev = State;
            State = next;

            foreach (var overlay in _overlays)
                PushState(overlay);

            if (prev.ColorIndex != next.ColorIndex || prev.SizeIndex != next.SizeIndex)
            {
                ApplyColorAndSize();
                RememberColorAndSize();
            }

            if (prev.ShowKeys != next.ShowKeys)
            {
                UpdateKeystrokes();
                RememberShowKeys();
            }

            if (next.Frozen && prev.Tool != next.Tool && DrawOnScreenState.IsDrawingTool(next.Tool))
                RememberScreenshotTool();

            OnChanged();
        }

        /// <summary>Stores the colour and size for the next session (see <see cref="Open"/>). Saved at
        /// the pick rather than at close, so a crash or a kill still keeps it.</summary>
        private void RememberColorAndSize()
        {
            // null before App startup assigns SettingsRoot.Current (and in harnesses that never do)
            if (SettingsRoot.Current?.General is not { } general)
                return;

            general.DrawOnScreenColorIndex = State.ColorIndex;
            general.DrawOnScreenSizeIndex = State.SizeIndex;
            try { SettingsService.Save(SettingsRoot.Current); }
            catch { }
        }

        /// <summary>Stores a frozen session's drawing tool for the next frozen session, at the pick like
        /// the colour. Live sessions skip this: they always open in click-through.</summary>
        private void RememberScreenshotTool()
        {
            if (SettingsRoot.Current?.General is not { } general)
                return;

            general.DrawOnScreenshotTool = State.Tool;
            try { SettingsService.Save(SettingsRoot.Current); }
            catch { }
        }

        /// <summary>Stores the keys button for the next session, at the press like the colour.</summary>
        private void RememberShowKeys()
        {
            if (_keySettings == null)
                return;

            _keySettings.ShowKeystrokes = State.ShowKeys;
            try { SettingsService.Save(SettingsRoot.Current); }
            catch { }
        }

        /// <summary>Lands <see cref="State"/> on one window. Click-through first: turning it on
        /// ends any gesture before the tool is (re)asserted. A selection only lives while the select
        /// tool is in hand, and is dropped before the tool changes: with a selection up the canvas's
        /// colour and size are bound to the selected graphics, and the colour and size writes that
        /// follow a tool change must land in the tool's settings, not restyle the selection.</summary>
        private void PushState(Overlay overlay)
        {
            overlay.Window.SetClickThrough(State.ClickThrough);
            if (!State.IsToolLit(ToolType.Pointer))
                overlay.Canvas.UnselectAll();
            overlay.Canvas.Tool = State.Tool;
            overlay.Canvas.Opacity = State.Hidden ? 0 : 1;
        }

        /// <summary>
        /// Writes the selected colour and size into every tool's settings entry, then onto the
        /// canvases directly (the canvas properties are bound to the active tool's entry, and the
        /// direct write also redraws the brush cursor at the new width).
        /// </summary>
        private void ApplyColorAndSize()
        {
            var color = DrawPalette.Colors[State.ColorIndex];
            var size = DrawPalette.Sizes[State.SizeIndex];

            foreach (var (tool, settings) in _settings)
                Style(tool, settings, color, size);

            foreach (var overlay in _overlays)
                PushStyle(overlay.Canvas, color, size);
        }

        private void PushStyle(DrawingCanvas canvas, Color color, DrawPalette.DrawSize size)
        {
            canvas.ObjectColor = color;
            canvas.LineWidth = DrawPalette.LineWidthFor(State.Tool, size);
            canvas.TextFontSize = DrawPalette.FontSizeFor(State.Tool, size);
        }

        private static void Style(ToolType tool, SavedToolSettings settings, Color color, DrawPalette.DrawSize size)
        {
            settings.ObjectColor = color;
            settings.LineWidth = DrawPalette.LineWidthFor(tool, size);
            settings.FontSize = DrawPalette.FontSizeFor(tool, size);
        }

        /// <summary>The canvases' settings lookup: a fresh default entry per tool, styled with the
        /// current colour and size, so a tool picked for the first time draws like the others.</summary>
        private SavedToolSettings ResolveSettings(ToolType tool)
        {
            if (!_settings.TryGetValue(tool, out var settings))
            {
                settings = SavedToolSettings.CreateDefault(tool);
                // text is ink on the desktop, not a card
                settings.FillColor = Colors.Transparent;
                Style(tool, settings, DrawPalette.Colors[State.ColorIndex], DrawPalette.Sizes[State.SizeIndex]);
                _settings[tool] = settings;
            }

            return settings;
        }

        private void CommitTextEdits()
        {
            foreach (var overlay in _overlays)
                overlay.Canvas.CommitTextEdit();
        }

        /// <summary>
        /// Whether any canvas holds a graphic. Only read at history appends, undo and hotplug — never
        /// from the list's own change events: the eraser's marquee is a graphic in the list while it
        /// is dragged, and following the list would flicker the Clear button through every drag. At
        /// an append the marquee is already gone, so a plain count is the right answer.
        /// </summary>
        private void RecomputeCanClear()
        {
            CanClear = _overlays.Any(o => o.Canvas.GraphicsList.Count > 0);
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Re-asserts every window above its topmost peers, the toolbar last so it stays
        /// above the canvases.</summary>
        /// <summary>
        /// Puts the strip back above the canvases. Showing it last is not enough on its own: a canvas
        /// finishes opening (and is restyled) after its Show() returns, which can land it above the
        /// strip, and a canvas over the strip makes Close unreachable. Posted so it runs after any
        /// such late work; cheap enough to repeat on every press on a canvas.
        /// </summary>
        private void KeepToolbarOnTop()
        {
            WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
            Dispatcher.UIThread.Post(() => WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar), DispatcherPriority.Background);
        }

        private void Raise()
        {
            foreach (var overlay in _overlays)
                WindowNativeExtensions.RaiseTopmostNoActivate(overlay.Window);
            WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
        }

        // ====================================================================
        // Keystrokes
        // ====================================================================

        /// <summary>Starts or stops the keystroke overlay to match <see cref="State"/>. Turning it
        /// off forgets every key, so turning it back on starts clean.</summary>
        private void UpdateKeystrokes()
        {
            if (_closing)
                return;

            if (State.ShowKeys)
            {
                _feed ??= new KeystrokeFeed(OnKeystroke);
                _feed.Start();
                TargetKeystrokes(_keysOverlay ?? OverlayUnderCursor());
            }
            else
            {
                _feed?.Stop();
                _keys.Clear();
                TargetKeystrokes(null);
            }
        }

        /// <summary>Points the one live overlay at the keys and blanks the others.</summary>
        private void TargetKeystrokes(Overlay target)
        {
            _keysOverlay = target;
            foreach (var overlay in _overlays)
            {
                if (overlay == target)
                    overlay.Window.Keystrokes.SetSource(_keys, () => _feed?.NowMs ?? 0, _keySettings ?? new SettingsDrawOnScreen());
                else
                    overlay.Window.Keystrokes.SetSource(null, null, null);
            }
        }

        /// <summary>The canvas of the monitor the mouse is on, the screen the user is working on;
        /// the first canvas when that cannot be told.</summary>
        private Overlay OverlayUnderCursor()
        {
            if (_toolbar != null && CursorPosition.TryGet(out var cursor)
                && DesktopScreens.FromPoint(_toolbar, cursor) is { } screen)
            {
                var match = _overlays.FirstOrDefault(o => o.Window.Screen.Bounds == screen.Bounds);
                if (match != null)
                    return match;
            }

            return _overlays.FirstOrDefault();
        }

        /// <summary>One key edge from the hook, on the UI thread. A press moves the rows to the
        /// screen the mouse is on now, so the keys follow the user between monitors.</summary>
        private void OnKeystroke(KeystrokeEdge edge)
        {
            if (_closing || !State.ShowKeys)
                return;

            if (edge.IsDown)
            {
                _keys.KeyDown(edge.Code, edge.Character, edge.TimeMs);
                var target = OverlayUnderCursor();
                if (target != _keysOverlay)
                    TargetKeystrokes(target);
            }
            else
            {
                _keys.KeyUp(edge.Code, edge.TimeMs);
            }

            _keysOverlay?.Window.Keystrokes.Refresh();
        }

        private void OnKeySettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closing)
                return;

            ApplyKeystrokeStyle();
            _keysOverlay?.Window.Keystrokes.Refresh();
        }

        /// <summary>Copies the settings into the keyboard track's own style objects, which is what
        /// the shared renderer reads.</summary>
        private void ApplyKeystrokeStyle()
        {
            var s = _keySettings ?? new SettingsDrawOnScreen();
            _keys.Style = new KeyboardContent
            {
                FontSize = s.FontSize,
                LingerMs = s.LingerMs,
                PauseBreakMs = s.PauseBreakMs,
                Filter = s.Filter switch
                {
                    DrawKeystrokeFilter.Special => KeystrokeFilter.Special,
                    DrawKeystrokeFilter.Shortcuts => KeystrokeFilter.Shortcuts,
                    _ => KeystrokeFilter.None,
                },
                TextColor = s.TextColor.ToUInt32(),
                BackgroundColor = s.FillColor.ToUInt32(),
            };
            _keys.Entry = RowTransition(s.EntryKind, s.EntryDurationMs, s.EntryEasing);
            _keys.Exit = RowTransition(s.ExitKind, s.ExitDurationMs, s.ExitEasing);
        }

        /// <summary>A settings animation as the SDK's <see cref="Transition"/>; the settings enums
        /// mirror the SDK's by name (Prisc.Shared cannot reference it).</summary>
        private static Transition RowTransition(DrawKeystrokeAnimation kind, int durationMs, DrawKeystrokeEasing easing) => new()
        {
            Kind = Enum.TryParse<TransitionKind>(kind.ToString(), out var k) ? k : TransitionKind.None,
            DurationTicks = Math.Max(0, durationMs) * TimeSpan.TicksPerMillisecond,
            Easing = Enum.TryParse<TransitionEasing>(easing.ToString(), out var e) ? e : TransitionEasing.Linear,
        };

        // ====================================================================
        // Canvas windows
        // ====================================================================

        /// <summary>Every monitor's picture, in <paramref name="screens"/>' order, or null (with none
        /// left undisposed) if any of them could not be taken: a frozen session with a live monitor
        /// in it would be neither one thing nor the other.</summary>
        private static Bitmap[] GrabScreens(IReadOnlyList<DesktopScreen> screens)
        {
            var shots = new Bitmap[screens.Count];
            for (var i = 0; i < screens.Count; i++)
            {
                shots[i] = ScreenPixels.Grab(screens[i].Bounds);
                if (shots[i] == null)
                {
                    Debug.WriteLine($"Draw on Instant Screenshot: could not grab the screen at {screens[i].Bounds}");
                    foreach (var shot in shots)
                        shot?.Dispose();
                    return null;
                }
            }

            return shots;
        }

        /// <summary>Creates, wires and shows the window for <paramref name="screen"/>, already in the
        /// current mode so its first frame is right. A frozen session passes the monitor's picture,
        /// which the window owns from here on.</summary>
        private Overlay AddOverlay(DesktopScreen screen, Bitmap backdrop = null)
        {
            var window = new DrawOnScreenWindow(screen, ResolveSettings, backdrop);
            var overlay = new Overlay(this, window);
            _overlays.Add(overlay);

            PushState(overlay);
            PushStyle(overlay.Canvas, DrawPalette.Colors[State.ColorIndex], DrawPalette.Sizes[State.SizeIndex]);
            overlay.Attach();

            window.Show();

            // the first canvas of a session with keys on, or a monitor plugged in with none showing
            if (State.ShowKeys && _keysOverlay == null)
                TargetKeystrokes(overlay);

            return overlay;
        }

        /// <summary>Takes a window out of the session (its monitor is gone, or it was closed from
        /// outside): its ink is forgotten by the history and the window closed if it still is open.</summary>
        private void RemoveOverlay(Overlay overlay)
        {
            overlay.Detach();
            _overlays.Remove(overlay);
            _history.Forget(overlay.Target);

            // its monitor is gone: the keys move to a screen that is still there
            if (overlay == _keysOverlay)
                TargetKeystrokes(State.ShowKeys ? _overlays.FirstOrDefault() : null);

            try { overlay.Window.Close(); }
            catch { }
        }

        private void OnHistoryAppended(Overlay overlay)
        {
            if (_closing)
                return;

            _history.Record(overlay.Target);
            RecomputeCanClear();
            OnChanged();
        }

        private void OnRightClicked(Overlay overlay)
        {
            if (_closing)
                return;

            Apply(State.StopDrawing());
        }

        /// <summary>
        /// Backstop for a silent tool reset inside the canvas: the overlay's tools are sticky, so
        /// while the user is drawing the canvas must hold the picked tool. Pushes <see cref="State"/>
        /// back if the canvas disagrees.
        /// </summary>
        private void OnCanvasToolChanged(Overlay overlay, ToolType tool)
        {
            if (_closing || State.ClickThrough || tool == State.Tool)
                return;

            overlay.Canvas.Tool = State.Tool;
        }

        /// <summary>
        /// The text tool needs the keyboard, and a WS_EX_NOACTIVATE window never gets it from a click.
        /// Activate() is an explicit request (NOACTIVATE blocks only click activation), so the window
        /// is made foreground for the edit and the keyboard handed back to the toolbar afterwards, so
        /// the hotkeys carry on — if the edit's end still finds this window focused (the keyboard may
        /// have gone elsewhere meanwhile, which is what ended the edit; <see cref="OnFocusSettled"/>
        /// deals with that). The hand-over in both directions is between the session's own windows,
        /// so <see cref="IsFocused"/> holds throughout.
        /// </summary>
        /// <remarks>
        /// TODO(smoke): if Activate() proves ineffective against the foreground lock, fall back to
        /// toggling WS_EX_NOACTIVATE off through the window's <see cref="ToggleableExStyles"/> for the
        /// duration of the edit.
        /// </remarks>
        private void OnTextEditingChanged(Overlay overlay)
        {
            if (_closing)
                return;

            if (overlay.Canvas.IsTextEditing)
            {
                overlay.Window.Activate();
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
            }
            else if (overlay.Window.IsActive)
            {
                _toolbar?.Activate();
            }
        }

        // ====================================================================
        // Focus
        // ====================================================================

        /// <summary>Whether the toolbar or a canvas has the keyboard right now (unsettled): the system's
        /// foreground window on Windows, not <see cref="WindowBase.IsActive"/>, which a refused
        /// SetForegroundWindow still sets; the key window of the active app on macOS (see
        /// <see cref="WindowNativeExtensions.IsForeground"/>).</summary>
        private bool HasKeyboard =>
            WindowNativeExtensions.IsForeground(_toolbar) || _overlays.Any(o => WindowNativeExtensions.IsForeground(o.Window));

        /// <summary>
        /// Puts the keyboard on the toolbar: past the foreground lock on Windows
        /// (<see cref="WindowNativeExtensions.ForceForeground"/>) — a hotkey swallowed by the keyboard
        /// hook does not count as input this process received, so a plain Activate is refused there
        /// while Avalonia still reports the strip active — then Activate for Avalonia's own focus.
        /// </summary>
        private void TakeKeyboard()
        {
            if (_toolbar == null)
                return;

            WindowNativeExtensions.ForceForeground(_toolbar);
            _toolbar.Activate();
            OnFocusMoved();
        }

        private void OnToolbarPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == WindowBase.IsActiveProperty)
                OnFocusMoved();
        }

        /// <summary>One of the session's windows gained or lost the keyboard. Not the Activated and
        /// Deactivated events: Avalonia raises them before it updates <see cref="WindowBase.IsActive"/>.</summary>
        private void OnFocusMoved()
        {
            if (_closing)
                return;

            if (HasKeyboard)
            {
                _focusLoss?.Stop();
                if (!_focused)
                {
                    _focused = true;
                    OnChanged();
                }
                return;
            }

            if (_focusLoss == null)
            {
                _focusLoss = new DispatcherTimer { Interval = FocusSettle };
                _focusLoss.Tick += (s, e) =>
                {
                    _focusLoss.Stop();
                    OnFocusSettled();
                };
            }

            _focusLoss.Stop();
            _focusLoss.Start();
        }

        /// <summary>
        /// The keyboard has really gone: the hotkeys stop (the strip disengages and its hints fly away),
        /// and a session that was drawing drops to click-through, so a tool is never left in hand with
        /// keys that do nothing. The keyboard is not fought for — a window the user switched to keeps it.
        /// A frozen session closes instead: it has no click-through, and its picture is of a moment the
        /// user has moved on from.
        /// </summary>
        private void OnFocusSettled()
        {
            if (_closing)
                return;

            // the keyboard arrived while the loss was settling (an activation can land a moment after
            // the IsActive change that announced it)
            if (HasKeyboard)
            {
                OnFocusMoved();
                return;
            }

            // only a keyboard the session had can be lost: one that never arrived (the foreground lock
            // won) leaves the strip unlit, for a click on a canvas to take it
            if (State.Frozen)
            {
                if (_focused)
                    Close();
                return;
            }

            _canvasHotkeys.Reset();
            var changed = _focused;
            _focused = false;

            if (State.IsDrawing)
                StopDrawing();
            else if (changed)
                OnChanged();
        }

        /// <summary>
        /// A press on a canvas re-asserts the strip above the canvases and, should the session be drawing
        /// without the keyboard (inside <see cref="FocusSettle"/>), takes it back for the toolbar: a press
        /// is input this process received, which is what lets it take the foreground. Windows only: a
        /// macOS canvas is made the key window by its own click, which already keeps the keyboard ours.
        /// </summary>
        private void OnCanvasPressed()
        {
            KeepToolbarOnTop();

            if (OperatingSystem.IsWindows() && State.IsDrawing && !HasKeyboard)
                TakeKeyboard();
        }

        private void OnCanvasHotkeyDown(KeyEventArgs e)
        {
            if (!_closing)
                _canvasHotkeys.KeyDown(e.Key, e.KeyModifiers);
        }

        private void OnCanvasHotkeyUp(KeyEventArgs e) => _canvasHotkeys.KeyUp(e.Key);

        /// <summary>A canvas window closed by something other than this session (App.CloseAllWindows
        /// at exit): the session cannot carry on with a hole in it.</summary>
        private void OnWindowClosed(Overlay overlay)
        {
            if (_closing)
                return;

            _ = ShutdownAsync();
        }

        private void OnToolbarClosed(object sender, EventArgs e)
        {
            if (_closing)
                return;

            _ = ShutdownAsync();
        }

        // ====================================================================
        // Display hotplug
        // ====================================================================

        /// <summary>Screens.Changed is a signal only (see <see cref="DesktopScreens"/> for why its
        /// list is not trusted); the monitors are re-read once the burst has settled.</summary>
        private void OnScreensChanged(object sender, EventArgs e)
        {
            if (_closing)
                return;

            if (_hotplug == null)
            {
                _hotplug = new DispatcherTimer { Interval = HotplugSettle };
                _hotplug.Tick += (s, args) =>
                {
                    _hotplug.Stop();
                    ReconcileScreens();
                };
            }

            // restart: another change inside the window pushes the reconcile out again
            _hotplug.Stop();
            _hotplug.Start();
        }

        /// <summary>
        /// Matches the windows to the monitors by bounds: a window whose monitor is still there keeps
        /// its ink (re-covering it if the scaling changed), a window whose monitor is gone is closed
        /// and forgotten by the history, and a new monitor gets an empty window (unless frozen).
        /// </summary>
        private void ReconcileScreens()
        {
            if (_closing)
                return;

            var screens = DesktopScreens.All(_toolbar);
            if (screens.Count == 0)
                return; // an enumeration failure is not "every monitor is gone"

            var matched = new HashSet<DesktopScreen>();
            foreach (var overlay in _overlays.ToArray())
            {
                var screen = screens.FirstOrDefault(s => s.Bounds == overlay.Window.Screen.Bounds);
                if (screen == null)
                {
                    RemoveOverlay(overlay);
                    continue;
                }

                matched.Add(screen);
                if (screen != overlay.Window.Screen)
                    overlay.Window.SetScreen(screen);
            }

            var added = false;
            foreach (var screen in screens)
            {
                // a frozen session only ever has a picture of the monitor it opened on: every other
                // one, present then or plugged in since, stays the live desktop
                if (matched.Add(screen) && !State.Frozen)
                {
                    AddOverlay(screen);
                    added = true;
                }
            }

            // a freshly shown canvas is the newest topmost window; the strip goes back above it
            if (added)
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);

            RecomputeCanClear();
            OnChanged();
        }

        /// <summary>One screen's window with the session's subscriptions on it, so attaching and
        /// detaching cannot drift apart.</summary>
        private sealed class Overlay
        {
            private readonly DrawOnScreenSession _session;
            private IDisposable _toolSubscription;

            public Overlay(DrawOnScreenSession session, DrawOnScreenWindow window)
            {
                _session = session;
                Window = window;
                Target = new CanvasUndoTarget(window.Canvas);
            }

            public DrawOnScreenWindow Window { get; }

            public DrawingCanvas Canvas => Window.Canvas;

            public CanvasUndoTarget Target { get; }

            public void Attach()
            {
                Canvas.HistoryAppended += OnHistoryAppended;
                Canvas.TextEditingChanged += OnTextEditingChanged;
                Window.RightClicked += OnRightClicked;
                Window.HotkeyDown += OnHotkeyDown;
                Window.HotkeyUp += OnHotkeyUp;
                Window.PropertyChanged += OnWindowPropertyChanged;
                Window.Closed += OnClosed;
                Window.Opened += OnOpened;
                Window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
                _toolSubscription = Canvas.GetObservable(DrawingCanvas.ToolProperty).Subscribe(new AnonymousObserver<ToolType>(OnToolChanged));
            }

            public void Detach()
            {
                Canvas.HistoryAppended -= OnHistoryAppended;
                Canvas.TextEditingChanged -= OnTextEditingChanged;
                Window.RightClicked -= OnRightClicked;
                Window.HotkeyDown -= OnHotkeyDown;
                Window.HotkeyUp -= OnHotkeyUp;
                Window.PropertyChanged -= OnWindowPropertyChanged;
                Window.Closed -= OnClosed;
                Window.Opened -= OnOpened;
                Window.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
                _toolSubscription?.Dispose();
                _toolSubscription = null;
            }

            private void OnHistoryAppended(object sender, EventArgs e) => _session.OnHistoryAppended(this);
            private void OnTextEditingChanged(object sender, EventArgs e) => _session.OnTextEditingChanged(this);
            private void OnRightClicked(object sender, EventArgs e) => _session.OnRightClicked(this);
            private void OnClosed(object sender, EventArgs e) => _session.OnWindowClosed(this);
            private void OnOpened(object sender, EventArgs e) => _session.KeepToolbarOnTop();
            private void OnPressed(object sender, PointerPressedEventArgs e) => _session.OnCanvasPressed();
            private void OnHotkeyDown(object sender, KeyEventArgs e) => _session.OnCanvasHotkeyDown(e);
            private void OnHotkeyUp(object sender, KeyEventArgs e) => _session.OnCanvasHotkeyUp(e);

            private void OnWindowPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == WindowBase.IsActiveProperty)
                    _session.OnFocusMoved();
            }
            private void OnToolChanged(ToolType tool) => _session.OnCanvasToolChanged(this, tool);
        }
    }
}
