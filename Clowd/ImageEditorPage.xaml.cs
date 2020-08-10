using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using Clowd.Controls;
using Clowd.Utilities;
using Cyotek.Windows.Forms;
using DrawToolsLib;
using DrawToolsLib.Graphics;
using RT.Util;
using ScreenVersusWpf;

namespace Clowd
{
    public partial class ImageEditorPage : TemplatedControl
    {
        public override string Title => "Edit";

        private DrawToolsLib.ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private BitmapSource _initialImage;
        private WpfRect? _initialBounds;

        private ImageEditorPage()
        {
            InitializeComponent();
            drawingCanvas.SetResourceReference(DrawToolsLib.DrawingCanvas.HandleColorProperty, "AccentColor");

            // register tool changed listener
            var toolDescriptor = DependencyPropertyDescriptor.FromProperty(DrawingCanvas.ToolProperty, typeof(DrawingCanvas));
            toolDescriptor.AddValueChanged(drawingCanvas, drawingCanvas_ToolChanged);

            drawingCanvas.ArtworkBackground = new SolidColorBrush(App.Current.Settings.EditorSettings.CanvasBackground);
            drawingCanvas.MouseUp += drawingCanvas_MouseUp;
            this.Loaded += ImageEditorPage2_Loaded;
            SyncToolState();
        }

        private void ImageEditorPage2_Loaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(buttonFocus);
        }

        public static void ShowNewEditor(BitmapSource image = null, WpfRect? screenBounds = null)
        {
            ImageEditorPage page = null;
            Window window = TemplatedWindow.GetWindow(typeof(ImageEditorPage));

            if (window != null)
            {
                if ((page = TemplatedWindow.GetContent<ImageEditorPage>(window)) != null)
                {
                    var memory = App.Current.Settings.EditorSettings.OpenCaptureInExistingEditor;
                    var result = window.ShowPrompt(
                        MessageBoxIcon.Information,
                        "There is already an editor open, would you like to insert the captured image here or open a new window?",
                        "Open new window?",
                        "Insert",
                        "Open new window",
                        ref memory);
                    App.Current.Settings.EditorSettings.OpenCaptureInExistingEditor = memory;
                    if (result)
                    {
                        page.AddImage(image);
                        window.Activate();
                        return;
                    }
                }
            }

            page = new ImageEditorPage();
            page._initialImage = image;
            page._initialBounds = screenBounds;
            window = TemplatedWindow.CreateWindow("Edit Capture", page);
            page.DoWindowFit(window);
            window.Show();
        }

        protected override async void OnActivated(Window wnd)
        {
            if (_initialImage == null)
                return;

            AddImage(_initialImage);

            var wasSidebarWidth = toolBar.ActualWidth;
            var wasActionRowHeight = propertiesBar.ActualHeight;

            bool fit = DoWindowFit(wnd);
            if (propertiesBar.ActualHeight != wasActionRowHeight || toolBar.ActualWidth != wasSidebarWidth)
                fit = DoWindowFit(wnd); // re-fit in case the action row has reflowed

            // just doing this to force a thread context switch.
            // by the time we get back on to the UI thread the window will be done resizing.
            await Task.Delay(10);

            if (fit)
                drawingCanvas.ZoomPanActualSize();
            else
                drawingCanvas.ZoomPanFit();
        }

        #region Helpers

        private void AddImage(BitmapSource img)
        {
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

        private bool DoWindowFit(Window wnd)
        {
            if (_initialImage == null)
                return true;

            var imageSize = new Size(ScreenTools.ScreenToWpf(_initialImage.PixelWidth), ScreenTools.ScreenToWpf(_initialImage.Height));
            var capturePadding = App.Current.Settings.EditorSettings.CapturePadding;
            var contentOffsetX = capturePadding + Math.Max(30, toolBar.ActualWidth);
            var contentOffsetY = capturePadding + Math.Max(30, propertiesBar.ActualHeight);

            var contentSize = new Size(imageSize.Width + contentOffsetX + capturePadding, imageSize.Height + contentOffsetY + capturePadding);

            if (_initialBounds != null)
            {
                //var w = TemplatedWindow.CreateWindow("Edit Capture", new ImageEditorPage(cropped));
                //var rectPos = SelectionRectangle;
                //var primaryScreen = ScreenTools.Screens.First().Bounds.ToWpfRect();
                //w.Left = rectPos.Left - primaryScreen.Left - App.Current.Settings.EditorSettings.CapturePadding - 7;
                //w.Top = rectPos.Top - primaryScreen.Top - App.Current.Settings.EditorSettings.CapturePadding - 60;
                //w.Show();

                return TemplatedWindow.SizeToContent(wnd, contentSize, _initialBounds.Value.Left - contentOffsetX, _initialBounds.Value.Top - contentOffsetY);
            }
            else
            {
                return TemplatedWindow.SizeToContent(wnd, contentSize);
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
            var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

            DrawingVisual vs = new DrawingVisual();
            using (DrawingContext dc = vs.RenderOpen())
            {
                dc.PushTransform(transform);
                dc.DrawRectangle(Brushes.White, null, bounds);
                dc.Pop();
                drawingCanvas.Draw(dc, null, transform, false);
            }
            return vs;
        }

        private RenderTargetBitmap GetRenderedBitmap()
        {
            var bounds = drawingCanvas.GetArtworkBounds();
            var transform = new TranslateTransform(-bounds.Left, -bounds.Top);

            RenderTargetBitmap bmp = new RenderTargetBitmap(
                ScreenTools.WpfToScreen(bounds.Width),
                ScreenTools.WpfToScreen(bounds.Height),
                ScreenTools.WpfToScreen(96),
                ScreenTools.WpfToScreen(96),
                PixelFormats.Pbgra32);
            DrawingVisual background = new DrawingVisual();
            using (DrawingContext dc = background.RenderOpen())
            {
                dc.PushTransform(transform);
                dc.DrawRectangle(Brushes.White, null, bounds);
            }
            bmp.Render(background);
            drawingCanvas.Draw(null, bmp, transform, false);
            return bmp;
        }

        private PngBitmapEncoder GetRenderedPng()
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(GetRenderedBitmap()));
            return enc;
        }

