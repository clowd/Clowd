using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clowd.Capture;
using Clowd.Config;
using Clowd.PlatformUtil;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.Util;
using Clowd.Drawing;
using Clowd.Drawing.Graphics;
using PropertyChanged;
using System.Text;

namespace Clowd.UI
{
    [AddINotifyPropertyChangedInterface]
    public partial class ImageEditorPage : UserControl
    {
        public StateCapabilities Capabilities { get; set; } = ToolStateManager.Empty();

        private ToolStateManager _manager = new ToolStateManager();
        private ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private PropertyChangeNotifier toolNotifier;
        private SettingsRoot _settings => SettingsRoot.Current;
        private SessionInfo _session;

        private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";

        public ImageEditorPage(SessionInfo info)
        {
            _session = info;

            InitializeComponent();
            //drawingCanvas.SetResourceReference(DrawingCanvas.HandleColorProperty, "AccentColor");
            drawingCanvas.HandleColor = AppStyles.AccentColor;
            drawingCanvas.ArtworkBackground = new SolidColorBrush(_settings.Editor.CanvasBackground);
            drawingCanvas.MouseUp += drawingCanvas_MouseUp;

            // register tool changed listener
            toolNotifier = new PropertyChangeNotifier(drawingCanvas, DrawingCanvas.ToolProperty);
            toolNotifier.ValueChanged += drawingCanvas_ToolChanged;

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
                    drawingCanvas.GraphicsList.DeserializeObjectsInto(state);
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
            var artBounds = drawingCanvas.GetArtworkBounds(false);
            if (this.ActualHeight > artBounds.Height && this.ActualWidth > artBounds.Width)
            {
                drawingCanvas.ZoomPanActualSize();
            }
            else
            {
                drawingCanvas.ZoomPanFit();
            }

            SyncToolState();
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

        #region Helpers

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
            var b = drawingCanvas.GetArtworkBounds();
            if (b.Height < 10 || b.Width < 10)
            {
                NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, "This operation could not be completed because there are no objects on the canvas.", "Canvas Empty");
                return false;
            }
            return true;
        }

        private DrawingVisual GetRenderedVisual()
        {
            return drawingCanvas.GraphicsList.DrawGraphicsToVisual();
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
            return drawingCanvas.GraphicsList.DrawGraphicsToBitmap();
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

        #endregion

        #region Commands

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

        private void UndoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Undo();
            SyncToolState();
        }

