using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using Clowd.PlatformUtil;
using Clowd.UI.Helpers;
using Clowd.Util;

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

        private string _graphicsPath => Path.Combine(Path.GetDirectoryName(_session.FilePath), "graphics.json");

        // §2.11 glue invariants: custom clipboard format carries UTF-8 JSON bytes of GraphicBase[]
        // (GraphicsSerializer); images travel as "image/png" PNG bytes (decision table #51).
        private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";
        private const string PNG_CLIPBOARD_FORMAT = "image/png";

        public RelayCommand SelectToolCommand { get; }
        public RelayCommand CommandSave { get; }
        public RelayCommand CommandCopy { get; }
        public RelayCommand CommandCut { get; }
        public RelayCommand CommandPaste { get; }
        public RelayCommand CommandUpload { get; }

        public EditorWindow(SessionInfo info)
        {
            _session = info;

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
            Deactivated += (_, _) =>
            {
                _pressedKeys.Clear();
                if (_panPreviousTool != null)
                {
                    // the pan key's KeyUp will never arrive; restore the tool now
                    drawingCanvas.Tool = _panPreviousTool.Value;
                    _panPreviousTool = null;
                }
                UpdateSessionInfo();
            };
            PositionChanged += (_, _) =>
            {
                TrackNormalBounds();
                UpdateSessionInfo();
            };
            SizeChanged += (_, _) => TrackNormalBounds();
            ScalingChanged += (_, _) => drawingCanvas.UpdateScaleTransform(); // decision table #56

            btnUpload.AddHandler(PointerPressedEvent, btnUpload_RightMouseDown, RoutingStrategies.Tunnel);

            miniColor.ParentWindow = this;
            miniColor.Cancelled += (_, _) => miniColorPopup.IsOpen = false;
        }

        private void UpdateSessionInfo()
        {
            if (_session != null)
            {
                _session.OpenEditor = new SessionOpenEditor()
                {
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
                (int)Math.Round(ClientSize.Width * scaling),
                (int)Math.Round(ClientSize.Height * scaling));
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
            // the property bar mutates Editor.Tools (SavedToolSettings) through two-way bindings;
            // persist those edits when the editor closes (explicit-save policy). Saved before the
            // preview render so a rendering failure can't skip the save.
            try { SettingsService.Save(_settings); }
            catch {; }

            UpdatePreview(drawingCanvas.DrawGraphicsToBitmap());
            _session.OpenEditor = null;
            _session = null;
        }

        public static void ShowSession(SessionInfo session)
        {
            // check if there is already a window open with this session in it
            if (session != null)
            {
                var openWnd = GetOpenEditors().FirstOrDefault(f => f._session == session);
                if (openWnd != null)
                {
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

            if (isExistingSession)
            {
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
            }
            else if (canPlaceExactly)
            {
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
                try
                {
                    wnd.rootGrid.Measure(requiredSize);
                    if (wnd.ToolBar.DesiredSize.Width > 0)
                        toolBarWidth = wnd.ToolBar.DesiredSize.Width;
                    if (wnd.PropertiesBar.DesiredSize.Height > 0)
                        propBarHeight = wnd.PropertiesBar.DesiredSize.Height;
                }
                catch {; }

                int ToScreenWH(double logical) => (int)Math.Round(logical * scaling);

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
            }
            else
            {
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

            foreach (var g in sessions)
            {
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
            if (OperatingSystem.IsMacOS() && (command.Gesture.Modifiers & KeyModifiers.Control) != 0)
            {
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
            var icmd = (System.Windows.Input.ICommand)command;
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
            if (IsPanKey(e.Key) && _panPreviousTool == null && !drawingCanvas.IsToolDragActive)
            {
                _panPreviousTool = drawingCanvas.Tool;
                drawingCanvas.Tool = ToolType.None;
            }

            // space has no other editor function; swallow it so a focused button isn't activated
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                return;
            }

            // arrow nudge — the only bare-key path that allows Ctrl (§2.4)
            (int x, int y) = e.Key switch
            {
                Key.Left => (-1, 0),
                Key.Up => (0, -1),
                Key.Right => (1, 0),
                Key.Down => (0, 1),
                _ => (0, 0),
            };

            if (x != 0 || y != 0)
            {
                e.Handled = true;

                var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
                if (OperatingSystem.IsMacOS())
                    ctrl |= (e.KeyModifiers & KeyModifiers.Meta) != 0;

                if (!ctrl || !isRepeat) _nudgeRepeatCount = 0;

                if (ctrl)
                {
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

            switch (e.Key)
            {
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
            ToolType? tool = e.Key switch
            {
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

            if (tool != null)
            {
                e.Handled = true;
                SelectToolExecuted(tool.Value.ToString());
            }
        }

        private void OnTunnelKeyUp(object sender, KeyEventArgs e)
        {
            _pressedKeys.Remove(e.Key);

            // restore the saved tool once no pan key (shift/space) remains held
            if (IsPanKey(e.Key) && _panPreviousTool != null && !_pressedKeys.Any(IsPanKey))
            {
                drawingCanvas.Tool = _panPreviousTool.Value;
                _panPreviousTool = null;
            }
        }

        private void SelectToolExecuted(object parameter)
        {
            if (drawingCanvas.IsToolDragActive)
                return;

            var tool = (ToolType)Enum.Parse(typeof(ToolType), (string)parameter);
            drawingCanvas.Tool = tool;
        }

        // ====================================================================
        // Session lifecycle (§5.7)
        // ====================================================================

        private bool LoadSessionState()
        {
            if (File.Exists(_graphicsPath))
            {
                try
                {
                    var state = (JsonObject)JsonNode.Parse(File.ReadAllText(_graphicsPath));
                    drawingCanvas.RestoreState(state);
                    return true;
                }
                catch {; }
            }

            // if there is a desktop image, and we failed to load an existing set of graphics
            if (File.Exists(_session.DesktopImgPath))
            {
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

        private void drawingCanvas_StateUpdated(object sender, StateChangedEventArgs e)
        {
            if (_session == null)
                return;

            using var fs = File.Create(_graphicsPath);
            if (e.State != null)
            {
                using var writer = new Utf8JsonWriter(fs);
                e.State.WriteTo(writer);
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

            try
            {
                // it could be locked by something else
                if (File.Exists(oldpreview))
                    File.Delete(oldpreview);
            }
            catch {; }
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
            if (b.Height < 1 || b.Width < 1)
            {
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
            if (savedPath != null)
            {
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

            byte[] png;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms);
                png = ms.ToArray();
            }

            var clipboard = Clipboard;
            if (clipboard == null)
                return;

            var data = new DataObject();
            data.Set(PNG_CLIPBOARD_FORMAT, png);
            data.Set(CANVAS_CLIPBOARD_FORMAT, bytes);
            await clipboard.SetDataObjectAsync(data);
        }

        private async void CutCommandExecuted(object parameter)
        {
            // Cut = Copy + DeleteAll (entire canvas) — intentional WPF behavior, do not "fix" (§5.4)
            await CopyCommandImpl();
            drawingCanvas.DeleteAll();
        }

        private async void PasteCommandExecuted(object parameter)
        {
            var clipboard = Clipboard;
            if (clipboard == null)
                return;

            byte[] clipGraphics = null;
            byte[] clipImage = null;

            try
            {
                var formats = await clipboard.GetFormatsAsync() ?? Array.Empty<string>();
                if (formats.Contains(CANVAS_CLIPBOARD_FORMAT))
                    clipGraphics = await clipboard.GetDataAsync(CANVAS_CLIPBOARD_FORMAT) as byte[];
                if (clipGraphics == null && formats.Contains(PNG_CLIPBOARD_FORMAT))
                    clipImage = await clipboard.GetDataAsync(PNG_CLIPBOARD_FORMAT) as byte[];
            }
            catch {; }

            if (clipGraphics != null)
            {
                var sessionDir = Path.GetDirectoryName(_session.FilePath);
                var graphics = GraphicsSerializer.DeserializeFromUtf8Bytes(clipGraphics);

                // copy any pasted bitmaps into this session directory
                foreach (var img in graphics.OfType<GraphicImage>())
                {
                    if (!String.IsNullOrEmpty(img.CursorFilePath) &&
                        !img.CursorFilePath.StartsWith(sessionDir, StringComparison.InvariantCultureIgnoreCase))
                    {
                        img.CursorFilePath = CopyFileToSessionDir(img.CursorFilePath);
                    }

                    if (!String.IsNullOrEmpty(img.BitmapFilePath) &&
                        !img.BitmapFilePath.StartsWith(sessionDir, StringComparison.InvariantCultureIgnoreCase))
                    {
                        img.BitmapFilePath = CopyFileToSessionDir(img.BitmapFilePath);
                    }
                }

                drawingCanvas.AddGraphics(graphics);
                return;
            }

            if (clipImage != null)
            {
                try
                {
                    // save pasted image into session folder + add to canvas
                    using var ms = new MemoryStream(clipImage);
                    using var bmp = new Bitmap(ms);
                    var imgPath = Path.Combine(Path.GetDirectoryName(_session.FilePath), Guid.NewGuid().ToString() + ".png");
                    File.WriteAllBytes(imgPath, clipImage);
                    var graphic = new GraphicImage(imgPath, new Size(bmp.PixelSize.Width, bmp.PixelSize.Height));
                    drawingCanvas.AddGraphic(graphic);
                    return;
                }
                catch {; }
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

        private void btnUpload_RightMouseDown(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(btnUpload).Properties.IsRightButtonPressed)
                return;

            var ctx = new ContextMenu() { Placement = PlacementMode.Pointer };

            ctx.Items.Add(new MenuItem()
            {
                Header = "Upload to:",
                IsEnabled = false,
            });

            var providers = UploadManager.GetAvailableProviders(SupportedUploadType.Image).ToArray();
            if (providers.Length < 2)
            {
                btnUpload.ContextMenu = null;
                return;
            }

            for (var i = 0; i < providers.Length; i++)
            {
                var f = providers[i];

                var mu = new MenuItem();
                mu.Header = f.Name + (i == 0 ? " (default)" : "");
                mu.Click += (s, ev) => UploadCommandImpl(f);

                ctx.Items.Add(mu);

                if (i == 0)
                    ctx.Items.Add(new Separator());
            }

            btnUpload.ContextMenu = ctx;
        }

        private static void RevealFileOrFolder(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", new[] { "-R", path });
                else
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)) { UseShellExecute = true });
            }
            catch {; }
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

            if (result != null)
            {
                drawingCanvas.TextFontFamilyName = result.TextFontFamilyName;
                drawingCanvas.TextFontSize = result.TextFontSize;
                drawingCanvas.TextFontStyle = result.TextFontStyle;
                drawingCanvas.TextFontWeight = result.TextFontWeight;
            }
        }
    }
}
