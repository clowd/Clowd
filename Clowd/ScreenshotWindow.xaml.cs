using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
using System.Windows.Shapes;
using Clowd.Utilities;
using Clowd.Capture;

namespace Clowd
{
    [PropertyChanged.ImplementPropertyChanged]
    public partial class ScreenshotWindow : Window
    {
        public BitmapSource ScreenImage { get; set; }
        public Rect CroppingRectangle { get; set; } = new Rect(0, 0, 0, 0);
        public Color ObjectColor { get; set; } = Colors.Red;
        public double LineWidth { get; set; } = 2;
        public bool ShowToolbar { get; private set; } = false;
        public bool ShowTips { get; private set; } = true;

        public double ToolButtonSize { get; } = 30;
        public double ToolBottomWidth { get; } = 240;
        public double ToolSideHeight { get; } = 240;
        public Point ToolSidePoint { get; private set; }
        public Point ToolBottomPoint { get; private set; }

        private bool draggingArea = false;
        private bool shiftPressed = false;
        private Point draggingOrigin = default(Point);
        private WindowFinder2 winFinder = new WindowFinder2();

        public ScreenshotWindow(bool startCapture = true)
        {
            InitializeComponent();
            if (startCapture)
                RegisterMouseDragHandlers();
            else
            {
                ShowTips = false;
                UpdateToolLocations();
                ShowToolbar = true;

            }
            ApplicationCommands.Close.InputGestures.Add(new KeyGesture(Key.Escape));
            winFinder.Capture();
            this.Activate();
        }


        private void RegisterMouseDragHandlers()
        {
            this.MouseDown += SurroundFill_MouseDown;
            this.MouseMove += SurroundFill_MouseMove;
            this.MouseUp += SurroundFill_MouseUp;
            this.KeyDown += SurroundFill_KeyDown;
            this.KeyUp += SurroundFill_KeyUp;
        }

