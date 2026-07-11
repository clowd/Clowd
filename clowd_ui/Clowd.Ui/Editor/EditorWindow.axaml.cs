using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;
using Path = System.IO.Path;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        private ToolType? _panPreviousTool = null; // null means we're not in a held-key (shift/space) pan
        private SettingsRoot _settings = SettingsRoot.Current;
        private SessionInfo _session;
        private int _nudgeRepeatCount;
        private ScreenRect _normalBounds; // tracked manually while WindowState == Normal (decision table #55)
        private readonly HashSet<Key> _pressedKeys = new HashSet<Key>(); // repeat tracker (decision table #37)

        private readonly string _graphicsPath;
        private readonly string _historyPath;

        private DispatcherTimer _sessionInfoDebounce;
        private readonly bool _openedEmpty; // no graphics on the canvas when the window opened (see Closing)

        // graphics.json/history.json persistence is latest-wins on a background thread (see StateUpdated handler)
        private byte[] _pendingGraphicsJson;
        private byte[] _pendingHistoryJson;
        private Task _graphicsWriteTask = Task.CompletedTask;
        private readonly object _graphicsWriteLock = new object();

        // sidebar drag bounds — also mirrored onto the sidebar ColumnDefinition (MinWidth/MaxWidth)
        // so Avalonia's own splitter clamps the drag; the editor re-clamps on read/persist.
        private const double SidebarMinWidth = 140;
        private const double SidebarMaxWidth = 600;

        // the sidebar's ColumnDefinition (contentGrid column 3). Avalonia's XAML compiler does not
        // emit a field for an x:Named ColumnDefinition, so reach it through the named grid.
        private ColumnDefinition SidebarColumn => contentGrid.ColumnDefinitions[3];

        public RelayCommand SelectToolCommand { get; }
        public RelayCommand CommandSave { get; }
        public RelayCommand CommandCopy { get; }
        public RelayCommand CommandCut { get; }
        public RelayCommand CommandPaste { get; }
        public RelayCommand CommandUpload { get; }

        // satisfies the XAML compiler's runtime-loader check (AVLN3001); an editor is only
        // ever constructed through ShowSession with a real SessionInfo
        [Obsolete("Runtime-loader signature only — use EditorWindow(SessionInfo).", error: true)]
        public EditorWindow()
        {
            throw new NotSupportedException("EditorWindow requires a SessionInfo.");
        }

        public EditorWindow(SessionInfo info)
        {
            _session = info;
            _graphicsPath = Path.Combine(Path.GetDirectoryName(info.FilePath), "graphics.json");
            _historyPath = Path.Combine(Path.GetDirectoryName(info.FilePath), "history.json");

            SelectToolCommand = new RelayCommand { Executed = SelectToolExecuted, Text = "Select tool" };
            CommandSave = new RelayCommand { Executed = SaveCommandExecuted, Text = "_Save", Gesture = new SimpleKeyGesture(Key.S, KeyModifiers.Control) };
            CommandCopy = new RelayCommand { Executed = CopyCommandExecuted, Text = "_Copy", Gesture = new SimpleKeyGesture(Key.C, KeyModifiers.Control) };
            CommandCut = new RelayCommand { Executed = CutCommandExecuted, Text = "Cu_t", Gesture = new SimpleKeyGesture(Key.X, KeyModifiers.Control) };
            CommandPaste = new RelayCommand { Executed = PasteCommandExecuted, Text = "_Paste", Gesture = new SimpleKeyGesture(Key.V, KeyModifiers.Control) };
            CommandUpload = new RelayCommand { Executed = UploadCommandExecuted, Text = "_Upload", Gesture = new SimpleKeyGesture(Key.U, KeyModifiers.Control) };

            DataContext = this;

            InitializeComponent();

            drawingCanvas.ArtworkBackground = _settings.Editor.CanvasBackground;
            drawingCanvas.HandleColor = AppStyles.AccentColor;
            drawingCanvas.StateUpdated += drawingCanvas_StateUpdated;
            LoadSessionState();
            // a blank "new document" window (no capture, no restored graphics) that is still
            // blank when it closes is discarded rather than persisted to the recent list
            _openedEmpty = drawingCanvas.GraphicsList.Count == 0;

            // Modifier-carrying command gestures become Window.KeyBindings (§2.4). Bare gestures
            // (Escape/Delete/Home/End and the tool letters) are routed exclusively by the tunnel
            // KeyDown handler below — RelayCommand.CreateKeyBinding returns null for those.
            AddCommandKeyBinding(drawingCanvas.CommandSelectAll);
            AddCommandKeyBinding(drawingCanvas.CommandUnselectAll);
            AddCommandKeyBinding(drawingCanvas.CommandDelete);
            AddCommandKeyBinding(drawingCanvas.CommandMoveToFront);
            AddCommandKeyBinding(drawingCanvas.CommandMoveToBack);
            AddCommandKeyBinding(drawingCanvas.CommandMoveForward);
            AddCommandKeyBinding(drawingCanvas.CommandMoveBackward);
            AddCommandKeyBinding(drawingCanvas.CommandUndo);
            AddCommandKeyBinding(drawingCanvas.CommandRedo);
            AddKeyBinding(drawingCanvas.CommandZoomPanAuto, Key.D0);
            AddKeyBinding(drawingCanvas.CommandZoomPanAuto, Key.NumPad0);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D1);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad1);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D2, 2d);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad2, 2d);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D3, 3d);
            AddKeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad3, 3d);
            AddKeyBinding(CommandSave, Key.S);
            AddKeyBinding(CommandCopy, Key.C);
            AddKeyBinding(CommandCut, Key.X);
            AddKeyBinding(CommandPaste, Key.V);
            AddKeyBinding(CommandUpload, Key.U);

            AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);

            Opened += EditorWindow_Opened;
            Closing += EditorWindow_Closing;
            Activated += (_, _) => UpdateSessionInfo();
            Deactivated += (_, _) => {
                _pressedKeys.Clear();
                if (_panPreviousTool != null) {
                    // the pan key's KeyUp will never arrive; restore the tool now
                    drawingCanvas.Tool = _panPreviousTool.Value;
                    _panPreviousTool = null;
                }
                UpdateSessionInfo();
            };
            PositionChanged += (_, _) => {
                TrackNormalBounds();
                // fires for every pixel of a window drag, and a session write is a synchronous
                // disk serialize (FileSyncObject) — record the final position once the move settles
                ScheduleUpdateSessionInfo();
            };
            SizeChanged += (_, _) => TrackNormalBounds();
            ScalingChanged += (_, _) => drawingCanvas.UpdateScaleTransform(); // decision table #56

            btnUpload.AddHandler(PointerPressedEvent, btnUpload_RightMouseDown, RoutingStrategies.Tunnel);

            miniColor.ParentWindow = this;
            miniColor.Cancelled += (_, _) => miniColorPopup.IsOpen = false;

            // opt-in editor features (customizable toolbar / layers sidebar). The sidebar flag
            // defaults false, so with default settings the strip renders exactly as before plus
            // the customize button, and no sidebar.
            //
            // ApplySidebarVisible sets the border + splitter visibility, sizes (or collapses) the
            // sidebar column, and attaches the layers panel only when the sidebar is actually shown —
            // an IsVisible=false panel stays in the visual tree, so an unconditional Attach would
            // rebuild rows on every edit for a panel nobody can see. The SidebarVisible setter runs
            // the same path on toggle.
            ApplySidebarVisible(_settings.Editor.SidebarVisible);
            RebuildToolStrip();
            RebuildCustomizePopup();
        }

        private void ScheduleUpdateSessionInfo()
        {
            _sessionInfoDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => {
                _sessionInfoDebounce.Stop();
                UpdateSessionInfo();
            });
            _sessionInfoDebounce.Stop();
            _sessionInfoDebounce.Start();
        }

        private void UpdateSessionInfo()
        {
            if (_session != null) {
                _session.OpenEditor = new SessionOpenEditor() {
                    IsTopMost = Topmost,
                    IsMaximized = WindowState == WindowState.Maximized,
                    IsMinimized = WindowState == WindowState.Minimized,
                    RestorePosition = _normalBounds,
                    VirtualDesktopId = null, // virtual-desktop restore dropped (decision table #55)
                };
            }
        }

        private void TrackNormalBounds()
        {
            if (WindowState != WindowState.Normal)
                return;

            // physical px (Position is physical; ClientSize is logical → multiply by scaling)
            var scaling = RenderScaling;
            _normalBounds = new ScreenRect(
                Position.X,
                Position.Y,
                (int) Math.Round(ClientSize.Width * scaling),
                (int) Math.Round(ClientSize.Height * scaling));
        }

        private void EditorWindow_Opened(object sender, EventArgs e)
        {
            TrackNormalBounds();
            UpdateSessionInfo();

            drawingCanvas.Focus();

            // if window is bigger than image, show at actual size. else, zoom to fit (§5.1:
            // posted so it runs after the first layout pass has produced a real canvas size).
            Dispatcher.UIThread.Post(() => drawingCanvas.ZoomPanAuto(), DispatcherPriority.Loaded);
        }

        private void EditorWindow_Closing(object sender, WindowClosingEventArgs e)
        {
            _sessionInfoDebounce?.Stop();

            // an in-place text edit commits on focus loss (ToolText.FinishEdit); force that now,
            // while _session is still alive: FlushPendingState defers while an edit is active, and
            // a commit raised after this handler (window Deactivated fires post-close) is dropped
            // by the null-_session check — losing the typed text AND any armed autosave tail
            drawingCanvas.Focus();

            drawingCanvas.FlushPendingState(); // deliver any debounced (mid-scrub) canvas state before the writer flush below
            // flush any pending background graphics.json write before the session is torn down
            Task pendingWrite;
            lock (_graphicsWriteLock)
                pendingWrite = _graphicsWriteTask;
            try { pendingWrite.Wait(TimeSpan.FromSeconds(5)); } catch {; }
            WritePendingGraphicsJson();

            // the property bar mutates Editor.Tools (SavedToolSettings) through two-way bindings;
            // persist those edits when the editor closes (explicit-save policy). Saved before the
            // preview render so a rendering failure can't skip the save.
            UpdateSidebarWidthSetting(); // belt-and-suspenders: fold in any un-persisted drag width
            try { SettingsService.Save(_settings); } catch {; }

            // a session that opened blank and is still blank holds nothing worth keeping —
            // delete it (session dir included) so it never lingers in the recent sessions list.
            // Checked after the flushes above so a pending text edit / debounced state can't be
            // mistaken for an empty canvas.
            if (_openedEmpty && drawingCanvas.GraphicsList.Count == 0) {
                var session = _session;
                _session = null;
                session.OpenEditor = null; // DeleteSession refuses sessions marked open in an editor
                try {
                    SessionManager.Current.DeleteSession(session);
                } catch (Exception ex) {
                    Debug.WriteLine($"failed to discard empty session: {ex.Message}");
                }
                return;
            }

            UpdatePreview(drawingCanvas.DrawGraphicsToBitmap());
            _session.OpenEditor = null;
            _session = null;
        }

        public static void ShowSession(SessionInfo session)
        {
            // check if there is already a window open with this session in it
            if (session != null) {
                var openWnd = GetOpenEditors().FirstOrDefault(f => f._session == session);
                if (openWnd != null) {
                    if (openWnd.WindowState == WindowState.Minimized)
                        openWnd.WindowState = WindowState.Normal;
                    openWnd.Activate();
                    return;
                }
            }

            bool isExistingSession = session?.OpenEditor != null && session.OpenEditor.RestorePosition != null;
            bool canPlaceExactly = session?.OriginalBounds?.IsEmpty() == false;

            if (session == null)
                session = SessionManager.Current.CreateNewSession();

            var wnd = new EditorWindow(session);

            if (isExistingSession) {
                // this session was not closed properly, restore it to its previous location
                var restore = session.OpenEditor.RestorePosition;
                wnd.Topmost = session.OpenEditor.IsTopMost;
                wnd.ShowActivated = false;
                wnd.WindowStartupLocation = WindowStartupLocation.Manual;
                wnd.SetWindowRect(new PixelRect(restore.X, restore.Y, restore.Width, restore.Height));

                if (session.OpenEditor.IsMaximized)
                    wnd.WindowState = WindowState.Maximized;
                else if (session.OpenEditor.IsMinimized)
                    wnd.WindowState = WindowState.Minimized;

                wnd.Show();
            } else if (canPlaceExactly) {
                // this is a brand new session. we'll show it on top of the captured area.
                // (practical subset per §3 #55: no border compensation, constant padding only)
                var origRect = session.OriginalBounds;
                var origPx = new PixelRect(origRect.X, origRect.Y, origRect.Width, origRect.Height);
                var screen = wnd.Screens.ScreenFromBounds(origPx) ?? wnd.Screens.Primary;
                var workArea = screen?.WorkingArea ?? origPx;
                var scaling = screen?.Scaling ?? 1.0;

                var padding = SettingsRoot.Current.Editor.StartupPadding;

                // measure the page to see if any of the tool bars wrap. the window has not been
                // shown yet so this is best-effort; fall back to the default 30px bar sizes.
                var logicalImageSize = new Size(origRect.Width / scaling, origRect.Height / scaling);
                var requiredSize = new Size(logicalImageSize.Width + 30 + padding, logicalImageSize.Height + 30 + padding);

                double toolBarWidth = 30, propBarHeight = 30;
                try {
                    wnd.rootGrid.Measure(requiredSize);
                    if (wnd.ToolBar.DesiredSize.Width > 0)
                        toolBarWidth = wnd.ToolBar.DesiredSize.Width;
                    if (wnd.PropertiesBar.DesiredSize.Height > 0)
                        propBarHeight = wnd.PropertiesBar.DesiredSize.Height;
                } catch {; }

                int ToScreenWH(double logical) => (int) Math.Round(logical * scaling);

                var rect = new PixelRect(
                    origPx.X - ToScreenWH(toolBarWidth) - padding,
                    origPx.Y - ToScreenWH(propBarHeight) - padding,
                    origPx.Width + ToScreenWH(toolBarWidth) + padding * 2,
                    origPx.Height + ToScreenWH(propBarHeight) + padding * 2);

                // we shuffle the rect around each edge if it is off screen to try and
                // achieve a window location that can show with 100% zoom.
                if (rect.X < workArea.X) rect = rect.Translate(new PixelVector(workArea.X - rect.X, 0));
                if (rect.Y < workArea.Y) rect = rect.Translate(new PixelVector(0, workArea.Y - rect.Y));
                if (rect.Right > workArea.Right) rect = rect.Translate(new PixelVector(workArea.Right - rect.Right, 0));
                if (rect.Bottom > workArea.Bottom) rect = rect.Translate(new PixelVector(0, workArea.Bottom - rect.Bottom));

                // finally intersect with screen to crop if the image really can't fit.
                rect = rect.Intersect(workArea);

                wnd.WindowStartupLocation = WindowStartupLocation.Manual;
                wnd.SetWindowRect(rect);
                wnd.Show();
                wnd.Activate();
            } else {
                // it is a new or empty session with no specific area to restore to.
                wnd.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                wnd.Show();
                wnd.Activate();
            }
        }

        public static void ShowAllPreviouslyActiveSessions()
        {
            var sessions = SessionManager.Current.Sessions
                                         .Where(s => s.OpenEditor != null).ToArray();

            foreach (var g in sessions) {
                ShowSession(g);
            }
        }

        private static IEnumerable<EditorWindow> GetOpenEditors()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.Windows.OfType<EditorWindow>();
            return Enumerable.Empty<EditorWindow>();
        }

        private void SetWindowRect(PixelRect rect)
        {
            // all placement math is physical px; divide by the target screen scaling only when
            // setting the logical Width/Height (decision table #55).
            var screen = Screens.ScreenFromBounds(rect) ?? Screens.Primary;
            var scaling = screen?.Scaling ?? 1.0;
            Position = rect.Position;
            Width = rect.Width / scaling;
            Height = rect.Height / scaling;
        }

        // ====================================================================
        // Keyboard (§5.4)
        // ====================================================================

        private void AddCommandKeyBinding(RelayCommand command)
        {
            var kb = command.CreateKeyBinding();
            if (kb == null)
                return; // bare gesture — routed by the tunnel KeyDown handler

            KeyBindings.Add(kb);

            // macOS: every Ctrl gesture is also registered with Meta (§2.4)
            if (OperatingSystem.IsMacOS() && (command.Gesture.Modifiers & KeyModifiers.Control) != 0) {
                var metaMods = (command.Gesture.Modifiers & ~KeyModifiers.Control) | KeyModifiers.Meta;
                KeyBindings.Add(new KeyBinding { Command = command, Gesture = new KeyGesture(command.Gesture.Key, metaMods) });
            }
        }

        private void AddKeyBinding(System.Windows.Input.ICommand command, Key key, object parameter = null)
        {
            KeyBindings.Add(MakeKeyBinding(command, new KeyGesture(key, KeyModifiers.Control), parameter));

            if (OperatingSystem.IsMacOS())
                KeyBindings.Add(MakeKeyBinding(command, new KeyGesture(key, KeyModifiers.Meta), parameter));
        }

        private static KeyBinding MakeKeyBinding(System.Windows.Input.ICommand command, KeyGesture gesture, object parameter)
        {
            var kb = new KeyBinding { Command = command, Gesture = gesture };
            if (parameter != null)
                kb.CommandParameter = parameter;
            return kb;
        }

        private static void ExecuteCommand(RelayCommand command)
        {
            var icmd = (System.Windows.Input.ICommand) command;
            if (icmd.CanExecute(null))
                icmd.Execute(null);
        }

        private static bool IsPanKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.Space;

        private void OnTunnelKeyDown(object sender, KeyEventArgs e)
        {
            // pressed-set repeat tracker (decision table #37)
            bool isRepeat = !_pressedKeys.Add(e.Key);

            if (e.Source is TextBox)
                return;

            // shift/space-pan: save the current tool and enter pan mode while the key is held
            // (skipped while a tool drag is active, §5.4)
            if (IsPanKey(e.Key) && _panPreviousTool == null && !drawingCanvas.IsToolDragActive) {
                _panPreviousTool = drawingCanvas.Tool;
                drawingCanvas.Tool = ToolType.None;
            }

            // space has no other editor function; swallow it so a focused button isn't activated
            if (e.Key == Key.Space) {
                e.Handled = true;
                return;
            }

            // arrow nudge — the only bare-key path that allows Ctrl (§2.4)
            (int x, int y) = e.Key switch {
                Key.Left => (-1, 0),
                Key.Up => (0, -1),
                Key.Right => (1, 0),
                Key.Down => (0, 1),
                _ => (0, 0),
            };

            if (x != 0 || y != 0) {
                e.Handled = true;

                var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (OperatingSystem.IsMacOS())
                    ctrl |= (e.KeyModifiers & KeyModifiers.Meta) != 0;

                if (!ctrl || !isRepeat) _nudgeRepeatCount = 0;

                if (ctrl) {
                    if (isRepeat) _nudgeRepeatCount++;
                    var distance = Math.Min(Math.Max(10, _nudgeRepeatCount * 2), 40);
                    x *= distance;
                    y *= distance;
                }

                drawingCanvas.Nudge(x, y);
                return;
            }

            if (e.KeyModifiers != KeyModifiers.None)
                return;

            switch (e.Key) {
            case Key.Escape:
                drawingCanvas.CancelCurrentOperation();
                e.Handled = true;
                return;
            case Key.Delete:
                ExecuteCommand(drawingCanvas.CommandDelete);
                e.Handled = true;
                return;
            case Key.Home:
                ExecuteCommand(drawingCanvas.CommandMoveToFront);
                e.Handled = true;
                return;
            case Key.End:
                ExecuteCommand(drawingCanvas.CommandMoveToBack);
                e.Handled = true;
                return;
            }

            // bare tool letters (replaces the WPF BareKeyBindings, decision table #36)
            ToolType? tool = e.Key switch {
                Key.D => ToolType.None,
                Key.S => ToolType.Pointer,
                Key.R => ToolType.Rectangle,
                Key.F => ToolType.FilledRectangle,
                Key.E => ToolType.Ellipse,
                Key.L => ToolType.Line,
                Key.A => ToolType.Arrow,
                Key.P => ToolType.PolyLine,
                Key.T => ToolType.Text,
                Key.N => ToolType.Count,
                Key.O => ToolType.Pixelate,
                _ => null,
            };

            if (tool != null) {
                e.Handled = true;
                SelectToolExecuted(tool.Value.ToString());
            }
        }

        private void OnTunnelKeyUp(object sender, KeyEventArgs e)
        {
            _pressedKeys.Remove(e.Key);

            // restore the saved tool once no pan key (shift/space) remains held
            if (IsPanKey(e.Key) && _panPreviousTool != null && !_pressedKeys.Any(IsPanKey)) {
                drawingCanvas.Tool = _panPreviousTool.Value;
                _panPreviousTool = null;
            }
        }

        private void SelectToolExecuted(object parameter)
        {
            if (drawingCanvas.IsToolDragActive)
                return;

            var tool = (ToolType) Enum.Parse(typeof(ToolType), (string) parameter);
            drawingCanvas.Tool = tool;
        }

        // ====================================================================
        // Opt-in features: generated tool strip, customize popup, master toggles
        // ====================================================================

        private sealed class ToolRegistryEntry
        {
            public ToolType Tool;
            public string DisplayName;
            public string IconKey;
            public string Tooltip;
            public double? Padding;
        }

        // Rows mirror the original static XAML 1:1 (icons, tooltips and the Count/Text
        // Padding=8 overrides).
        private static readonly ToolRegistryEntry[] ToolRegistry =
        {
            new ToolRegistryEntry { Tool = ToolType.None, DisplayName = "Pan", IconKey = "IconToolNone", Tooltip = "Pan Tool (D)\nCan also hold SHIFT or SPACE to enter Pan Mode." },
            new ToolRegistryEntry { Tool = ToolType.Pointer, DisplayName = "Selection", IconKey = "IconToolPointer", Tooltip = "Selection Tool (S)" },
            new ToolRegistryEntry { Tool = ToolType.Rectangle, DisplayName = "Rectangle", IconKey = "IconToolRectangle", Tooltip = "Rectangle (R)" },
            new ToolRegistryEntry { Tool = ToolType.FilledRectangle, DisplayName = "Filled Rectangle", IconKey = "IconToolFilledRectangle", Tooltip = "Filled Rectangle (F)" },
            new ToolRegistryEntry { Tool = ToolType.Ellipse, DisplayName = "Ellipse", IconKey = "IconToolEllipse", Tooltip = "Ellipse (E)" },
            new ToolRegistryEntry { Tool = ToolType.Line, DisplayName = "Line", IconKey = "IconToolLine", Tooltip = "Line (L)" },
            new ToolRegistryEntry { Tool = ToolType.Arrow, DisplayName = "Arrow", IconKey = "IconToolArrow", Tooltip = "Arrow (A)" },
            new ToolRegistryEntry { Tool = ToolType.PolyLine, DisplayName = "Pencil", IconKey = "IconToolPolyLine", Tooltip = "Pencil (P)" },
            new ToolRegistryEntry { Tool = ToolType.Count, DisplayName = "Step Count", IconKey = "IconToolNumericCount", Tooltip = "Numerical Step Count (N)", Padding = 8 },
            new ToolRegistryEntry { Tool = ToolType.Text, DisplayName = "Text", IconKey = "IconToolText", Tooltip = "Text (T)", Padding = 8 },
            new ToolRegistryEntry { Tool = ToolType.Pixelate, DisplayName = "Obscure", IconKey = "IconToolPixelate", Tooltip = "Obscure (O)" },
        };

        private readonly List<Control> _generatedToolControls = new List<Control>();

        private static ToolRegistryEntry GetToolEntry(ToolType tool) => ToolRegistry.FirstOrDefault(e => e.Tool == tool);

        /// <summary>Toggles the right-hand layers sidebar. Backed by settings.</summary>
        public bool SidebarVisible
        {
            get => _settings.Editor.SidebarVisible;
            set
            {
                if (_settings.Editor.SidebarVisible == value)
                    return;

                _settings.Editor.SidebarVisible = value;
                // shared setting: mirror the sidebar visibility (and attach/detach) across all editors
                foreach (var wnd in GetOpenEditors())
                    wnd.ApplySidebarVisible(value);
                TrySaveSettings();
            }
        }

        /// <summary>Applies the current sidebar-visible flag to this window. Attaches the layers panel
        /// when shown and detaches it when hidden so a hidden panel does no per-edit rebuild work.</summary>
        private void ApplySidebarVisible(bool value)
        {
            sidebarBorder.IsVisible = value;
            sidebarSplitter.IsVisible = value;

            // a pixel-width column reserves its space even when its content is collapsed, so a hidden
            // sidebar would leave an empty gap. Collapse the column when hidden (both Width AND
            // MinWidth — the MinWidth that bounds the drag would otherwise pin the column open) and
            // restore the persisted, clamped width when shown.
            if (value) {
                SidebarColumn.MinWidth = SidebarMinWidth;
                SidebarColumn.Width = new GridLength(Math.Clamp(_settings.Editor.SidebarWidth, SidebarMinWidth, SidebarMaxWidth), GridUnitType.Pixel);
                layersPanel.Attach(drawingCanvas);
            } else {
                SidebarColumn.MinWidth = 0;
                SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
                layersPanel.Detach();
            }
        }

        private void sidebarSplitter_DragCompleted(object sender, VectorEventArgs e)
        {
            UpdateSidebarWidthSetting();
            TrySaveSettings();
        }

        /// <summary>Captures the current sidebar column width (clamped) into settings. No-op while the
        /// sidebar is hidden so a collapsed 0-width column can't overwrite the remembered width.</summary>
        private void UpdateSidebarWidthSetting()
        {
            if (!_settings.Editor.SidebarVisible)
                return;

            // the border stretches to fill the sidebar column, so its arranged width is the column's
            // actual width; clamp to guard a value the drag bounds should already have enforced.
            _settings.Editor.SidebarWidth = Math.Clamp(sidebarBorder.Bounds.Width, SidebarMinWidth, SidebarMaxWidth);
        }

        /// <summary>Rebuilds the generated portion of the vertical tool strip (visible tools in the
        /// resolved order). The fixed Undo/Redo/customize buttons stay at the end; generated
        /// controls are always inserted at the front.</summary>
        private void RebuildToolStrip()
        {
            var order = ToolbarConfig.ResolveToolbarOrder(_settings.Editor);
            var hidden = ToolbarConfig.ResolveHiddenTools(_settings.Editor);

            bool IsToolVisible(ToolType t) => order.Contains(t) && !hidden.Contains(t);

            // if the active tool's button is about to disappear, fall back to the pointer first
            var active = drawingCanvas.Tool;
            if (!IsToolVisible(active))
                drawingCanvas.Tool = ToolType.Pointer;

            // during a held-key pan the active tool is None but the tool to restore on key-up is
            // saved in _panPreviousTool; if its button is disappearing (hidden via the customize
            // popup), drop it to Pointer so key-up can't reinstate a now-hidden tool
            if (_panPreviousTool != null && !IsToolVisible(_panPreviousTool.Value))
                _panPreviousTool = ToolType.Pointer;

            foreach (var control in _generatedToolControls)
                ToolBar.Children.Remove(control);
            _generatedToolControls.Clear();

            var generated = new List<Control>();

            foreach (var tool in order)
            {
                if (hidden.Contains(tool))
                    continue;

                var entry = GetToolEntry(tool);
                if (entry != null)
                    generated.Add(CreateToolButton(entry));
            }

            for (int i = 0; i < generated.Count; i++)
                ToolBar.Children.Insert(i, generated[i]);
            _generatedToolControls.AddRange(generated);
        }

        private ToolButton CreateToolButton(ToolRegistryEntry entry)
        {
            var name = entry.Tool.ToString();
            var button = new ToolButton
            {
                Command = SelectToolCommand,
                CommandParameter = name,
                IconPath = FindIconGeometry(entry.IconKey),
            };

            if (entry.Padding.HasValue)
                button.Padding = new Thickness(entry.Padding.Value);

            ToolTip.SetTip(button, entry.Tooltip);

            // mirror the original XAML IsChecked pattern: OneWay from the canvas Tool through the
            // ToolTypeConverter, checked iff the active tool equals this button's tool.
            button.Bind(ToggleButton.IsCheckedProperty, new Binding
            {
                Source = drawingCanvas,
                Path = "Tool",
                Mode = BindingMode.OneWay,
                Converter = new ToolTypeConverter(),
                ConverterParameter = name,
            });

            return button;
        }

        private Geometry FindIconGeometry(string key)
        {
            return this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Geometry : null;
        }

        /// <summary>Regenerates the customize-popup rows (one per vector tool in resolved order) in place.</summary>
        private void RebuildCustomizePopup()
        {
            customizeRows.Children.Clear();

            var order = ToolbarConfig.ResolveToolbarOrder(_settings.Editor);
            var hidden = ToolbarConfig.ResolveHiddenTools(_settings.Editor);

            for (int i = 0; i < order.Count; i++)
            {
                var tool = order[i];
                var entry = GetToolEntry(tool);
                if (entry == null)
                    continue;

                var row = new Grid
                {
                    Height = 28,
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                };

                var check = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    IsChecked = !hidden.Contains(tool),
                    IsEnabled = tool != ToolType.Pointer, // the pointer can never be hidden
                };
                Grid.SetColumn(check, 0);
                var toolForCheck = tool;
                check.IsCheckedChanged += (_, _) => SetToolHidden(toolForCheck, check.IsChecked != true);
                row.Children.Add(check);

                var label = new TextBlock
                {
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    Text = entry.DisplayName,
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                int index = i;

                var up = CreateReorderButton("IconChevronUp", i > 0);
                Grid.SetColumn(up, 2);
                up.Click += (_, _) => MoveTool(index, index - 1);
                row.Children.Add(up);

                var down = CreateReorderButton("IconChevronDown", i < order.Count - 1);
                Grid.SetColumn(down, 3);
                down.Click += (_, _) => MoveTool(index, index + 1);
                row.Children.Add(down);

                customizeRows.Children.Add(row);
            }
        }

        private ToolButton CreateReorderButton(string iconKey, bool enabled)
        {
            return new ToolButton
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(4),
                IconPath = FindIconGeometry(iconKey),
                IsEnabled = enabled,
            };
        }

        private void SetToolHidden(ToolType tool, bool hide)
        {
            if (tool == ToolType.Pointer)
                return; // guarded — the pointer is always visible

            var hidden = ToolbarConfig.ResolveHiddenTools(_settings.Editor);
            if (hide)
                hidden.Add(tool);
            else
                hidden.Remove(tool);

            _settings.Editor.HiddenTools = hidden.Select(t => t.ToString()).ToList();
            RebuildToolStrip();
            TrySaveSettings();
            RebuildCustomizePopup();
        }

        private void MoveTool(int from, int to)
        {
            var order = ToolbarConfig.ResolveToolbarOrder(_settings.Editor).ToList();
            if (from < 0 || to < 0 || from >= order.Count || to >= order.Count)
                return;

            (order[to], order[from]) = (order[from], order[to]);
            _settings.Editor.ToolbarOrder = order.Select(t => t.ToString()).ToList();
            RebuildToolStrip();
            TrySaveSettings();
            RebuildCustomizePopup();
        }

        private void customize_Click(object sender, RoutedEventArgs e)
        {
            RebuildCustomizePopup();
            customizePopup.PlacementTarget = btnCustomize;
            customizePopup.IsOpen = true;
        }

        private void customizeResetOrder_Click(object sender, RoutedEventArgs e)
        {
            _settings.Editor.ToolbarOrder = null;
            _settings.Editor.HiddenTools = null;
            RebuildToolStrip();
            TrySaveSettings();
            RebuildCustomizePopup();
        }

        private async void customizeResetSettings_Click(object sender, RoutedEventArgs e)
        {
            // close the light-dismiss popup before showing the modal prompt
            customizePopup.IsOpen = false;

            // same wording as the settings window's "Reset all tool defaults…" button
            if (!await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning,
                    "Reset every tool's saved color, line width and font back to the defaults? This cannot be undone."))
                return;

            _settings.Editor.Tools = new Dictionary<ToolType, SavedToolSettings>();
            TrySaveSettings();
            // the property bar is bound to the old SavedToolSettings instances; rebind it to the
            // (lazily re-created) defaults in the new dictionary
            drawingCanvas.ResyncToolSettings();
        }

        private void TrySaveSettings()
        {
            // the editor has no ambient auto-save; persist customization/toggle changes explicitly
            try { SettingsService.Save(_settings); } catch {; }
        }

        // ====================================================================
        // Session lifecycle (§5.7)
        // ====================================================================

        private bool LoadSessionState()
        {
            if (File.Exists(_graphicsPath)) {
                try {
                    var state = (JsonObject) JsonNode.Parse(File.ReadAllText(_graphicsPath));
                    drawingCanvas.RestoreState(state);
                    RestoreHistoryFile(state);
                    return true;
                } catch {; }
            }

            // if there is a desktop image, and we failed to load an existing set of graphics
            if (File.Exists(_session.DesktopImgPath)) {
                var sel = _session.CroppedRect ?? ScreenRect.Empty;
                var crop = new PixelRect(sel.X, sel.Y, sel.Width, sel.Height);

                var scursor = _session.CursorPosition;
                var cursor = scursor == null ? default(PixelRect) : new PixelRect(scursor.X, scursor.Y, scursor.Width, scursor.Height);

                var graphic = new GraphicImage(
                    _session.DesktopImgPath,
                    new Rect(0, 0, crop.Width, crop.Height),
                    crop,
                    cursorFilePath: _session.CursorImgPath,
                    cursorPosition: cursor,
                    cursorVisible: _settings.Capture.ScreenshotWithCursor);

                // add image
                drawingCanvas.AddGraphic(graphic);
            }

            return false;
        }

        private void RestoreHistoryFile(JsonObject state)
        {
            // best-effort: a missing/corrupt/out-of-sync history.json (graphics.json is the
            // authority) opens the session with empty undo history exactly as before the file
            // existed; the next commit overwrites it with a fresh chain
            try {
                if (File.Exists(_historyPath))
                    drawingCanvas.TryRestoreHistory((JsonObject) JsonNode.Parse(File.ReadAllText(_historyPath)), state);
            } catch {; }
        }

        private static byte[] SerializeToBytes(JsonObject json)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
                json.WriteTo(writer);
            return ms.ToArray();
        }

        private void drawingCanvas_StateUpdated(object sender, StateChangedEventArgs e)
        {
            if (_session == null)
                return;

            if (e.State == null)
                return; // never enqueue empty bytes — a truncated graphics.json silently loses the whole session

            // serialize in memory on the UI thread (cheap), then hand the bytes to a latest-wins
            // background writer — undo/redo and merged drag steps fire this on every step, and a
            // synchronous File.Create here stalls the canvas for the duration of the disk write.
            Interlocked.Exchange(ref _pendingGraphicsJson, SerializeToBytes(e.State));
            if (e.History != null)
                Interlocked.Exchange(ref _pendingHistoryJson, SerializeToBytes(e.History));
            lock (_graphicsWriteLock)
                _graphicsWriteTask = _graphicsWriteTask.ContinueWith(_ => WritePendingGraphicsJson(), TaskScheduler.Default);
        }

        private void WritePendingGraphicsJson()
        {
            var bytes = Interlocked.Exchange(ref _pendingGraphicsJson, null);
            if (bytes != null) {
                try {
                    File.WriteAllBytes(_graphicsPath, bytes);
                } catch (Exception ex) {
                    Debug.WriteLine($"failed to persist graphics.json: {ex.Message}");
                }
            }

            // history.json second: after a crash between the two writes, graphics.json (the
            // authority) is the newer file and the stale history fails its load-time replay
            // check, falling back cleanly to empty history
            var history = Interlocked.Exchange(ref _pendingHistoryJson, null);
            if (history != null) {
                try {
                    File.WriteAllBytes(_historyPath, history);
                } catch (Exception ex) {
                    Debug.WriteLine($"failed to persist history.json: {ex.Message}");
                }
            }
        }

        private void UpdatePreview(Bitmap bitmap)
        {
            if (bitmap == null || drawingCanvas.GraphicsList.Count == 0)
                return;

            // save new preview image to file
            var newpreview = SaveImageToSessionDir(bitmap);
            var oldpreview = _session.PreviewImgPath;
            _session.PreviewImgPath = newpreview;

            try {
                // it could be locked by something else
                if (File.Exists(oldpreview))
                    File.Delete(oldpreview);
            } catch {; }
        }

        private string SaveImageToSessionDir(Bitmap src)
        {
            var path = Path.Combine(Path.GetDirectoryName(_session.FilePath), Guid.NewGuid().ToString() + ".png");
            src.Save(path);
            return path;
        }

        private string CopyFileToSessionDir(string src)
        {
            var ext = Path.GetExtension(src);
            var path = Path.Combine(Path.GetDirectoryName(_session.FilePath), Guid.NewGuid().ToString() + ext);
            File.Copy(src, path, true);
            return path;
        }

        // ====================================================================
        // Save / copy / cut / paste / upload
        // ====================================================================

        private bool VerifyArtworkExists()
        {
            var b = drawingCanvas.GraphicsList.ContentBounds;
            if (b.Height < 1 || b.Width < 1) {
                NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error,
                                           "This operation could not be completed because there are no objects on the canvas.", "Canvas Empty");
                return false;
            }

            return true;
        }

        private async void SaveCommandExecuted(object parameter)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = drawingCanvas.DrawGraphicsToBitmap();
            if (bitmap == null)
                return;

            UpdatePreview(bitmap);

            var savedPath = await NiceDialog.ShowSaveImageDialog(this, bitmap, _settings.General.LastSavePath, _settings.Capture.FilenamePattern);
            if (savedPath != null) {
                _settings.General.LastSavePath = Path.GetDirectoryName(savedPath);
                SettingsService.Save(_settings); // settings no longer auto-save on PropertyChanged
                if (_settings.Capture.OpenSavedInExplorer)
                    RevealFileOrFolder(savedPath);
            }
        }

        private async void CopyCommandExecuted(object parameter)
        {
            await CopyCommandImpl();
        }

        private async Task CopyCommandImpl()
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = drawingCanvas.DrawGraphicsToBitmap();
            if (bitmap == null)
                return;

            UpdatePreview(bitmap);

            var graphics = drawingCanvas.GraphicsList.GetGraphicList(drawingCanvas.SelectedCount > 0);
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(graphics);
            await ClipboardImpl.SetClipboardCanvasData(Clipboard, bitmap, bytes);
        }

        private async void CutCommandExecuted(object parameter)
        {
            // Cut = Copy + DeleteAll (entire canvas) — intentional WPF behavior, do not "fix" (§5.4)
            await CopyCommandImpl();
            drawingCanvas.DeleteAll();
        }

        private async void PasteCommandExecuted(object parameter)
        {
            byte[] clipGraphics = null;
            Bitmap clipBitmap = null;

            try {
                (clipBitmap, clipGraphics) = await ClipboardImpl.GetClipboardCanvasData(Clipboard);
            } catch {; }

            if (clipGraphics != null) {
                clipBitmap?.Dispose();
                var sessionDir = Path.GetDirectoryName(_session.FilePath);
                var graphics = GraphicsSerializer.DeserializeFromUtf8Bytes(clipGraphics);

                // copy any pasted bitmaps into this session directory
                foreach (var img in graphics.OfType<GraphicImage>()) {
                    if (!String.IsNullOrEmpty(img.CursorFilePath) &&
                        !img.CursorFilePath.StartsWith(sessionDir, StringComparison.InvariantCultureIgnoreCase)) {
                        img.CursorFilePath = CopyFileToSessionDir(img.CursorFilePath);
                    }

                    if (!String.IsNullOrEmpty(img.BitmapFilePath) &&
                        !img.BitmapFilePath.StartsWith(sessionDir, StringComparison.InvariantCultureIgnoreCase)) {
                        img.BitmapFilePath = CopyFileToSessionDir(img.BitmapFilePath);
                    }
                }

                drawingCanvas.AddGraphics(graphics);
                return;
            }

            if (clipBitmap != null) {
                try {
                    // save pasted image into session folder + add to canvas
                    using var bmp = clipBitmap;
                    var imgPath = Path.Combine(Path.GetDirectoryName(_session.FilePath), Guid.NewGuid().ToString() + ".png");
                    bmp.Save(imgPath);
                    var graphic = new GraphicImage(imgPath, new Size(bmp.PixelSize.Width, bmp.PixelSize.Height));
                    drawingCanvas.AddGraphic(graphic);
                    return;
                } catch {; }
            }

            await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "The clipboard does not contain an image.", "Failed to paste");
        }

        private void UploadCommandExecuted(object parameter)
        {
            UploadCommandImpl();
        }

        private async void UploadCommandImpl(IUploadProvider provider = null)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = drawingCanvas.DrawGraphicsToBitmap();
            if (bitmap == null)
                return;

            UpdatePreview(bitmap);
            await UploadManager.UploadSession(_session, provider);
        }

        private async void btnUpload_RightMouseDown(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(btnUpload).Properties.IsRightButtonPressed)
                return;

            e.Handled = true;

            if (!VerifyArtworkExists())
                return;

            // same destination-picker shown when no default is set; picking here uploads to that
            // provider once, unless the user ticks "set as default" in the dialog
            var provider = await UploadManager.SelectProvider(SupportedUploadType.Image);
            if (provider != null)
                UploadCommandImpl(provider);
        }

        private static void RevealFileOrFolder(string path)
        {
            try {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", new[] { "-R", path });
                else
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)) { UseShellExecute = true });
            } catch {; }
        }

        // ====================================================================
        // Color popups + font dialog (§5.1 / §5.3)
        // ====================================================================

        private void objectColor_Click(object sender, PointerPressedEventArgs e)
        {
            miniColor.ColorSelectFn = null;
            miniColor.CurrentColor = HslRgbColor.FromColor(drawingCanvas.ObjectColor);
            miniColor.ColorSelectFn = (c) => drawingCanvas.ObjectColor = c;
            miniColorPopup.IsOpen = true;
        }

        private void backgroundColor_Click(object sender, PointerPressedEventArgs e)
        {
            miniColor.ColorSelectFn = null;
            miniColor.CurrentColor = HslRgbColor.FromColor(drawingCanvas.ArtworkBackground);
            miniColor.ColorSelectFn = (c) => drawingCanvas.SetBackgroundColor(c);
            miniColorPopup.IsOpen = true;
        }

        private async void font_Click(object sender, RoutedEventArgs e)
        {
            var result = await NiceDialog.ShowFontDialogAsync(
                this,
                drawingCanvas.TextFontFamilyName,
                drawingCanvas.TextFontSize,
                drawingCanvas.TextFontStyle,
                drawingCanvas.TextFontWeight);

            if (result != null) {
                drawingCanvas.TextFontFamilyName = result.TextFontFamilyName;
                drawingCanvas.TextFontSize = result.TextFontSize;
                drawingCanvas.TextFontStyle = result.TextFontStyle;
                drawingCanvas.TextFontWeight = result.TextFontWeight;
            }
        }
    }
}