        private void RedoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Redo();
            SyncToolState();
        }

        private async void CopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var bitmap = GetRenderedBitmap();

            var ms = new MemoryStream(drawingCanvas.GraphicsList.SerializeSelected());
            
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

        private void DeleteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Delete();
            SyncToolState();
        }

        private async void UploadCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            var ms = new MemoryStream();
            GetRenderedPng().Save(ms);
            ms.Position = 0;

            await UploadManager.UploadImage(ms, "png", viewName: "Image");
        }

        private void SelectToolCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            if (drawingCanvas.CanUnselectAll)
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
                    drawingCanvas.GraphicsList.DeserializeObjectsInto(ms.ToArray());
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

        private void SelectAllCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.SelectAll();
            SyncToolState();
        }

        private void ZoomActualCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.ZoomPanActualSize();
        }

        private void ZoomFitCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.ZoomPanFit();
        }

        private void MoveToFrontCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.MoveToFront();
        }

        private void MoveToBackCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.MoveToBack();
        }

        #endregion

        #region Events

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
            var state = drawingCanvas.GraphicsList.Serialize();
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
            drawingCanvas.ObjectColor = await NiceDialog.ShowColorDialogAsync(this, drawingCanvas.ObjectColor);
        }

        private async void backgroundColor_Click(object sender, MouseButtonEventArgs e)
        {
            var oldColor = drawingCanvas.ArtworkBackground as SolidColorBrush;
            var newColor = await NiceDialog.ShowColorDialogAsync(this, oldColor?.Color ?? Colors.White);
            drawingCanvas.ArtworkBackground = new SolidColorBrush(newColor);
            _settings.Editor.CanvasBackground = newColor;
        }

        private async void font_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FontDialog dlg = new System.Windows.Forms.FontDialog();
            float wfSize = (float)drawingCanvas.TextFontSize / 96 * 72;
            System.Drawing.FontStyle wfStyle;
            if (drawingCanvas.TextFontStyle == FontStyles.Italic)
                wfStyle = System.Drawing.FontStyle.Italic;
            else
                wfStyle = System.Drawing.FontStyle.Regular;
            if (drawingCanvas.TextFontWeight.ToOpenTypeWeight() > 400)
                wfStyle |= System.Drawing.FontStyle.Bold;
            dlg.Font = new System.Drawing.Font(drawingCanvas.TextFontFamilyName, wfSize, wfStyle, System.Drawing.GraphicsUnit.Point);
            dlg.FontMustExist = true;
            dlg.MaxSize = 64;
            dlg.MinSize = 8;
            dlg.ShowColor = false;
            dlg.ShowEffects = false;
            dlg.ShowHelp = false;
            dlg.AllowVerticalFonts = false;
            dlg.AllowVectorFonts = true;
            dlg.AllowScriptChange = false;
            if (await dlg.ShowAsNiceDialogAsync(this))
            {
                drawingCanvas.TextFontFamilyName = dlg.Font.FontFamily.GetName(0);
                drawingCanvas.TextFontSize = dlg.Font.SizeInPoints / 72 * 96;
                drawingCanvas.TextFontStyle = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Italic) ? FontStyles.Italic : FontStyles.Normal;
                drawingCanvas.TextFontWeight = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Bold) ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private async void ImageStitch_Click(object sender, DPadButtonClickEventArgs e)
        {
            //var wnd = TemplatedWindow.GetWindow(this);
            //var state = wnd.WindowState;
            //wnd.WindowState = WindowState.Minimized;
            //await Task.Delay(400); // wait for window to hide

            //var selection = drawingCanvas.Selection.ToArray();
            //if (selection.Length != 1)
            //    return;

            //var image = selection[0] as GraphicImage;
            //if (image == null)
            //    return;





            //CaptureWindow2.ShowNewCapture(_initialBounds, (img) =>
            //{
            //    var xReferenceCenter = (image.Right + image.Left) / 2;
            //    var yRefereceCenter = (image.Bottom + image.Top) / 2;
            //    var width = ScreenTools.ScreenToWpf(img.PixelWidth);
            //    var height = ScreenTools.ScreenToWpf(img.PixelHeight);
            //    double x, y;

            //    switch (e.Button)
            //    {
            //        case DPadButton.Left:
            //            x = image.Left - width;
            //            y = yRefereceCenter - (height / 2);
            //            break;
            //        case DPadButton.Top:
            //            x = xReferenceCenter - (width / 2);
            //            y = image.Top - height;
            //            break;
            //        case DPadButton.Right:
            //            x = image.Right;
            //            y = yRefereceCenter - (height / 2);
            //            break;
            //        case DPadButton.Bottom:
            //            x = xReferenceCenter - (width / 2);
            //            y = image.Bottom;
            //            break;
            //        default:
            //            throw new ArgumentOutOfRangeException();
            //    }

            //    var graphic = new GraphicImage(drawingCanvas, new Rect(x, y, width, height), img, 0);
            //    drawingCanvas.AddGraphic(graphic);
            //    drawingCanvas.ZoomPanFit();
            //    wnd.WindowState = state;
            //});
        }

        private void toggleTopMost_Click(object sender, RoutedEventArgs e)
        {
            var w = Window.GetWindow(this);
            w.Topmost = toggleTopMost.IsChecked == true;
        }

        #endregion
    }
}
