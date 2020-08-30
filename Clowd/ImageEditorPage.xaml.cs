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
using PropertyChanged;
using RT.Util;
using ScreenVersusWpf;

namespace Clowd
{
    [ImplementPropertyChanged]
    public partial class ImageEditorPage : TemplatedControl
    {
        public override string Title => "Edit";

        public StateCapabilities Capabilities { get; set; } = ToolStateManager.Empty();

        private ToolStateManager _manager = new ToolStateManager();
        private DrawToolsLib.ToolType? _shiftPanPreviousTool = null; // null means we're not in a shift-pan
        private BitmapSource _initialImage;
        private WpfRect? _initialBounds;
        private PropertyChangeNotifier toolNotifier;

        private ImageEditorPage()
        {
            InitializeComponent();
            drawingCanvas.SetResourceReference(DrawingCanvas.HandleColorProperty, "AccentColor");
            drawingCanvas.ArtworkBackground = new SolidColorBrush(App.Current.Settings.EditorSettings.CanvasBackground);
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
        }

        public static async void ShowNewEditor(BitmapSource image = null, WpfRect? screenBounds = null, bool allowPrompt = true)
        {
            ImageEditorPage page = null;
            Window window = TemplatedWindow.GetWindow(typeof(ImageEditorPage));

            if (window != null && image != null && allowPrompt)
            {
                if ((page = TemplatedWindow.GetContent<ImageEditorPage>(window)) != null)
                {
                    var result = await NiceDialog.ShowPromptAsync(
                        window,
                        NiceDialogIcon.Information,
                        "There is already an editor open, would you like to insert the captured image here or open a new window?",
                        "Open new window?",
                        "Insert",
                        "Open new window",
                        App.Current.Settings.EditorSettings,
                        s => s.OpenCaptureInExistingEditor);

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

            SyncToolState();
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
                dc.DrawRectangle(new SolidColorBrush(App.Current.Settings.EditorSettings.CanvasBackground), null, bounds);
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
                dc.DrawRectangle(new SolidColorBrush(App.Current.Settings.EditorSettings.CanvasBackground), null, bounds);
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
            SyncToolState();
        }

        private void RedoCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Redo();
            SyncToolState();
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
            SyncToolState();
        }

        private void DeleteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            drawingCanvas.Delete();
            SyncToolState();
        }

        private void UploadCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (!VerifyArtworkExists())
                return;
            var ms = new MemoryStream();
            GetRenderedPng().Save(ms);
            ms.Position = 0;
            var task = UploadManager.Upload(ms, "clowd-default.png");
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

        private void PasteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (drawingCanvas.Paste())
                return;

            var img = ClipboardEx.GetImage();
            AddImage(img);
            SyncToolState();
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
                    SyncToolState();
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
            drawingCanvas.ObjectColor = await NiceDialog.ShowColorDialogAsync(this, drawingCanvas.ObjectColor);
        }

