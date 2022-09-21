using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using System.ComponentModel;
using System.Windows.Controls.Primitives;

namespace Clowd.UI
{
    public partial class EditorWindow : SystemThemedWindow
    {
        private ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private SettingsRoot _settings = SettingsRoot.Current;
        private SessionInfo _session;
        private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";
        private int _nudgeRepeatCount;

        public EditorWindow(SessionInfo info)
        {
            _session = info;

            InitializeComponent();
            drawingCanvas.HandleColor = AppStyles.AccentColor;
            drawingCanvas.StateUpdated += drawingCanvas_StateUpdated;
            drawingCanvas.ArtworkBackground = info.CanvasBackground;

            this.InputBindings.Add(drawingCanvas.CommandSelectAll.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandUnselectAll.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandDelete.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandMoveToFront.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandMoveToBack.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandMoveForward.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandMoveBackward.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandUndo.CreateKeyBinding());
            this.InputBindings.Add(drawingCanvas.CommandRedo.CreateKeyBinding());
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanAuto, Key.D0, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanAuto, Key.NumPad0, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D1, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad1, ModifierKeys.Control));
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D2, ModifierKeys.Control) { CommandParameter = 2d });
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad2, ModifierKeys.Control) { CommandParameter = 2d });
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.D3, ModifierKeys.Control) { CommandParameter = 3d });
            this.InputBindings.Add(new KeyBinding(drawingCanvas.CommandZoomPanActualSize, Key.NumPad3, ModifierKeys.Control) { CommandParameter = 3d });

            this.Loaded += ImageEditorPage2_Loaded;
            this.PreviewKeyDown += ImageEditorPage_PreviewKeyDown;
            this.Closing += EditorWindow_Closing;
            this.Loaded += (_, _) => UpdateSessionInfo();
            this.Deactivated += (_, _) => UpdateSessionInfo();
            this.Activated += (_, _) => UpdateSessionInfo();
        }

        private void UpdateSessionInfo()
        {
            if (_session != null)
            {
                _session.OpenEditor = new SessionOpenEditor()
                {
                    IsTopMost = Topmost,
                    Position = ScreenPosition,
                    VirtualDesktopId = PlatformWindow?.VirtualDesktopId,
                };
            }
        }

        private void EditorWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdatePreview(drawingCanvas.DrawGraphicsToBitmap());
            _session.OpenEditor = null;
            _session = null;
        }

        public static void ShowSession(SessionInfo session)
        {
            App.Analytics.ScreenView("EditorWindow");

            // check if there is already a window open with this session in it
            if (session != null)
            {
                var openWnd = App.Current.Windows.OfType<EditorWindow>().FirstOrDefault(f => f._session == session);
                if (openWnd != null)
                {
                    openWnd.PlatformWindow.Activate();
                    return;
                }
            }

            // it's possible for EnsureHandle to go off and trigger the Activate event which sets
            // OpenEditor to not null, all before we hit the if/else below
            bool isExistingSession = session?.OpenEditor != null;
            bool canPlaceExactly = session?.OriginalBounds?.IsEmpty() == false;

            if (session == null)
                session = SessionManager.Current.CreateNewSession();

            var wnd = new EditorWindow(session);
            wnd.WindowStartupLocation = WindowStartupLocation.Manual;
            wnd.EnsureHandle();

            if (isExistingSession)
            {
                // this session was not closed properly, restore it to its previous location
                wnd.Topmost = session.OpenEditor.IsTopMost;
                wnd.ShowActivated = false;
                try
                {
                    if (session.OpenEditor.VirtualDesktopId != null && SettingsRoot.Current.Editor.RestoreToSameVirtualDesktop)
                        wnd.PlatformWindow.MoveToDesktop(session.OpenEditor.VirtualDesktopId.Value);
                }
                catch {; }

                wnd.ScreenPosition = session.OpenEditor.Position;
                wnd.Show();
            }
            else if (canPlaceExactly)
            {
                // this is a brand new session. we'll show it on top of the captured area.
                var origRect = session.OriginalBounds;
                var screen = Platform.Current.GetScreenFromRect(origRect);
                var workArea = screen.WorkingArea;
                var dpi = screen.ToDpiContext();

                // adjust working area to account for the invisible resizing border around the window
                var resizePadding = (int)(SystemParameters.ResizeFrameVerticalBorderWidth + SystemParameters.FixedFrameVerticalBorderWidth);
                workArea = new ScreenRect(
                    workArea.Left - resizePadding,
                    workArea.Y,
                    workArea.Width + (resizePadding * 2),
                    workArea.Height + resizePadding);

                // calculate needed client rect; add 30 because of default toolbar size.
                var logicalImageSize = dpi.ToWorldSize(origRect.Size);
                var padding = SettingsRoot.Current.Editor.StartupPadding;
                var requiredSize = new Size(logicalImageSize.Width + 30 + padding, logicalImageSize.Height + 30 + padding);

                // measure the page to see if any of the tool bars wrap
                wnd.rootGrid.Measure(requiredSize);
                var toolBarSize = wnd.ToolBar.DesiredSize;
                var propBarSize = wnd.PropertiesBar.DesiredSize;

                var rect = new ScreenRect(
                    origRect.X - dpi.ToScreenWH(toolBarSize.Width) - padding,
                    origRect.Y - dpi.ToScreenWH(propBarSize.Height) - padding,
                    origRect.Width + dpi.ToScreenWH(toolBarSize.Width) + padding * 2,
                    origRect.Height + dpi.ToScreenWH(propBarSize.Height) + padding * 2);

                // this is the 'ideal' rect that places the window precisely on top of the captured area,
                // but part of the window may be outside the monitor
                var idealRect = wnd.PlatformWindow.GetWindowRectFromIdealClientRect(rect);

                // we shuffle the ideal rect around each edge if it is off screen to try and 
                // achieve a window location that can show with 100% zoom.
                if (idealRect.Left < workArea.Left) idealRect = idealRect.Translate(workArea.Left - idealRect.Left, 0);
                if (idealRect.Top < workArea.Top) idealRect = idealRect.Translate(0, workArea.Top - idealRect.Top);
                if (idealRect.Right > workArea.Right) idealRect = idealRect.Translate(workArea.Right - idealRect.Right, 0);
                if (idealRect.Bottom > workArea.Bottom) idealRect = idealRect.Translate(0, workArea.Bottom - idealRect.Bottom);

                // finally intersect with screen to crop if the image really can't fit.
                wnd.PlatformWindow.WindowBounds = idealRect.Intersect(workArea);

                wnd.Show();
                wnd.PlatformWindow.Activate();
            }
            else
            {
                // it is a new or empty session with no specific area to restore to.
                wnd.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                wnd.Show();
                wnd.PlatformWindow.Activate();
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

        private void ImageEditorPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                drawingCanvas.CancelCurrentOperation();

            if (e.OriginalSource is not TextBoxBase)
            {
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

                    var ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                    if (!ctrl || !e.IsRepeat) _nudgeRepeatCount = 0;

                    if (ctrl)
                    {
                        if (e.IsRepeat) _nudgeRepeatCount++;
                        var distance = Math.Min(Math.Max(10, _nudgeRepeatCount * 2), 40);
                        x *= distance;
                        y *= distance;
                    }

                    drawingCanvas.Nudge(x, y);
                }
            }
        }

        private void ImageEditorPage2_Loaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(buttonFocus);

            bool loaded = false;

            if (!String.IsNullOrWhiteSpace(_session.GraphicsStream))
            {
                try
                {
                    var state = Convert.FromBase64String(_session.GraphicsStream);
                    drawingCanvas.DeserializeGraphics(state);
                    drawingCanvas.UnselectAll();
                    loaded = true;
                }
                catch { }
            }

            // if there is a desktop image, and we failed to load an existing set of graphics
            if (!loaded && File.Exists(_session.DesktopImgPath))
            {
                var sel = _session.CroppedRect;
                var crop = new Int32Rect(sel.X, sel.Y, sel.Width, sel.Height);

                var scursor = _session.CursorPosition;
                var cursor = scursor == null ? Int32Rect.Empty : new Int32Rect(scursor.X, scursor.Y, scursor.Width, scursor.Height);

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

            // if window is bigger than image, show at actual size. else, zoom to fit
            drawingCanvas.ZoomPanAuto();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            drawingCanvas.UpdateScaleTransform();
        }

        private bool VerifyArtworkExists()
        {
            var b = drawingCanvas.GraphicsList.ContentBounds;
            if (b.Height < 10 || b.Width < 10)
            {
                NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "This operation could not be completed because there are no objects on the canvas.", "Canvas Empty");
                return false;
            }

            return true;
        }

        private void PrintCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            PrintDialog dlg = new PrintDialog();
            var image = drawingCanvas.DrawGraphicsToVisual();
            if (dlg.ShowDialog().GetValueOrDefault() != true)
            {
                return;
            }

            dlg.PrintVisual(image, "Graphics");
        }

        private void CloseCommand(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();
        }

        private async void SaveCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = drawingCanvas.DrawGraphicsToBitmap();
            UpdatePreview(bitmap);

            var frame = BitmapFrame.Create(bitmap);
            var savedPath = await NiceDialog.ShowSaveImageDialog(null, frame, _settings.General.LastSavePath, _settings.Capture.FilenamePattern);
            if (savedPath != null)
            {
                _settings.General.LastSavePath = Path.GetDirectoryName(savedPath);
                if (_settings.Capture.OpenSavedInExplorer)
                    Platform.Current.RevealFileOrFolder(savedPath);
            }
        }

        private async void CopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = drawingCanvas.DrawGraphicsToBitmap();
            UpdatePreview(bitmap);

            var ms = new MemoryStream(drawingCanvas.SerializeGraphics(true));

            var data = new ClipboardDataObject();
            data.SetImage(bitmap);
            data.SetDataFormat(CANVAS_CLIPBOARD_FORMAT, ms);

            await data.SetClipboardData(this);
        }

        private void CutCommand(object sender, ExecutedRoutedEventArgs e)
        {
            CopyCommand(sender, e);
            drawingCanvas.DeleteAll();
        }

        private async void UploadCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            UpdatePreview(drawingCanvas.DrawGraphicsToBitmap());
            await UploadManager.UploadSession(_session);
        }

        private void SelectToolCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            var tool = (ToolType)Enum.Parse(typeof(ToolType), (string)e.Parameter);
            drawingCanvas.Tool = tool;
        }

        private async void PasteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            var data = await ClipboardDataObject.GetClipboardData(this);
            if (data.ContainsDataFormat(CANVAS_CLIPBOARD_FORMAT))
            {
                var ms = data.GetDataFormat<MemoryStream>(CANVAS_CLIPBOARD_FORMAT);
                if (ms != null)
                {
                    drawingCanvas.DeserializeGraphics(ms.ToArray());
                    return;
                }
            }
            else if (data.ContainsImage())
            {
                var img = data.GetImage();
                if (img != null)
                {
                    // save pasted image into session folder + add to canvas
                    var imgPath = SaveImageToSessionDir(img);
                    var graphic = new GraphicImage(imgPath, new Size(img.PixelWidth, img.PixelHeight));
                    drawingCanvas.AddGraphic(graphic);
                    return;
                }
            }

            await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "The clipboard does not contain an image.", "Failed to paste");
        }

        private void rootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && _shiftPanPreviousTool == null && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _shiftPanPreviousTool = drawingCanvas.Tool;
                drawingCanvas.Tool = ToolType.None;
                // i need to change a random property here so WPF updates. really weird, fix it some day?
                buttonFocus.Opacity = 0.02;
            }
        }

        private void rootGrid_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && _shiftPanPreviousTool != null)
            {
                drawingCanvas.Tool = _shiftPanPreviousTool.Value;
                _shiftPanPreviousTool = null;
                // i need to change a random property here so WPF updates. really weird, fix it some day?
                buttonFocus.Opacity = 0.03;
            }
        }

        private void drawingCanvas_StateUpdated(object sender, StateChangedEventArgs e)
        {
            _session.GraphicsStream = Convert.ToBase64String(e.State);
        }

        private void UpdatePreview(BitmapSource bitmap)
        {
            if (drawingCanvas.GraphicsList.Count == 0)
                return;

            // save new preview image to file
            var newpreview = SaveImageToSessionDir(bitmap);
            var oldpreview = _session.PreviewImgPath;
            _session.PreviewImgPath = newpreview;

            try
            {
                // it could be locked by WPF
                if (File.Exists(oldpreview))
                    File.Delete(oldpreview);
            }
            catch {; }
        }

        private string SaveImageToSessionDir(BitmapSource src)
        {
            var path = Path.Combine(Path.GetDirectoryName(_session.FilePath), Guid.NewGuid().ToString() + ".png");
            src.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        private async void objectColor_Click(object sender, MouseButtonEventArgs e)
        {
            drawingCanvas.ObjectColor = await NiceDialog.ShowColorPromptAsync(this, drawingCanvas.ObjectColor);
        }

        private async void backgroundColor_Click(object sender, MouseButtonEventArgs e)
        {
            var oldColor = drawingCanvas.ArtworkBackground;
            var newColor = await NiceDialog.ShowColorPromptAsync(this, oldColor);
            drawingCanvas.ArtworkBackground = newColor;
            _session.CanvasBackground = newColor;
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
