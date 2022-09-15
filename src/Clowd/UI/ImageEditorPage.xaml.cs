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
    public partial class ImageEditorPage : UserControl, INotifyPropertyChanged
    {
        private ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private SettingsRoot _settings = SettingsRoot.Current;
        private SessionInfo _session;
        private const string CANVAS_CLIPBOARD_FORMAT = "{65475a6c-9dde-41b1-946c-663ceb4d7b15}";

        public event PropertyChangedEventHandler PropertyChanged;

        public ImageEditorPage(SessionInfo info)
        {
            _session = info;

            InitializeComponent();
            drawingCanvas.HandleColor = AppStyles.AccentColor;
            drawingCanvas.ArtworkBackground = _settings.Editor.CanvasBackground;
            drawingCanvas.MouseUp += drawingCanvas_MouseUp;

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
            this.PreviewKeyDown += ImageEditorPage_PreviewKeyDown;
        }

        private void ImageEditorPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                drawingCanvas.CancelCurrentOperation();

            if (e.OriginalSource is not TextBoxBase)
            {
                var distance = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ? 10 : 1;
                switch (e.Key)
                {
                    case Key.Left:
                        drawingCanvas.Nudge(-1 * distance, 0);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        drawingCanvas.Nudge(0, -1 * distance);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        drawingCanvas.Nudge(1 * distance, 0);
                        e.Handled = true;
                        break;
                    case Key.Down:
                        drawingCanvas.Nudge(0, 1 * distance);
                        e.Handled = true;
                        break;
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

                var graphic = new GraphicImage(
                    _session.DesktopImgPath,
                    new Rect(0, 0, crop.Width, crop.Height),
                    crop);

                // add image
                drawingCanvas.AddGraphic(graphic);
            }

            // if window is bigger than image, show at actual size. else, zoom to fit
            drawingCanvas.ZoomPanAuto();

            toggleTopMost.IsChecked = Window.GetWindow(this)?.Topmost;
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

        private DrawingVisual GetRenderedVisual()
        {
            return drawingCanvas.DrawGraphicsToVisual();
        }

        private BitmapSource GetRenderedBitmap()
        {
            return drawingCanvas.DrawGraphicsToBitmap();
        }

        private PngBitmapEncoder GetRenderedPng()
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            return enc;
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

        private void drawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (drawingCanvas.GraphicsList.Count == 0)
                return;

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
            _settings.Editor.CanvasBackground = newColor;
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