        private void SyncToolState()
        {
            var selection = drawingCanvas.Selection.ToArray();
            panelAngle.Visibility = Visibility.Collapsed;
            panelFont.Visibility = Visibility.Collapsed;
            panelBackground.Visibility = Visibility.Collapsed;
            panelZoom.Visibility = Visibility.Collapsed;

            var actionType = drawingCanvas.GetToolActionType(drawingCanvas.Tool);
            if (selection.Length == 0 && (actionType == DrawToolsLib.ToolActionType.Cursor || actionType == ToolActionType.Drawing))
            {
                this.labelSelectionType.Text = "Tool: ";
                this.labelTool.Text = drawingCanvas.Tool.ToString();
                panelColor.Visibility = Visibility.Collapsed;
                panelStroke.Visibility = Visibility.Collapsed;
                panelBackground.Visibility = Visibility.Visible;
                panelZoom.Visibility = Visibility.Visible;
                return;
            }
            else
            {
                panelColor.Visibility = Visibility.Visible;
                panelStroke.Visibility = Visibility.Visible;
            }

            if (selection.Length == 1)
            {
                var obj = selection.First();
                this.labelSelectionType.Text = "Selection: ";

                var gtxt = obj.GetType().Name;

                if (gtxt.StartsWith("Graphic"))
                    gtxt = gtxt.Substring(7);

                this.labelTool.Text = gtxt;

                if (obj is GraphicImage)
                {
                    panelColor.Visibility = Visibility.Collapsed;
                    panelStroke.Visibility = Visibility.Collapsed;
                    return;
                }

                if (obj.GetType().GetProperty("Angle") != null)
                {
                    panelAngle.Visibility = Visibility.Visible;
                    var angleBinding = new Binding("Angle");
                    angleBinding.Source = obj;
                    angleBinding.Mode = BindingMode.TwoWay;
                    angleBinding.Converter = new Converters.AngleConverter();
                    textObjectAngle.SetBinding(TextBox.TextProperty, angleBinding);
                    var angleResetBinding = new Binding("Angle");
                    angleResetBinding.Source = obj;
                    angleResetBinding.Mode = BindingMode.TwoWay;
                    resetObjectAngle.SetBinding(ResetDefaultButton.CurrentValueProperty, angleResetBinding);
                }

                if (obj is GraphicText txt)
                {
                    panelFont.Visibility = Visibility.Visible;
                    drawingCanvas.TextFontFamilyName = txt.FontName;
                    drawingCanvas.TextFontSize = txt.FontSize;
                    drawingCanvas.TextFontStretch = txt.FontStretch;
                    drawingCanvas.TextFontStyle = txt.FontStyle;
                    drawingCanvas.TextFontWeight = txt.FontWeight;
                }

                if (obj is GraphicBase g)
                {
                    drawingCanvas.LineWidth = g.LineWidth;
                    drawingCanvas.ObjectColor = g.ObjectColor;
                }
            }
            else if (selection.Length > 1)
            {
                this.labelSelectionType.Text = "Selection: ";
                this.labelTool.Text = "Multiple";
            }
            else
            {
                this.labelSelectionType.Text = "Tool: ";
                this.labelTool.Text = drawingCanvas.Tool.ToString();

                if (drawingCanvas.Tool == ToolType.Text)
                    panelFont.Visibility = Visibility.Visible;

                if (actionType != ToolActionType.Cursor)
                {
                    var tools = App.Current.Settings.EditorSettings.ToolSettings;
                    if (!tools.ContainsKey(drawingCanvas.Tool))
                        tools[drawingCanvas.Tool] = new SavedToolSettings();
                    var settings = tools[drawingCanvas.Tool];

                    drawingCanvas.LineWidth = settings.LineWidth;
                    drawingCanvas.ObjectColor = settings.ObjectColor;
                    drawingCanvas.TextFontFamilyName = settings.FontFamily;
                    drawingCanvas.TextFontSize = settings.FontSize;
                    drawingCanvas.TextFontStretch = settings.FontStretch;
                    drawingCanvas.TextFontStyle = settings.FontStyle;
                    drawingCanvas.TextFontWeight = settings.FontWeight;
                }
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
            if (drawingCanvas.CanUnselectAll)
                drawingCanvas.UnselectAll();
            var tool = (DrawToolsLib.ToolType)Enum.Parse(typeof(DrawToolsLib.ToolType), (string)e.Parameter);
            drawingCanvas.Tool = tool;
        }

        private void PasteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (drawingCanvas.Paste())
                return;

            var img = ClipboardEx.GetImage();
            AddImage(img);
        }

