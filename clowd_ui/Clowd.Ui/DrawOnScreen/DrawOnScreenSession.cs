using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.UI.Helpers;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// A draw-on-screen session: one <see cref="DrawOnScreenWindow"/> per monitor, the
    /// <see cref="DrawOnScreenToolbar"/> that drives them, one <see cref="DrawHistory"/> across their
    /// separate undo stacks, and the per-tool settings the canvases draw with (the session's own
    /// table — the editor's persisted tool settings are never read or written). Single-instance via
    /// <see cref="ActiveInstance"/>, UI thread only, like the recording and share pages.
    /// <para>
    /// <see cref="Apply"/> is the one place the mode is written: every transition of
    /// <see cref="DrawOnScreenState"/> goes through it and lands on every canvas, so the toolbar
    /// (which only reads <see cref="State"/>) can never show a mode the canvases are not in. Nothing
    /// here is a keyboard shortcut; the canvas windows swallow every key but the text tool's.
    /// </para>
    /// </summary>
    public sealed class DrawOnScreenSession : IDrawOnScreenController
    {
        // display changes arrive in bursts (one per monitor, then the work areas); settle first
        private static readonly TimeSpan HotplugSettle = TimeSpan.FromMilliseconds(500);

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

        // the window that had the keyboard before the text tool took it (see OnTextEditingChanged)
        private IntPtr _prevForeground;

        private DrawOnScreenSession()
        {
        }

        public DrawOnScreenState State { get; private set; } = DrawOnScreenState.Initial;

        public bool CanUndo => _history.CanUndo;

        public bool CanClear { get; private set; }

        public event EventHandler Changed;

        /// <summary>
        /// The tray action: starts a session, or brings the live one back to the front (its windows
        /// are topmost, but a later topmost window — another Clowd overlay, say — can have covered
        /// them). Only the toolbar's Close discards the ink.
        /// </summary>
        public static void Toggle()
        {
            Dispatcher.UIThread.VerifyAccess();

            if (ActiveInstance is { } active)
            {
                active.Raise();

                // the press found a session already up: point at its toolbar, wherever it was left
                active._toolbar?.ReplayIntroComet();
                return;
            }

            new DrawOnScreenSession().Open();
        }

        private void Open()
        {
            if (ActiveInstance != null)
                return;

            ActiveInstance = this;

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
                _toolbar.Screens.Changed += OnScreensChanged;

                var screens = DesktopScreens.All(_toolbar);
                if (screens.Count == 0 && DesktopScreens.Primary(_toolbar) is { } primary)
                    screens = new[] { primary };

                foreach (var screen in screens)
                    AddOverlay(screen);

                // the strip goes to the monitor the user is looking at, which the mouse is the best
                // guess of before any pointer event has reached us
                var anchor = (CursorPosition.TryGet(_toolbar, out var cursor) ? DesktopScreens.FromPoint(_toolbar, cursor) : null)
                    ?? DesktopScreens.Primary(_toolbar)
                    ?? screens.FirstOrDefault();
                if (anchor != null)
                    _toolbar.ShowAtBottomOf(anchor.WorkingArea);
                else
                    _toolbar.Show();
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);

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

        public void ToggleHide()
        {
            CommitTextEdits();
            Apply(State.ToggleHide());
        }

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

            if (_toolbar != null)
            {
                _toolbar.Closed -= OnToolbarClosed;
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
                ApplyColorAndSize();

            OnChanged();
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
            canvas.TextFontSize = size.TextSize;
        }

        private static void Style(ToolType tool, SavedToolSettings settings, Color color, DrawPalette.DrawSize size)
        {
            settings.ObjectColor = color;
            settings.LineWidth = DrawPalette.LineWidthFor(tool, size);
            settings.FontSize = size.TextSize;
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
        // Canvas windows
        // ====================================================================

        /// <summary>Creates, wires and shows the window for <paramref name="screen"/>, already in the
        /// current mode so its first frame is right.</summary>
        private Overlay AddOverlay(DesktopScreen screen)
        {
            var window = new DrawOnScreenWindow(screen, ResolveSettings);
            var overlay = new Overlay(this, window);
            _overlays.Add(overlay);

            PushState(overlay);
            PushStyle(overlay.Canvas, DrawPalette.Colors[State.ColorIndex], DrawPalette.Sizes[State.SizeIndex]);
            overlay.Attach();

            window.Show();
            return overlay;
        }

        /// <summary>Takes a window out of the session (its monitor is gone, or it was closed from
        /// outside): its ink is forgotten by the history and the window closed if it still is open.</summary>
        private void RemoveOverlay(Overlay overlay)
        {
            overlay.Detach();
            _overlays.Remove(overlay);
            _history.Forget(overlay.Target);

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

            Apply(State.CanvasRightClick());
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
        /// is made foreground for the edit and the keyboard handed back afterwards to whoever had it,
        /// if the edit's end still finds this window in front (the user may have clicked elsewhere
        /// meanwhile, which is what ended the edit — leave that alone).
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

            var self = overlay.Window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

            if (overlay.Canvas.IsTextEditing)
            {
                var foreground = WindowNativeExtensions.GetForegroundWindowHandle();
                // a chained edit can find this window still in front; never hand the keyboard "back"
                // to ourselves
                _prevForeground = foreground == self ? _prevForeground : foreground;

                overlay.Window.Activate();
                WindowNativeExtensions.RaiseTopmostNoActivate(_toolbar);
            }
            else
            {
                if (self != IntPtr.Zero && WindowNativeExtensions.GetForegroundWindowHandle() == self)
                    WindowNativeExtensions.TryRestoreForeground(_prevForeground);
                _prevForeground = IntPtr.Zero;
            }
        }

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
        /// and forgotten by the history, and a new monitor gets an empty window.
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
                if (matched.Add(screen))
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
            private void OnPressed(object sender, PointerPressedEventArgs e) => _session.KeepToolbarOnTop();
            private void OnToolChanged(ToolType tool) => _session.OnCanvasToolChanged(this, tool);
        }
    }
}
