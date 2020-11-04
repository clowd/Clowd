using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.UI.Helpers;
using Clowd.Util;
using ScreenVersusWpf;

namespace Clowd.UI.Controls
{
    public partial class SelectionBorderControl : Decorator
    {
        //public double BorderWidth
        //{
        //    get { return (double)GetValue(BorderWidthProperty); }
        //    set { SetValue(BorderWidthProperty, value); }
        //}

        //public static readonly DependencyProperty BorderWidthProperty = DependencyProperty.Register("BorderWidth", typeof(double), typeof(SelectionBorder), new PropertyMetadata(2d));

        public Brush BorderBrush
        {
            get { return (Brush)GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register("BorderBrush", typeof(Brush), typeof(SelectionBorderControl), new PropertyMetadata(Brushes.Black));

        public bool Resizable
        {
            get { return (bool)GetValue(ResizableProperty); }
            set { SetValue(ResizableProperty, value); }
        }

        public static readonly DependencyProperty ResizableProperty = DependencyProperty.Register("Resizable", typeof(bool), typeof(SelectionBorderControl), new PropertyMetadata(false, DependencyProperty_OnResizableChanged));

        private static void DependencyProperty_OnResizableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as SelectionBorderControl).OnResizableChanged(d, e);
        }

        public WpfRect SelectionRectangle
        {
            get { return (WpfRect)GetValue(SelectionRectangleProperty); }
            set { SetValue(SelectionRectangleProperty, value); }
        }
        public static readonly DependencyProperty SelectionRectangleProperty = DependencyProperty.Register(nameof(SelectionRectangle), typeof(WpfRect), typeof(SelectionBorderControl), new PropertyMetadata(new WpfRect()));


        //public bool IsEnabled
        //{
        //    get { return (bool)GetValue(IsEnabledProperty); }
        //    set { SetValue(IsEnabledProperty, value); }
        //}

        //public static readonly DependencyProperty IsEnabledProperty
        //    = DependencyProperty.Register("IsEnabled", typeof(bool), typeof(SelectionBorderControl), new PropertyMetadata(false, (s, e) => { (s as SelectionBorderControl).EnabledChanged(s, e); }));


        private bool _adornerRegistered = false;

        public SelectionBorderControl()
        {
            InitializeComponent();
            this.IsEnabledChanged += OnIsEnabledChanged;
            OnIsEnabledChanged(this, default(DependencyPropertyChangedEventArgs));

            if (!App.IsDesignMode)
            {
                const double stroke = 2d;
                const double margin = 1d;
                crectBottom.StrokeThickness = ScreenTools.WpfSnapToPixelsFloor(stroke);
                crectBottom.Margin = new Thickness(-ScreenTools.WpfSnapToPixelsFloor(margin));
                crectTop.StrokeThickness = ScreenTools.WpfSnapToPixelsFloor(stroke);
                crectTop.Margin = new Thickness(-ScreenTools.WpfSnapToPixelsFloor(margin));

                selectionBorder2.DataContext = this;
            }
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Storyboard sb = FindResource("BorderDashAnimation") as Storyboard;
            if (IsEnabled)
            {
                sb.Begin();
                crectTop.Visibility = Visibility.Visible;
            }
            else
            {
                sb.Stop();
                crectTop.Visibility = Visibility.Collapsed;
            }
        }

        private void OnResizableChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ManageSelectionResizeHandlers(Resizable);
        }


        private void ManageSelectionResizeHandlers(bool register)
        {
            var adornedElement = this;

            AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(adornedElement);
            if (adornerLayer == null)
                return;

            if (register && !_adornerRegistered)
            {
                _adornerRegistered = true;
                const string template =
    "<ControlTemplate TargetType=\"Thumb\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Grid>" +
            "<Ellipse Fill = \"{TemplateBinding Background}\" />" +
            "<Ellipse Margin = \"1\" Fill = \"White\" />" +
            "<Ellipse Margin = \"2\" Fill = \"{TemplateBinding Background}\" />" +
        "</Grid>" +
    "</ControlTemplate>";
                Style style = new Style(typeof(Thumb));
                style.Setters.Add(new Setter(Thumb.BackgroundProperty, App.Current.Resources["HighlightBrush"]));
                style.Setters.Add(new Setter(Thumb.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template)));

                ResizingAdorner myAdorner = new ResizingAdorner(adornedElement, style);
                myAdorner.SetupCustomResizeHandling((r) =>
                {
                    SelectionRectangle = r.ToWpfRect();
                });
                adornerLayer.Add(myAdorner);

                ScreenPoint mouseDownPos = default(ScreenPoint);
                ScreenRect originRect = default(ScreenRect);
                bool mouseDown = false;
                MouseButtonEventHandler mouseDownHandler = (sender, e) =>
                {
                    adornedElement.CaptureMouse();
                    originRect = SelectionRectangle.ToScreenRect();
                    mouseDownPos = ScreenTools.GetMousePosition();
                    mouseDown = true;
                };
                MouseEventHandler mouseMoveHandler = (sender, e) =>
                {
                    if (!mouseDown)
                        return;
                    var cur = ScreenTools.GetMousePosition();
                    var delta = mouseDownPos - cur;
                    var result = new ScreenRect(originRect.Left - delta.X, originRect.Top - delta.Y, originRect.Width, originRect.Height);
                    result = result.Intersect(ScreenTools.VirtualScreen.Bounds);
                    SelectionRectangle = result.ToWpfRect();
                };
                MouseButtonEventHandler mouseUpHandler = (sender, e) =>
                {
                    adornedElement.ReleaseMouseCapture();
                    mouseDown = false;
                };

                adornedElement.MouseDown += mouseDownHandler;
                adornedElement.MouseMove += mouseMoveHandler;
                adornedElement.MouseUp += mouseUpHandler;
                adornedElement.Cursor = Cursors.SizeAll;
                adornedElement.IsHitTestVisible = true;
            }

            if (!register && _adornerRegistered)
            {
                _adornerRegistered = false;
                if (adornerLayer != null)
                {
                    var resize = adornerLayer.GetAdorners(adornedElement).FirstOrDefault(a => a is ResizingAdorner);
                    if (resize != null)
                        adornerLayer.Remove(resize);

                    adornedElement.RemoveRoutedEventHandlers(UserControl.MouseDownEvent);
                    adornedElement.RemoveRoutedEventHandlers(UserControl.MouseMoveEvent);
                    adornedElement.RemoveRoutedEventHandlers(UserControl.MouseUpEvent);
                    adornedElement.Cursor = Cursors.Cross;
                    adornedElement.IsHitTestVisible = false;
                }
            }
        }
    }
}
