using Clowd.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using CS.Wpf;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class ImageEditorPage : UserControl
    {
        public bool ShowActionLabels { get; set; } = true;

        private bool _autoPanning = false;
        private bool _shiftPressed = false;
        private string _imagePath;

        public ImageEditorPage(string initImagePath)
        {
            InitializeComponent();
            drawingCanvas.SetResourceReference(DrawToolsLib.DrawingCanvas.HandleColorProperty, "AccentColor");
            drawingCanvas.ObjectColor = Colors.Red;
            drawingCanvas.LineWidth = 2;
            this.Loaded += ImageEditorPage_Loaded;
            _imagePath = initImagePath;
            //http://www.1001fonts.com/honey-script-font.html

            if (!String.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath))
            {
                double width, height;
                using (var stream = new FileStream(_imagePath, FileMode.Open, FileAccess.Read))
                {
                    var bitmapFrame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = DpiScale.DownScaleX(bitmapFrame.PixelWidth);
                    height = DpiScale.DownScaleY(bitmapFrame.PixelHeight);
                }
                objectCanvas.Width = width * 1.5;
                objectCanvas.Height = height * 1.5;
                drawingCanvas.AddImageGraphic(_imagePath, new Rect(width / 4, height / 4, width, height));
            }
        }

        private void MoveCommandsToChrome()
        {
            var window = TemplatedWindow.GetWindow(this);
            if (window is MahApps.Metro.Controls.MetroWindow)
            {
                window.Title = "";
                var metro = window as MahApps.Metro.Controls.MetroWindow;
                var left = new MahApps.Metro.Controls.WindowCommands();
                var right = new MahApps.Metro.Controls.WindowCommands();
                rootGrid.Children.Remove(actionBar);
                rootGrid.Children.Remove(toolBar);
                left.Items.Add(actionBar);
                right.Items.Add(toolBar);
                metro.LeftWindowCommands = left;
                metro.RightWindowCommands = right;
                rootGrid.RowDefinitions[0].Height = new GridLength(0);
            }
        }
        private void RefreshArtworkBounds()
        {
            var rect = drawingCanvas.GetArtworkBounds();
            if (rect == Rect.Empty)
                return;
            artworkBounds.Width = rect.Width;
            artworkBounds.Height = rect.Height;
            Canvas.SetLeft(artworkBounds, rect.Left);
            Canvas.SetTop(artworkBounds, rect.Top);
        }
        private bool VerifyArtworkExists()
        {
            var b = drawingCanvas.GetArtworkBounds();
            if (b.Height < 10 || b.Width < 10)
            {
                //TODO: Show an error saying that there is nothing on the canvas.
                return false;
            }
            return true;
        }
        private DrawingVisual GetRenderedVisual()
        {
            var bounds = drawingCanvas.GetArtworkBounds();

            DrawingVisual vs = new DrawingVisual();
            DrawingContext dc = vs.RenderOpen();

            drawingCanvas.RemoveClip();

            var transform = new TranslateTransform(-bounds.Left, -bounds.Top);
            dc.PushClip(new RectangleGeometry(bounds, 0, 0, transform));
            dc.PushTransform(transform);

            dc.DrawRectangle(Brushes.White, null, bounds);
            drawingCanvas.Draw(dc);
            drawingCanvas.RefreshClip();

            dc.Pop();
            dc.Close();

            return vs;
        }
        private RenderTargetBitmap GetRenderedBitmap()
        {
            var drawingVisual = GetRenderedVisual();
            RenderTargetBitmap bmp = new RenderTargetBitmap(
                (int)DpiScale.UpScaleX(drawingVisual.ContentBounds.Width),
                (int)DpiScale.UpScaleY(drawingVisual.ContentBounds.Height),
                DpiScale.DpiX,
                DpiScale.DpiY,
                PixelFormats.Pbgra32);
            bmp.Render(drawingVisual);
            return bmp;
        }
        private PngBitmapEncoder GetRenderedPng()
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            return enc;
        }

        private void ImageEditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            zoomControl.PreviewMouseLeftButtonDown += ZoomControl_PreviewMouseLeftButtonDown;
            zoomControl.PreviewMouseLeftButtonUp += ZoomControl_PreviewMouseLeftButtonUp;
            this.PreviewKeyDown += ZoomControl_PreviewKeyDown;
            this.PreviewKeyUp += ZoomControl_PreviewKeyUp;
            drawingCanvas.MouseMove += DrawingCanvas_MouseMove;

            // you need to focus a button, or some other control that holds keyboard focus.
            // if you don't do this, input bindings / keyboard shortcuts won't work.
            Keyboard.Focus(uploadButton);



            drawingCanvas.RefreshClip();
            ZoomFit_Clicked(null, null);
        }

        private void ZoomControl_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_shiftPressed && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            {
                _shiftPressed = false;
                shiftIndicator.Background = new SolidColorBrush(Color.FromRgb(112, 112, 112));
                shiftIndicator.Opacity = 0.8;
                drawingCanvas.Cursor = Cursors.Arrow;
                drawingCanvas.Tool = DrawToolsLib.ToolType.Pointer;
            }
        }
        private void ZoomControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox)
                return;
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                _shiftPressed = true;
                shiftIndicator.Background = (Brush)App.Current.Resources["AccentColorBrush"];
                shiftIndicator.Opacity = 1;
                drawingCanvas.Cursor = Cursors.SizeAll;
                drawingCanvas.Tool = DrawToolsLib.ToolType.None;
            }
        }
        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!zoomControl.Panning && e.LeftButton == MouseButtonState.Pressed)
            {
                RefreshArtworkBounds();
            }
        }
        private void ZoomControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (zoomControl.Panning)
            {
                zoomControl.StopPanning();
                if (_autoPanning)
                    drawingCanvas.Tool = DrawToolsLib.ToolType.Pointer;
                _autoPanning = false;
                e.Handled = true;
            }
            RefreshArtworkBounds();
        }
        private void ZoomControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_shiftPressed || drawingCanvas.Tool == DrawToolsLib.ToolType.None || (
                drawingCanvas.Tool == DrawToolsLib.ToolType.Pointer
                    && drawingCanvas.HitTestGraphics(e.GetPosition(drawingCanvas)) < 0))
            {
                if (drawingCanvas.Tool == DrawToolsLib.ToolType.Pointer)
                {
                    _autoPanning = true;
                    drawingCanvas.Tool = DrawToolsLib.ToolType.None;
                }
                zoomControl.StartPanning();
                e.Handled = true;
                drawingCanvas.UnselectAll();
            }
            RefreshArtworkBounds();
        }

        private void Font_Clicked(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FontDialog dlg = new System.Windows.Forms.FontDialog();
            float wfSize = (float)DpiScale.UpScaleX(drawingCanvas.TextFontSize) / 96 * 72;
            System.Drawing.FontStyle wfStyle;
            if (drawingCanvas.TextFontStyle == FontStyles.Italic)
                wfStyle = System.Drawing.FontStyle.Italic;
            else
                wfStyle = System.Drawing.FontStyle.Regular;
            dlg.Font = new System.Drawing.Font(drawingCanvas.TextFontFamilyName, wfSize, wfStyle);
            dlg.FontMustExist = true;
            dlg.MaxSize = 64;
            dlg.MinSize = 8;
            dlg.ShowColor = false;
            dlg.ShowEffects = false;
            dlg.ShowHelp = false;
            dlg.AllowVerticalFonts = false;
            dlg.AllowVectorFonts = true;
            dlg.AllowScriptChange = false;
            if (dlg.ShowDialog(Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK)
            {
                drawingCanvas.TextFontFamilyName = dlg.Font.FontFamily.GetName(0);
                drawingCanvas.TextFontSize = DpiScale.DownScaleX(dlg.Font.Size / 72 * 96);
                switch(dlg.Font.Style)
                {
                    case System.Drawing.FontStyle.Italic:
                        drawingCanvas.TextFontStyle = FontStyles.Italic;
                        break;
                    default:
                        drawingCanvas.TextFontStyle = FontStyles.Normal;
                        break;
                }
            }
        }
        private void Brush_Clicked(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.ColorDialog dlg = new System.Windows.Forms.ColorDialog();
            dlg.AnyColor = true;
            dlg.FullOpen = true;
            dlg.ShowHelp = false;
            var initial = drawingCanvas.ObjectColor;
            dlg.Color = System.Drawing.Color.FromArgb(initial.A, initial.R, initial.G, initial.B);
            if (dlg.ShowDialog(Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK)
            {
                var final = dlg.Color;
                drawingCanvas.ObjectColor = Color.FromArgb(final.A, final.R, final.G, final.B);
            }
        }
        private void ZoomFit_Clicked(object sender, RoutedEventArgs e)
        {
            var rect = zoomControl.GetActualContentRect();
            var scaleX = zoomControl.ActualWidth / rect.Width;
            var scaleY = zoomControl.ActualHeight / rect.Height;
            var scale = Math.Min(scaleX, scaleY);
            zoomControl.ContentScale = scale;
            var x = zoomControl.ActualWidth / 2 - rect.Width * scale / 2;
            var y = zoomControl.ActualHeight / 2 - rect.Height * scale / 2;
            zoomControl.ContentOffset = new Point(x, y);
        }
        private void ZoomActual_Clicked(object sender, RoutedEventArgs e)
        {
            var pt = zoomControl.ContentOffset;
            var originRect = zoomControl.GetRenderedContentRect();
            var originCenter = new Point(originRect.Width / 2, originRect.Height / 2);
            zoomControl.ContentScale = 1;
            var newRect = zoomControl.GetRenderedContentRect();
            var newCenter = new Point(newRect.Width / 2, newRect.Height / 2);

            var offsetX = originCenter.X - newCenter.X;
            var offsetY = originCenter.Y - newCenter.Y;
            pt.Offset(offsetX, offsetY);
            zoomControl.ContentOffset = pt;
        }

        private void PrintCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            var c = objectCanvas;
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
        private void SaveCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string defaultName = "screenshot";
            string extension = ".png";
            // generate unique file name (screenshot1.png, screenshot2.png etc)
            if (File.Exists(System.IO.Path.Combine(directory, $"{defaultName}{extension}")))
            {
                int i = 1;
                while (File.Exists(System.IO.Path.Combine(directory, $"{defaultName}{i}{extension}")))
                {
                    i++;
                }
                defaultName = defaultName + i.ToString();
            }
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = defaultName; // Default file name
            dlg.DefaultExt = extension; // Default file extension
            dlg.Filter = $"Images ({extension})|*{extension}"; // Filter files by extension
            dlg.OverwritePrompt = true;
            dlg.InitialDirectory = directory;

            // Show save file dialog box
            Nullable<bool> result = dlg.ShowDialog();
            // Process save file dialog box results
            string filename = "";
            if (result == true)
            {
                // Save document
                filename = dlg.FileName;
            }
            else return;

            if (File.Exists(filename))
                File.Delete(filename);
            using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                GetRenderedPng().Save(fs);
            }
        }
        private void UndoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Undo();
            RefreshArtworkBounds();
        }
        private void RedoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Redo();
            RefreshArtworkBounds();
        }
        private void CopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            using (var ms = new MemoryStream())
            {
                GetRenderedPng().Save(ms);
                var img = System.Drawing.Image.FromStream(ms);
                System.Windows.Forms.Clipboard.SetImage(img);
            }
        }
        private void DeleteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Delete();
            RefreshArtworkBounds();
        }
        private void UploadCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            using (var ms = new MemoryStream())
            {
                GetRenderedPng().Save(ms);
                ms.Position = 0;
                byte[] b;
                using (BinaryReader br = new BinaryReader(ms))
                {
                    b = br.ReadBytes(Convert.ToInt32(ms.Length));
                }
                var task = UploadManager.Upload(b, "clowd-default.png");
            }
        }
        private void SelectToolCommand(object sender, ExecutedRoutedEventArgs e)
        {
            var tool = (DrawToolsLib.ToolType)Enum.Parse(typeof(DrawToolsLib.ToolType), (string)e.Parameter);
            drawingCanvas.Tool = tool;
            if (tool == DrawToolsLib.ToolType.None)
            {
                drawingCanvas.UnselectAll();
                drawingCanvas.Cursor = Cursors.SizeAll;
            }
        }
    }
}
