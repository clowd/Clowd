using Clowd.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
using Clowd.Interop.Gdi32;
using CS.Util.Extensions;
using DrawToolsLib.Graphics;
using ScreenVersusWpf;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class ImageEditorPage : TemplatedControl
    {
        public override string Title => "Editor";
        public bool ShowActionLabels { get; set; } = true;

        private DrawToolsLib.ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private BitmapSource _image;
        private Size _imageSize;
        private double _actionBarSize = double.NaN;
        private bool _actionBarLabels;

        public ImageEditorPage() : this(null)
        {
        }

        public ImageEditorPage(BitmapSource initImage)
        {
            InitializeComponent();
            drawingCanvas.SetResourceReference(DrawToolsLib.DrawingCanvas.HandleColorProperty, "AccentColor");
            drawingCanvas.ObjectColor = Colors.Red;
            drawingCanvas.LineWidth = 2;
            this.Loaded += ImageEditorPage_Loaded;
            this.SizeChanged += ImageEditorPage_SizeChanged;
            _image = initImage;
            if (_image != null)
            {
                _imageSize = new Size(ScreenTools.ScreenToWpf(initImage.PixelWidth),
                    ScreenTools.ScreenToWpf(initImage.Height));
                var graphic = new GraphicImage(drawingCanvas, new Rect(new Point(0, 0), _imageSize), _image);
                drawingCanvas.AddGraphic(graphic);
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
            using (DrawingContext dc = vs.RenderOpen())
            {
                var transform = new TranslateTransform(Math.Floor(-bounds.Left), Math.Floor(-bounds.Top));
                dc.PushTransform(transform);
                dc.DrawRectangle(Brushes.White, null, bounds);
                drawingCanvas.Draw(dc);
            }
            return vs;
        }
        private RenderTargetBitmap GetRenderedBitmap()
        {
            var drawingVisual = GetRenderedVisual();
            RenderTargetBitmap bmp = new RenderTargetBitmap(
                ScreenTools.WpfToScreen(drawingVisual.ContentBounds.Width),
                ScreenTools.WpfToScreen(drawingVisual.ContentBounds.Height),
                ScreenTools.WpfToScreen(96),
                ScreenTools.WpfToScreen(96),
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

        protected override async void OnActivated(Window wnd)
        {
            if (_imageSize.IsEmpty || _imageSize.IsDefault())
                return;

            var padding = App.Current.Settings.EditorSettings.CapturePadding;
            var sidebarWidth = rightSidepanel.Visibility != Visibility.Visible ? 0 : (ActualWidth - RightSidebarX() + 2 * RightSidebarMargin());
            bool fit = TemplatedWindow.SizeToContent(wnd, new Size(_imageSize.Width + padding * 2 + sidebarWidth,
                _imageSize.Height + actionRow.ActualHeight + padding * 2));

            // just doing this to force a thread context switch.
            // by the time we get back on to the UI thread the window will be done resizing.
            await Task.Delay(10);

            if (fit)
                ZoomActual_Clicked(null, null);
            else
                ZoomFit_Clicked(null, null);
        }

        private void ImageEditorPage_Loaded(object sender, RoutedEventArgs e)
        {
            // you need to focus a button, or some other control that holds keyboard focus.
            // if you don't do this, input bindings / keyboard shortcuts won't work.
            Keyboard.Focus(uploadButton);
            ZoomFit_Clicked(null, null);

            _actionBarSize = actionBar.ActualWidth;
            _actionBarLabels = ShowActionLabels = true;
        }

        private void ImageEditorPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // probably need to bring this back soon, just ommiting for now and recording in source control.

            //if (!Double.IsNaN(_actionBarSize) && rootGrid.ActualWidth < _actionBarSize && ShowActionLabels)
            //{
            //    ShowActionLabels = false;
            //}
            //else if(_actionBarLabels && rootGrid.ActualWidth > _actionBarSize && !ShowActionLabels)
            //{
            //    ShowActionLabels = true;
            //}

            if (toolBar.ActualWidth + actionBar.ActualWidth > rootGrid.ActualWidth)
            {
                actionRow.Height = new GridLength(61);
                toolBar.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                actionRow.Height = new GridLength(30);
                toolBar.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }

        private void Font_Clicked(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FontDialog dlg = new System.Windows.Forms.FontDialog();
            float wfSize = (float)ScreenTools.ScreenToWpf(drawingCanvas.TextFontSize) / 96 * 72;
            System.Drawing.FontStyle wfStyle;
            if (drawingCanvas.TextFontStyle == FontStyles.Italic)
                wfStyle = System.Drawing.FontStyle.Italic;
            else
                wfStyle = System.Drawing.FontStyle.Regular;
            if (drawingCanvas.TextFontWeight.ToOpenTypeWeight() > 400)
                wfStyle |= System.Drawing.FontStyle.Bold;
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
                drawingCanvas.TextFontSize = ScreenTools.WpfToScreen(dlg.Font.Size / 72 * 96);
                drawingCanvas.TextFontStyle = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Italic) ? FontStyles.Italic : FontStyles.Normal;
                drawingCanvas.TextFontWeight = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Bold) ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private void Brush_Clicked(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.ColorDialog dlg = new System.Windows.Forms.ColorDialog();
            dlg.AnyColor = true;
            dlg.FullOpen = true;
            dlg.ShowHelp = false;
            dlg.CustomColors = App.Current.Settings.CustomColors;
            var initial = drawingCanvas.ObjectColor;
            dlg.Color = System.Drawing.Color.FromArgb(initial.A, initial.R, initial.G, initial.B);
            if (dlg.ShowDialog(Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK)
            {
                App.Current.Settings.CustomColors = dlg.CustomColors;
                App.Current.Settings.Save();
                var final = dlg.Color;
                drawingCanvas.ObjectColor = Color.FromArgb(final.A, final.R, final.G, final.B);
            }
        }

        private double RightSidebarX()
        {
            if (rightSidepanel.Visibility != Visibility.Visible)
                return 0;
            return rightSidepanel.TransformToVisual(this).Transform(new Point(0, 0)).X;
        }

        private double RightSidebarMargin()
        {
            if (rightSidepanel.Visibility != Visibility.Visible)
                return 0;
            return ActualWidth - rightSidepanel.TransformToVisual(this).Transform(new Point(rightSidepanel.Width, 0)).X;
        }

        private void ZoomFit_Clicked(object sender, RoutedEventArgs e)
        {
            double? widthOverride = null;
            if (rightSidepanel.Visibility == Visibility.Visible)
                widthOverride = RightSidebarX() - RightSidebarMargin();
            drawingCanvas.ZoomPanFit(widthOverride);
        }

        private void ZoomActual_Clicked(object sender, RoutedEventArgs e)
        {
            double? widthOverride = null;
            if (rightSidepanel.Visibility == Visibility.Visible)
                widthOverride = RightSidebarX();
            drawingCanvas.ZoomPanActualSize(widthOverride);
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
        }

        private void RedoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Redo();
        }

        private void CopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;

            var data = drawingCanvas.GetClipboardObject();
            ClipboardEx.AddImageToData(data, GetRenderedBitmap());
            Clipboard.SetDataObject(data, true);
        }

        private void CutCommand(object sender, ExecutedRoutedEventArgs e)
        {
            CopyCommand(sender, e);
            drawingCanvas.DeleteAll();
        }

        private void DeleteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Delete();
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
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            var tool = (DrawToolsLib.ToolType)Enum.Parse(typeof(DrawToolsLib.ToolType), (string)e.Parameter);
            drawingCanvas.Tool = tool;
        }

        private void PasteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (drawingCanvas.Paste())
                return;

            var img = ClipboardEx.GetImage();
            if (img == null)
                return;
            var width = ScreenTools.ScreenToWpf(img.PixelWidth);
            var height = ScreenTools.ScreenToWpf(img.PixelHeight);
            var graphic = new GraphicImage(drawingCanvas, new Rect(
                drawingCanvas.WorldOffset.X - (width / 2),
                drawingCanvas.WorldOffset.Y - (height / 2),
                width, height), img);
            drawingCanvas.AddGraphic(graphic);
        }

        private void SelectAllCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.SelectAll();
        }

        private void rootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox)
                return;
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && _shiftPanPreviousTool == null && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _shiftPanPreviousTool = drawingCanvas.Tool;
                drawingCanvas.Tool = DrawToolsLib.ToolType.None;
                shiftIndicator.Background = (Brush)App.Current.Resources["AccentColorBrush"];
                shiftIndicator.Opacity = 1;
            }
        }

        private void rootGrid_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && _shiftPanPreviousTool != null)
            {
                drawingCanvas.Tool = _shiftPanPreviousTool.Value;
                _shiftPanPreviousTool = null;
                shiftIndicator.Background = new SolidColorBrush(Color.FromRgb(112, 112, 112));
                shiftIndicator.Opacity = 0.8;
            }
        }
    }
}