        private void SelectAllCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.SelectAll();
        }

        #endregion

        #region Events

        private void rootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox textBox)
            {
                if (e.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    Keyboard.Focus(buttonFocus);
                }

                // Handle A-Z here, so that keybindings to those keys will be handled here
                if ((int)e.Key >= 44 && (int)e.Key <= 69 && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    var key = (char)KeyInterop.VirtualKeyFromKey(e.Key);
                    string str = key.ToString().ToLower();
                    if (Console.CapsLock)
                        str = str.ToUpper();

                    if (!String.IsNullOrWhiteSpace(str))
                    {
                        var comp = new TextComposition(InputManager.Current, textBox, str);
                        textBox.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, comp)
                        {
                            RoutedEvent = TextCompositionManager.TextInputEvent
                        });
                        e.Handled = true;
                        return;
                    }
                }
                return;
            }
            if ((e.Key == Key.LeftShift || e.Key == Key.RightShift) && _shiftPanPreviousTool == null && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _shiftPanPreviousTool = drawingCanvas.Tool;
                drawingCanvas.Tool = DrawToolsLib.ToolType.None;
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
            SyncToolState();
        }

        private async void objectColor_Click(object sender, MouseButtonEventArgs e)
        {
            drawingCanvas.ObjectColor = await this.ShowColorDialog(drawingCanvas.ObjectColor);
            if (drawingCanvas.SelectionCount == 0)
                App.Current.Settings.EditorSettings.ToolSettings[drawingCanvas.Tool].ObjectColor = drawingCanvas.ObjectColor;
        }

        private async void backgroundColor_Click(object sender, MouseButtonEventArgs e)
        {
            var oldColor = drawingCanvas.ArtworkBackground as SolidColorBrush;
            var newColor = await this.ShowColorDialog(oldColor?.Color ?? Colors.White);
            drawingCanvas.ArtworkBackground = new SolidColorBrush(newColor);
            App.Current.Settings.EditorSettings.CanvasBackground = newColor;
        }

        private void font_Click(object sender, RoutedEventArgs e)
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
            if (dlg.ShowDialog(Window.GetWindow(this)) == System.Windows.Forms.DialogResult.OK)
            {
                drawingCanvas.TextFontFamilyName = dlg.Font.FontFamily.GetName(0);
                drawingCanvas.TextFontSize = ScreenTools.WpfToScreen(dlg.Font.SizeInPoints / 72 * 96);
                drawingCanvas.TextFontStyle = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Italic) ? FontStyles.Italic : FontStyles.Normal;
                drawingCanvas.TextFontWeight = dlg.Font.Style.HasFlag(System.Drawing.FontStyle.Bold) ? FontWeights.Bold : FontWeights.Normal;

                if (drawingCanvas.SelectionCount == 0)
                {
                    var toolset = App.Current.Settings.EditorSettings.ToolSettings[drawingCanvas.Tool];
                    toolset.FontFamily = drawingCanvas.TextFontFamilyName;
                    toolset.FontSize = drawingCanvas.TextFontSize;
                    toolset.FontStyle = drawingCanvas.TextFontStyle;
                    toolset.FontWeight = drawingCanvas.TextFontWeight;
                }
            }
        }

        private void strokeWidth_Changed(object sender, TextChangedEventArgs e)
        {
            if (drawingCanvas.SelectionCount == 0)
            {
                var toolset = App.Current.Settings.EditorSettings.ToolSettings[drawingCanvas.Tool];
                toolset.LineWidth = drawingCanvas.LineWidth;
            }
        }

        #endregion

    }
}