        private void SurroundFill_KeyUp(object sender, KeyEventArgs e)
        {
            shiftPressed = false;
            SurroundFill_MouseMove(null, null);
            ShowTips = true;
        }
        private void SurroundFill_KeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                shiftPressed = true;
                SurroundFill_MouseMove(null, null);
                ShowTips = false;
            }
        }
        private void SurroundFill_MouseMove(object sender, MouseEventArgs e)
        {
            var currentPoint = Mouse.GetPosition(this);
            if (draggingArea)
            {
                double x, y, width, height;
                if (currentPoint.X > draggingOrigin.X)
                {

                    x = draggingOrigin.X;
                    width = currentPoint.X - draggingOrigin.X;
                }
                else
                {
                    x = currentPoint.X;
                    width = draggingOrigin.X - currentPoint.X;
                }
                if (currentPoint.Y > draggingOrigin.Y)
                {
                    y = draggingOrigin.Y;
                    height = currentPoint.Y - draggingOrigin.Y;
                }
                else
                {
                    y = currentPoint.Y;
                    height = draggingOrigin.Y - currentPoint.Y;
                }
                CroppingRectangle = new Rect(x, y, width, height);
            }
            else if (shiftPressed)
            {
                var wfMouse = new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                CroppingRectangle = DpiScale.TranslateDownScaleRect(winFinder.GetWindowThatContainsPoint(wfMouse).WindowRect);
            }
            else
            {
                CroppingRectangle = new Rect(0, 0, 0, 0);
            }
        }
        private void SurroundFill_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!(draggingArea || shiftPressed))
                return;
            ((UIElement)sender).ReleaseMouseCapture();

            this.MouseDown -= SurroundFill_MouseDown;
            this.MouseMove -= SurroundFill_MouseMove;
            this.MouseUp -= SurroundFill_MouseUp;
            this.KeyDown -= SurroundFill_KeyDown;
            this.KeyUp -= SurroundFill_KeyUp;

            draggingArea = false;
            shiftPressed = false;

            if (CroppingRectangle.Width < 20 || CroppingRectangle.Height < 20)
            {
                CroppingRectangle = new Rect(0, 0, this.Width, this.Height);
            }

            UpdateToolLocations();
            ShowToolbar = true;
        }
        private void SurroundFill_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var wfMouse = System.Windows.Forms.Cursor.Position;
                ShowTips = false;
                if (!shiftPressed)
                {
                    ((UIElement)sender).CaptureMouse();
                    draggingArea = true;
                    draggingOrigin = e.GetPosition(drawingContainer);
                }
            }
        }

        public void UpdateToolLocations()
        {
            var spaceLeft = CroppingRectangle.X;
            var spaceRight = (this.ActualWidth) - CroppingRectangle.Right;
            var spaceBottom = (this.ActualHeight) - CroppingRectangle.Bottom;
            var spaceTop = CroppingRectangle.Y;

            double sideYoffset = 0, sideXoffset = 0, bottomXoffset = ToolButtonSize;

            //Side

            if (CroppingRectangle.Bottom < ToolSideHeight)
            {
                // Set offset if toolbar will run off to the top side of the screen
                sideYoffset = ToolSideHeight - CroppingRectangle.Bottom;
            }
            if (spaceRight >= ToolButtonSize)
            {
                // room to display normally
                ToolSidePoint = new Point(CroppingRectangle.Right, CroppingRectangle.Bottom - ToolSideHeight + sideYoffset);
            }
            else if (spaceLeft >= ToolButtonSize)
            {
                // move to left side of screen
                ToolSidePoint = new Point(CroppingRectangle.X - ToolButtonSize, CroppingRectangle.Bottom - ToolSideHeight + sideYoffset);
            }
            else if (spaceRight > 0)
            {
                // no room on left side or right side, so align toolbar with right edge of screen
                sideXoffset = 0 - ToolButtonSize + spaceRight;
                ToolSidePoint = new Point(CroppingRectangle.Right + sideXoffset, CroppingRectangle.Bottom - ToolSideHeight + sideYoffset);
            }
            else
            {
                // align toolbar with inside edge of selection
                sideXoffset = 0 - ToolButtonSize;
                ToolSidePoint = new Point(CroppingRectangle.Right - ToolButtonSize, CroppingRectangle.Bottom - ToolSideHeight + sideYoffset);
            }

            //bottom
            if (CroppingRectangle.Right < ToolBottomWidth)
            {
                bottomXoffset = ToolBottomWidth - CroppingRectangle.Right;
            }
            else if (sideYoffset > 0)
            {
                bottomXoffset = 0;
            }
            else if (sideXoffset != 0)
            {
                bottomXoffset = sideXoffset;
            }
            if (spaceBottom >= ToolButtonSize)
            {
                // space, display normally
                ToolBottomPoint = new Point(CroppingRectangle.Right - ToolBottomWidth + bottomXoffset, CroppingRectangle.Bottom);
            }
            else if (spaceTop >= ToolButtonSize)
            {
                // display on top, and if offset still at default, change to 0
                if (bottomXoffset == ToolButtonSize)
                    bottomXoffset = 0;
                ToolBottomPoint = new Point(CroppingRectangle.Right - ToolBottomWidth + bottomXoffset, CroppingRectangle.Y - ToolButtonSize);
            }
            else if (spaceBottom > 0)
            {
                // display on bottom edge of screen
                ToolBottomPoint = new Point(CroppingRectangle.Right - ToolBottomWidth + bottomXoffset, CroppingRectangle.Bottom - ToolButtonSize + spaceBottom);
            }
            else
            {
                // display on bottom edge of selection
                ToolBottomPoint = new Point(CroppingRectangle.Right - ToolBottomWidth + bottomXoffset, CroppingRectangle.Bottom - ToolButtonSize);
            }
        }
        public void LoadBitmap(System.Drawing.Bitmap source)
        {
            IntPtr ip = source.GetHbitmap();
            BitmapSource bs = null;
            try
            {
                bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(ip,
                   IntPtr.Zero, Int32Rect.Empty,
                   System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                Clowd.Interop.Gdi32.GDI32.DeleteObject(ip);
            }

            ScreenImage = bs;
        }
        private DrawingVisual GetRenderedVisual(bool withDrawings = true)
        {
            Rect croppingRect = DpiScale.TranslateUpScaleRect(CroppingRectangle);
            var i32r = new Int32Rect((int)croppingRect.X, (int)croppingRect.Y, (int)croppingRect.Width, (int)croppingRect.Height);
            var cropped = new CroppedBitmap(ScreenImage, i32r);

            var rect = new Rect(0, 0, CroppingRectangle.Width, CroppingRectangle.Height);
            // Create DrawingVisual and get its drawing context
            DrawingVisual vs = new DrawingVisual();
            DrawingContext dc = vs.RenderOpen();

            // Draw image
            dc.DrawImage(cropped, rect);

            // Remove clip in the canvas - we set our own clip.
            drawingCanvas.RemoveClip();

            // Prepare drawing context to draw graphics
            dc.PushClip(new RectangleGeometry(rect));

            // Ask canvas to draw overlays
            drawingCanvas.Draw(dc);

            // Restore clip
            drawingCanvas.RefreshClip();

            dc.Pop();
            dc.Close();

            return vs;
        }
        private RenderTargetBitmap GetRenderedBitmap(bool withDrawings = true)
        {
            var drawingVisual = GetRenderedVisual(withDrawings);
            RenderTargetBitmap bmp = new RenderTargetBitmap(
                (int)DpiScale.UpScaleX(drawingVisual.ContentBounds.Width),
                (int)DpiScale.UpScaleY(drawingVisual.ContentBounds.Height),
                DpiScale.DpiX,
                DpiScale.DpiY,
                PixelFormats.Pbgra32);
            bmp.Render(drawingVisual);
            return bmp;
        }

        private void buttonTool_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            drawingCanvas.Tool = (DrawToolsLib.ToolType)Enum.Parse(typeof(DrawToolsLib.ToolType),
                ((System.Windows.Controls.Primitives.ButtonBase)sender).Tag.ToString());
            e.Handled = true;
        }
        private void buttonActionReset_Click(object sender, RoutedEventArgs e)
        {
            ShowToolbar = false;
            CroppingRectangle = new Rect(0, 0, 0, 0);
            drawingCanvas.Clear();
            RegisterMouseDragHandlers();
            ShowTips = true;
        }
        private async void buttonActionSearch_Click(object sender, RoutedEventArgs e)
        {
            //http://www.google.com/searchbyimage?image_url=http://prntscr.com/806t1i/direct

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                ms.Position = 0;
                byte[] b;
                using (BinaryReader br = new BinaryReader(ms))
                {
                    b = br.ReadBytes(Convert.ToInt32(ms.Length));
                }
                this.Close();
                var url = await UploadManager.Upload(b, "clowd-default.png", new UploadOptions() { Direct = true });
                UploadManager.RemoveUpload(url, true);
                Process.Start("http://www.google.com/searchbyimage?image_url=" + url);
            }
        }
        private void buttonActionUpload_Click(object sender, RoutedEventArgs e)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                ms.Position = 0;
                byte[] b;
                using (BinaryReader br = new BinaryReader(ms))
                {
                    b = br.ReadBytes(Convert.ToInt32(ms.Length));
                }
                var task = UploadManager.Upload(b, "clowd-default.png");
            }
            this.Close();
        }
        private void buttonActionPicker_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Samples.CustomControls.ColorPickerDialog cPicker = new Microsoft.Samples.CustomControls.ColorPickerDialog();

            cPicker.StartingColor = ObjectColor;
            cPicker.Owner = this;

            bool? dialogResult = cPicker.ShowDialog();
            if (dialogResult != null && (bool)dialogResult == true)
            {
                ObjectColor = cPicker.SelectedColor;
            }
        }
        private void buttonActionPreview_Click(object sender, RoutedEventArgs e)
        {
            RenderTargetBitmap bmp = GetRenderedBitmap();
            ScreenshotWindow sw = new ScreenshotWindow(false);
            sw.Topmost = false;
            sw.WindowStyle = WindowStyle.ThreeDBorderWindow;
            sw.WindowState = WindowState.Normal;
            sw.Width = Math.Min(bmp.Width + 18, ScreenUtil.ScreenBounds.Width - 800);
            sw.Height = Math.Min(bmp.Height + 40, ScreenUtil.ScreenBounds.Height - 400);
            sw.imageBackground.Stretch = Stretch.Uniform;
            sw.ResizeMode = ResizeMode.CanResize;
            this.Close();
            sw.Show();
            sw.ScreenImage = bmp;
            sw.CroppingRectangle = new Rect(-100, -100, 10000, 10000);
            sw.ToolSidePoint = new Point(-50, 0);
            sw.ToolBottomPoint = new Point(0, -50);
        }

        void UndoCommand(object sender, ExecutedRoutedEventArgs args)
        {
            drawingCanvas.Undo();
        }
        void RedoCommand(object sender, ExecutedRoutedEventArgs args)
        {
            drawingCanvas.Redo();
        }
        void SaveCommand(object sender, ExecutedRoutedEventArgs args)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));

            string filename = "";
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = "Screenshot"; // Default file name
            dlg.DefaultExt = ".png"; // Default file extension
            dlg.Filter = "Images (.png)|*.png"; // Filter files by extension
            dlg.OverwritePrompt = true;
            dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            // Show save file dialog box
            Nullable<bool> result = dlg.ShowDialog();
            // Process save file dialog box results
            if (result == true)
            {
                // Save document
                filename = dlg.FileName;
            }
            else return;

            if (File.Exists(filename)) File.Delete(filename);
            using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
            this.Close();
        }
        void PrintCommand(object sender, ExecutedRoutedEventArgs args)
        {
            PrintDialog dlg = new PrintDialog();
            var image = GetRenderedVisual();
            this.Close();
            if (dlg.ShowDialog().GetValueOrDefault() != true)
            {
                return;
            }

            dlg.PrintVisual(image, "Graphics");
        }
        void CloseCommand(object sender, ExecutedRoutedEventArgs args)
        {
            this.Close();
        }
        void CopyCommand(object sender, ExecutedRoutedEventArgs args)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                var img = System.Drawing.Image.FromStream(ms);
                System.Windows.Forms.Clipboard.SetImage(img);
            }
            this.Close();
        }
    }
}
