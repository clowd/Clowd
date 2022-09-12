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

namespace Clowd.UI
{
    public partial class ImageEditorPage : UserControl, INotifyPropertyChanged
    {
        public StateCapabilities Capabilities
        {
            get => _capabilities;
            set
            {
                if (_capabilities != value)
                {
                    _capabilities = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Capabilities)));
                }
            }
        }

        private ToolStateManager _manager = new ToolStateManager();
        private ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private PropertyChangeNotifier toolNotifier;
        private SettingsRoot _settings => SettingsRoot.Current;
        private SessionInfo _session;
        private StateCapabilities _capabilities = ToolStateManager.Empty();
        private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";

        public event PropertyChangedEventHandler PropertyChanged;

        public ImageEditorPage(SessionInfo info)
        {
            _session = info;

            InitializeComponent();
            drawingCanvas.HandleColor = AppStyles.AccentColor;
            drawingCanvas.ArtworkBackground = _settings.Editor.CanvasBackground;
            drawingCanvas.MouseUp += drawingCanvas_MouseUp;

            // register tool changed listener
            toolNotifier = new PropertyChangeNotifier(drawingCanvas, DrawingCanvas.ToolProperty);
            toolNotifier.ValueChanged += drawingCanvas_ToolChanged;

            this.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) drawingCanvas.CancelCurrentOperation(); };
            this.InputBindings.Add(drawingCanvas.CommandSelectAll.CreateKeyBinding());
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
            SyncToolState();
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

                var graphic = new GraphicImage(
                    _session.DesktopImgPath,
                    new Rect(0, 0, crop.Width, crop.Height),
                    crop);

                // add image
                drawingCanvas.AddGraphic(graphic);
            }

            // if window is bigger than image, show at actual size. else, zoom to fit
            drawingCanvas.ZoomPanAuto();

            SyncToolState();

            toggleTopMost.IsChecked = Window.GetWindow(this)?.Topmost;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            drawingCanvas.UpdateScaleTransform();
        }

        //public static void ShowFromSession(SessionInfo info)
        //{
        //    var w = new ImageEditorPage();
        //    w._session = info;

        //    var screen = Platform.Current.GetScreenFromRect(info.SelectionRect);
        //    var dpi = screen.ToDpiContext();
        //    var padding = dpi.ToWorldWH(w._settings.Editor.StartupPadding);


        //    // get wpf to do a layout pass, check if it can be fully fit within the screen
        //    var availableSize = dpi.ToWorldSize(screen.WorkingArea.Size);
        //    w.Measure(new Size(availableSize.Width, availableSize.Height));
        //    var requestedSize = w.DesiredSize;

        //    //var t = w.toolBar.DesiredSize;

        //    //w.Show();
        //    //w.GetPlatformWindow().Activate();
        //}

        //protected override async void OnActivated(Window wnd)
        //{
        //if (_initialImage == null)
        //    return;

        //AddImage(_initialImage);

        //var wasSidebarWidth = toolBar.ActualWidth;
        //var wasActionRowHeight = propertiesBar.ActualHeight;

        ////bool fit = DoWindowFit(wnd);
        ////if (propertiesBar.ActualHeight != wasActionRowHeight || toolBar.ActualWidth != wasSidebarWidth)
        ////    fit = DoWindowFit(wnd); // re-fit in case the action row has reflowed

        //// just doing this to force a thread context switch.
        //// by the time we get back on to the UI thread the window will be done resizing.
        //await Task.Delay(10);

        ////if (fit)
        //drawingCanvas.ZoomPanActualSize();
        ////else
        ////    drawingCanvas.ZoomPanFit();

        //SyncToolState();
        //}


        //private void AddImage(BitmapSource img)
        //{
        //    if (img == null)
        //        return;

        //    //var width = ScreenTools.ScreenToWpf(img.PixelWidth);
        //    //var height = ScreenTools.ScreenToWpf(img.PixelHeight);
        //    //var graphic = new GraphicImage(drawingCanvas, new Rect(
        //    //    ScreenTools.WpfSnapToPixels(drawingCanvas.WorldOffset.X - (width / 2)),
        //    //    ScreenTools.WpfSnapToPixels(drawingCanvas.WorldOffset.Y - (height / 2)),
        //    //    width, height), img);

        //    var width = img.PixelWidth;
        //    var height = img.PixelHeight;
        //    var graphic = new GraphicImage(drawingCanvas, new Rect(
        //       drawingCanvas.WorldOffset.X - (width / 2),
        //      drawingCanvas.WorldOffset.Y - (height / 2),
        //        width, height), img);

        //    drawingCanvas.AddGraphic(graphic);
        //}

        //private bool DoWindowFit(Window wnd)
        //{
        //    //if (_initialImage == null)
        //    //    return true;

        //    //var imageSize = new Size(ScreenTools.ScreenToWpf(_initialImage.PixelWidth), ScreenTools.ScreenToWpf(_initialImage.Height));
        //    //var capturePadding = App.Current.Settings.EditorSettings.CapturePadding;
        //    //var contentOffsetX = capturePadding + Math.Max(30, toolBar.ActualWidth);
        //    //var contentOffsetY = capturePadding + Math.Max(30, propertiesBar.ActualHeight);

        //    //var contentSize = new Size(imageSize.Width + contentOffsetX + capturePadding, imageSize.Height + contentOffsetY + capturePadding);

        //    ////if (_initialBounds != null)
        //    ////{
        //    ////    return TemplatedWindow.SizeToContent(wnd, contentSize, _initialBounds.Value.Left - contentOffsetX, _initialBounds.Value.Top - contentOffsetY);
        //    ////}
        //    ////else
        //    ////{
        //    //return TemplatedWindow.SizeToContent(wnd, contentSize);
        //    //}
        //}

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

        private DrawingVisual GetRenderedVisual()
        {
            return drawingCanvas.DrawGraphicsToVisual();
        }

        //private DrawingVisual GetRenderedVisual()
        //{
        //    var bounds = drawingCanvas.GetArtworkBounds();
        //    var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

        //    DrawingVisual vs = new DrawingVisual();
        //    using (DrawingContext dc = vs.RenderOpen())
        //    {
        //        dc.PushTransform(transform);
        //        dc.DrawRectangle(new SolidColorBrush(_settings.Editor.CanvasBackground), null, bounds);
        //        dc.Pop();
        //        drawingCanvas.Draw(dc, null, transform, false);
        //    }
        //    return vs;
        //}

        private BitmapSource GetRenderedBitmap()
        {
            return drawingCanvas.DrawGraphicsToBitmap();
        }

        //private RenderTargetBitmap GetRenderedBitmap()
        //{
        //    var bounds = drawingCanvas.GetArtworkBounds();
        //    if (bounds.IsEmpty)
        //        return null;

        //    var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

        //    RenderTargetBitmap bmp = new RenderTargetBitmap(
        //        (int)bounds.Width,
        //        (int)bounds.Height,
        //        96,
        //        96,
        //        PixelFormats.Pbgra32);
        //    DrawingVisual background = new DrawingVisual();
        //    using (DrawingContext dc = background.RenderOpen())
        //    {
        //        dc.PushTransform(transform);
        //        dc.DrawRectangle(new SolidColorBrush(_settings.Editor.CanvasBackground), null, bounds);
        //    }
        //    bmp.Render(background);
        //    drawingCanvas.Draw(null, bmp, transform, false);
        //    return bmp;
        //}

        private PngBitmapEncoder GetRenderedPng()
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            return enc;
        }

        private void SyncToolState()
        {
            var selection = drawingCanvas.SelectedItems.ToArray();
            object newStateObj;
            if (selection.Length == 0)
            {
                newStateObj = drawingCanvas.Tool;
            }
            else if (selection.Length == 1)
            {
                newStateObj = selection.First();
            }
            else
            {
                newStateObj = null;
            }

            // if the state object has changed (IsCurrent) then we need to enter a new capability state
            var currentState = Capabilities;
            if (!currentState.IsCurrent(newStateObj))
            {
                var newState = _manager.GetObjectCapabilities(newStateObj);
                currentState.ExitState(this);
                Capabilities = newState;
                newState.EnterState(this, newStateObj);
            }
        }



        private void PrintCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            PrintDialog dlg = new PrintDialog();
            var image = GetRenderedVisual();
            if (dlg.ShowDialog().GetValueOrDefault() != true)
            {
                return;
            }

            dlg.PrintVisual(image, "Graphics");
        }

        private void CloseCommand(object sender, ExecutedRoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }

        private async void SaveCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var filename = await NiceDialog.ShowSelectSaveFileDialog(this, "Save Image", _settings.General.LastSavePath, "screenshot", "png");

            if (String.IsNullOrWhiteSpace(filename))
            {
                return;
            }
            else
            {
                using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
                    GetRenderedPng().Save(fs);

                Platform.Current.RevealFileOrFolder(filename);
                _settings.General.LastSavePath = System.IO.Path.GetDirectoryName(filename);
            }
        }

        private async void CopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = GetRenderedBitmap();

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
            SyncToolState();
        }

        private async void UploadCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            await UploadManager.UploadSession(_session);
        }

        private void SelectToolCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            drawingCanvas.UnselectAll();

            var tool = (ToolType)Enum.Parse(typeof(ToolType), (string)e.Parameter);
            drawingCanvas.Tool = tool;

            SyncToolState();
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
                    SyncToolState();
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
                    SyncToolState();
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
                //shiftIndicator.Background = (Brush)App.Current.Resources["AccentColorBrush"];
                //shiftIndicator.Opacity = 1;
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
                //shiftIndicator.Background = new SolidColorBrush(Color.FromRgb(112, 112, 112));
                //shiftIndicator.Opacity = 0.8;
            }
        }

        private void drawingCanvas_ToolChanged(object sender, EventArgs e)
        {
            SyncToolState();
        }

        private void drawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (drawingCanvas.GraphicsList.Count == 0)
                return;

            SyncToolState();

            // persist editor state to session file
            var state = drawingCanvas.SerializeGraphics(false);
            var newstream = Convert.ToBase64String(state);
            if (newstream != _session.GraphicsStream)
            {
                _session.GraphicsStream = newstream;

                // save new preview image to file
                var preview = GetRenderedBitmap();
                if (preview != null)
                {
                    var newpreview = SaveImageToSessionDir(preview);
                    var oldpreview = _session.PreviewImgPath;
                    _session.PreviewImgPath = newpreview;
                    if (File.Exists(oldpreview))
                        File.Delete(oldpreview);
                }
            }
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

        private void toggleTopMost_Click(object sender, RoutedEventArgs e)
        {
            var w = Window.GetWindow(this);
            w.Topmost = toggleTopMost.IsChecked == true;
        }
    }
}