        private async void backgroundColor_Click(object sender, MouseButtonEventArgs e)
        {
            var oldColor = drawingCanvas.ArtworkBackground as SolidColorBrush;
            var newColor = await NiceDialog.ShowColorDialogAsync(this, oldColor?.Color ?? Colors.White);
            drawingCanvas.ArtworkBackground = new SolidColorBrush(newColor);
            App.Current.Settings.EditorSettings.CanvasBackground = newColor;
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

        #endregion

        public class ToolStateManager
        {
            private readonly List<StateCapabilities> _capabilities;
            private static readonly EmptyCapabilities _empty = new EmptyCapabilities();

            public ToolStateManager()
            {
                _capabilities = new List<StateCapabilities>();

                foreach (ToolType e in Enum.GetValues(typeof(ToolType)))
                    _capabilities.Add(new ToolStateCapabilities(e));

                _capabilities.Add(new ObjectStateCapabilities<GraphicArrow>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicEllipse>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicFilledRectangle>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicImage>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicLine>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicPolyLine>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicRectangle>());
                _capabilities.Add(new ObjectStateCapabilities<GraphicText>());
            }

            public StateCapabilities GetObjectCapabilities(object obj)
            {
                if (obj == null)
                    return Empty();

                return _capabilities.Single(c => c.IsSupported(obj));
            }

            public static StateCapabilities Empty()
            {
                return _empty;
            }
        }


        public abstract class StateCapabilities : INotifyPropertyChanged
        {
            public abstract string Description { get; }
            public abstract string Name { get; }
            public abstract bool HasAutoColor { get; }
            public abstract bool HasColor { get; }
            public abstract bool HasStroke { get; }
            public abstract bool HasFont { get; }
            public abstract bool HasAngle { get; }
            public virtual bool CanCanvasZoom { get; } = true;
            public virtual bool CanChangeCanvasBackground { get; } = true;

            public event PropertyChangedEventHandler PropertyChanged;

            public abstract void EnterState(ImageEditorPage page, object obj);
            public abstract void ExitState(ImageEditorPage page);
            public abstract bool IsSupported(object obj);
            public abstract bool IsCurrent(object obj);
        }

        public class EmptyCapabilities : StateCapabilities
        {
            public override string Description => "State";

            public override string Name => "Empty";

            public override bool HasAutoColor => true;

            public override bool HasColor => true;

            public override bool HasStroke => true;

            public override bool HasFont => true;

            public override bool HasAngle => true;

            public override void EnterState(ImageEditorPage page, object obj)
            {
            }

            public override void ExitState(ImageEditorPage page)
            {
            }

            public override bool IsCurrent(object obj)
            {
                return false;
            }

            public override bool IsSupported(object obj)
            {
                return true;
            }
        }

        public class ToolStateCapabilities : StateCapabilities
        {
            private readonly ToolType _tool;
            private readonly ToolActionType _action;

            private static DrawingCanvas _fakeCanvas;
            private ImageEditorPage _page;

            public override string Description => "Tool";

            public override string Name => _tool.ToString();

            public override bool HasAutoColor => _tool == ToolType.Text;

            public override bool HasColor => _action == ToolActionType.Object;

            public override bool HasStroke => _action == ToolActionType.Object && _tool != ToolType.FilledRectangle && _tool != ToolType.Text;

            public override bool HasFont => _tool == ToolType.Text;

            public override bool HasAngle => false;

            public override bool CanChangeCanvasBackground => _action == ToolActionType.Cursor;

            public ToolStateCapabilities(ToolType tool)
            {
                _tool = tool;
                _action = _fakeCanvas.GetToolActionType(tool);
            }

            static ToolStateCapabilities()
            {
                _fakeCanvas = new DrawingCanvas();
            }

            public override void EnterState(ImageEditorPage page, object obj)
            {
                _page = page;

                var canvas = page.drawingCanvas;

                var settings = GetToolSettings();

                canvas.LineWidth = settings.LineWidth;

                canvas.ObjectColor = HasAutoColor && settings.TextObjectColorIsAuto
                    ? Colors.Transparent
                    : settings.ObjectColor;

                canvas.TextFontFamilyName = settings.FontFamily;
                canvas.TextFontSize = settings.FontSize;
                canvas.TextFontStretch = settings.FontStretch;
                canvas.TextFontStyle = settings.FontStyle;
                canvas.TextFontWeight = settings.FontWeight;

                if (HasAutoColor)
                {
                    page.chkColorAuto.Click += ChkColorAuto_Click;
                    page.chkColorAuto.IsChecked = settings.TextObjectColorIsAuto;
                }
                else
                {
                    page.chkColorAuto.IsChecked = false;
                }
            }

            public override void ExitState(ImageEditorPage page)
            {
                if (HasAutoColor)
                {
                    page.chkColorAuto.Click -= ChkColorAuto_Click;
                }

                var canvas = page.drawingCanvas;
                var settings = GetToolSettings();

                settings.LineWidth = canvas.LineWidth;

                if (page.chkColorAuto.IsChecked != true)
                {
                    settings.ObjectColor = canvas.ObjectColor;
                }

                settings.FontFamily = canvas.TextFontFamilyName;
                settings.FontSize = canvas.TextFontSize;
                settings.FontStretch = canvas.TextFontStretch;
                settings.FontStyle = canvas.TextFontStyle;
                settings.FontWeight = canvas.TextFontWeight;

                _page = null;
            }

            private void ChkColorAuto_Click(object sender, RoutedEventArgs e)
            {
                var settings = GetToolSettings();
                var isAuto = ((CheckBox)e.Source).IsChecked == true;

                settings.TextObjectColorIsAuto = isAuto;

                _page.drawingCanvas.ObjectColor = HasAutoColor && settings.TextObjectColorIsAuto
                    ? Colors.Transparent
                    : settings.ObjectColor;
            }

            public override bool IsSupported(object obj)
            {
                if (obj is ToolType tt && tt == _tool)
                    return true;

                return false;
            }

            private SavedToolSettings GetToolSettings() => App.Current.Settings.EditorSettings.ToolSettings[_tool];

            public override bool IsCurrent(object obj)
            {
                if (obj is ToolType toolc)
                    return toolc == _tool;

                return false;
            }
        }

        public class ObjectStateCapabilities<TGraphic> : StateCapabilities where TGraphic : GraphicBase
        {
            public override string Description => "Selection";

            public override string Name
            {
                get
                {
                    var gtxt = typeof(TGraphic).Name;
                    if (gtxt.StartsWith("Graphic"))
                        gtxt = gtxt.Substring(7);
                    return gtxt;
                }
            }

            public override bool HasAutoColor => false;

            public override bool HasColor => IsNotOneOf(typeof(GraphicImage));

            public override bool HasStroke => IsNotOneOf(typeof(GraphicImage), typeof(GraphicText), typeof(GraphicFilledRectangle));

            public override bool HasFont => IsOneOf(typeof(GraphicText));

            public override bool HasAngle => typeof(TGraphic).GetProperty(ANGLE_NAME) != null;

            public override bool CanChangeCanvasBackground => false;

            public ObjectStateCapabilities()
            {

            }

            private const string ANGLE_NAME = "Angle";
            private object _currentGraphic;

            public override void EnterState(ImageEditorPage page, object obj)
            {
                _currentGraphic = obj;
                var canvas = page.drawingCanvas;

                if (obj is GraphicBase g)
                {
                    if (HasColor)
                        canvas.ObjectColor = g.ObjectColor;

                    if (HasStroke)
                        canvas.LineWidth = g.LineWidth;

                    if (HasFont && obj is GraphicText txt)
                    {
                        canvas.TextFontFamilyName = txt.FontName;
                        canvas.TextFontSize = txt.FontSize;
                        canvas.TextFontStretch = txt.FontStretch;
                        canvas.TextFontStyle = txt.FontStyle;
                        canvas.TextFontWeight = txt.FontWeight;
                    }

                    if (HasAngle)
                    {
                        var angleBinding = new Binding(ANGLE_NAME);
                        angleBinding.Source = obj;
                        angleBinding.Mode = BindingMode.TwoWay;
                        angleBinding.Converter = new Converters.AngleConverter();
                        page.textObjectAngle.SetBinding(SpinnerTextBox.TextProperty, angleBinding);

                        var angleResetBinding = new Binding(ANGLE_NAME);
                        angleResetBinding.Source = obj;
                        angleResetBinding.Mode = BindingMode.TwoWay;
                        page.resetObjectAngle.SetBinding(ResetDefaultButton.CurrentValueProperty, angleResetBinding);
                    }
                }
            }

            public override void ExitState(ImageEditorPage page)
            {
                _currentGraphic = null;
            }

            public override bool IsSupported(object obj)
            {
                return obj.GetType() == typeof(TGraphic);
            }

            private bool IsNotOneOf(params Type[] types) => types.All(t => t != typeof(TGraphic));
            private bool IsOneOf(params Type[] types) => types.Any(t => t == typeof(TGraphic));

            public override bool IsCurrent(object obj)
            {
                return obj == _currentGraphic;
            }
        }
    }
}
