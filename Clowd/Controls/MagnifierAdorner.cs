using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Clowd.Controls
{
    [TemplatePart(Name = PART_VisualBrush, Type = typeof(VisualBrush))]
    public class Magnifier : Control
    {
        private const double DEFAULT_SIZE = 100d;
        private const string PART_VisualBrush = "PART_VisualBrush";

        #region Private Members

        private VisualBrush _visualBrush = new VisualBrush();

        #endregion //Private Members

        #region Properties

        #region FrameType

        public static readonly DependencyProperty FrameTypeProperty = DependencyProperty.Register("FrameType", typeof(FrameType), typeof(Magnifier), new UIPropertyMetadata(FrameType.Circle, OnFrameTypeChanged));
        public FrameType FrameType
        {
            get
            {
                return (FrameType)GetValue(FrameTypeProperty);
            }
            set
            {
                SetValue(FrameTypeProperty, value);
            }
        }

        private static void OnFrameTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Magnifier m = (Magnifier)d;
            m.OnFrameTypeChanged((FrameType)e.OldValue, (FrameType)e.NewValue);
        }

        protected virtual void OnFrameTypeChanged(FrameType oldValue, FrameType newValue)
        {
            this.UpdateSizeFromRadius();
        }

        #endregion //FrameType

        #region Radius

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register("Radius", typeof(double), typeof(Magnifier), new FrameworkPropertyMetadata((Magnifier.DEFAULT_SIZE / 2), new PropertyChangedCallback(OnRadiusPropertyChanged)));
        public double Radius
        {
            get
            {
                return (double)GetValue(RadiusProperty);
            }
            set
            {
                SetValue(RadiusProperty, value);
            }
        }

        private static void OnRadiusPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Magnifier m = (Magnifier)d;
            m.OnRadiusChanged(e);
        }

        protected virtual void OnRadiusChanged(DependencyPropertyChangedEventArgs e)
        {
            this.UpdateSizeFromRadius();
        }

        #endregion

        #region Target

        public static readonly DependencyProperty TargetProperty = DependencyProperty.Register("Target", typeof(UIElement), typeof(Magnifier));
        public UIElement Target
        {
            get
            {
                return (UIElement)GetValue(TargetProperty);
            }
            set
            {
                SetValue(TargetProperty, value);
            }
        }

        #endregion //Target

        #region ViewBox

        internal Rect ViewBox
        {
            get
            {
                return _visualBrush.Viewbox;
            }
            set
            {
                _visualBrush.Viewbox = value;
            }
        }

        #endregion

        #region ZoomFactor

        public static readonly DependencyProperty ZoomFactorProperty = DependencyProperty.Register("ZoomFactor", typeof(double), typeof(Magnifier), new FrameworkPropertyMetadata(0.5, OnZoomFactorPropertyChanged), OnValidationCallback);
        public double ZoomFactor
        {
            get
            {
                return (double)GetValue(ZoomFactorProperty);
            }
            set
            {
                SetValue(ZoomFactorProperty, value);
            }
        }

        private static bool OnValidationCallback(object baseValue)
        {
            double zoomFactor = (double)baseValue;
            return (zoomFactor >= 0);
        }

        private static void OnZoomFactorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Magnifier m = (Magnifier)d;
            m.OnZoomFactorChanged(e);
        }

        protected virtual void OnZoomFactorChanged(DependencyPropertyChangedEventArgs e)
        {
            UpdateViewBox();
        }

        #endregion //ZoomFactor

        #region MouseOffset


        public Point MouseOffset
        {
            get { return (Point)GetValue(MouseOffsetProperty); }
            set { SetValue(MouseOffsetProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MyProperty.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty MouseOffsetProperty =
            DependencyProperty.Register("MouseOffset", typeof(Point), typeof(Magnifier), new PropertyMetadata(new Point(0,0)));


        #endregion

        #endregion //Properties

        #region Constructors

        /// <summary>
        /// Initializes static members of the <see cref="Magnifier"/> class.
        /// </summary>
        static Magnifier()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Magnifier), new FrameworkPropertyMetadata(typeof(Magnifier)));
            HeightProperty.OverrideMetadata(typeof(Magnifier), new FrameworkPropertyMetadata(Magnifier.DEFAULT_SIZE));
            WidthProperty.OverrideMetadata(typeof(Magnifier), new FrameworkPropertyMetadata(Magnifier.DEFAULT_SIZE));
        }

        public Magnifier()
        {
            this.SizeChanged += new SizeChangedEventHandler(OnSizeChangedEvent);
        }

        private void OnSizeChangedEvent(object sender, SizeChangedEventArgs e)
        {
            UpdateViewBox();
        }

        private void UpdateSizeFromRadius()
        {
            if (this.FrameType == FrameType.Circle)
            {
                double newSize = Radius * 2;
                if (!AreVirtuallyEqual(Width, newSize))
                {
                    Width = newSize;
                }

                if (!AreVirtuallyEqual(Height, newSize))
                {
                    Height = newSize;
                }
            }
        }
        public static bool AreVirtuallyEqual(double d1, double d2)
        {
            if (double.IsPositiveInfinity(d1))
                return double.IsPositiveInfinity(d2);

            if (double.IsNegativeInfinity(d1))
                return double.IsNegativeInfinity(d2);

            if (Double.IsNaN(d1))
                return Double.IsNaN(d2);

            double n = d1 - d2;
            double d = (Math.Abs(d1) + Math.Abs(d2) + 10) * 1.0e-15;
            return (-d < n) && (d > n);
        }

        #endregion

        #region Base Class Overrides

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            VisualBrush newBrush = GetTemplateChild(PART_VisualBrush) as VisualBrush;

            // Just create a brush as placeholder even if there is no such brush.
            // This avoids having to "if" each access to the _visualBrush member.
            // Do not keep the current _visualBrush whatsoever to avoid memory leaks.
            if (newBrush == null)
            {
                newBrush = new VisualBrush();
            }

            newBrush.Viewbox = _visualBrush.Viewbox;
            _visualBrush = newBrush;
        }

        #endregion // Base Class Overrides

        #region Methods

        private void UpdateViewBox()
        {
            if (!IsInitialized)
                return;

            ViewBox = new Rect(
              ViewBox.Location,
              new Size(ActualWidth * ZoomFactor, ActualHeight * ZoomFactor));
        }

        #endregion //Methods
    }
    public class MagnifierAdorner : Adorner
    {
        #region Members

        private Magnifier _magnifier;
        private Point _currentMousePosition;

        #endregion

        #region Constructors

        public MagnifierAdorner(UIElement element, Magnifier magnifier)
          : base(element)
        {
            _magnifier = magnifier;
            UpdateViewBox();
            AddVisualChild(_magnifier);

            Loaded += (s, e) => InputManager.Current.PostProcessInput += OnProcessInput;
            Unloaded += (s, e) => InputManager.Current.PostProcessInput -= OnProcessInput;
        }

        #endregion

        #region Private/Internal methods

        private void OnProcessInput(object sender, ProcessInputEventArgs e)
        {
            Point pt = Mouse.GetPosition(this);

            if (_currentMousePosition == pt)
                return;

            _currentMousePosition = pt;
            UpdateViewBox();
            InvalidateArrange();
        }

        internal void UpdateViewBox()
        {
            var viewBoxLocation = CalculateViewBoxLocation();
            _magnifier.ViewBox = new Rect(viewBoxLocation, _magnifier.ViewBox.Size);
        }

        private Point CalculateViewBoxLocation()
        {
            double offsetX = 0, offsetY = 0;

            Point adorner = Mouse.GetPosition(this);
            Point element = Mouse.GetPosition(AdornedElement);

            offsetX = element.X - adorner.X;
            offsetY = element.Y - adorner.Y;

            //An element will use the offset from its parent (StackPanel, Grid, etc.) to be rendered.
            //When this element is put in a VisualBrush, the element will draw with that offset applied. 
            //To fix this: we add that parent offset to Magnifier location.
            Vector parentOffsetVector = VisualTreeHelper.GetOffset(_magnifier.Target);
            Point parentOffset = new Point(parentOffsetVector.X, parentOffsetVector.Y);

            double left = _currentMousePosition.X - ((_magnifier.ViewBox.Width / 2) + offsetX) + parentOffset.X;
            double top = _currentMousePosition.Y - ((_magnifier.ViewBox.Height / 2) + offsetY) + parentOffset.Y;
            return new Point(left, top);
        }

        #endregion

        #region Overrides

        protected override Visual GetVisualChild(int index)
        {
            return _magnifier;
        }

        protected override int VisualChildrenCount
        {
            get
            {
                return 1;
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _magnifier.Measure(constraint);
            return base.MeasureOverride(constraint);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = _currentMousePosition.X - (_magnifier.Width / 2) + _magnifier.MouseOffset.X;
            double y = _currentMousePosition.Y - (_magnifier.Height / 2) + _magnifier.MouseOffset.Y;
            _magnifier.Arrange(new Rect(x, y, _magnifier.Width, _magnifier.Height));
            return base.ArrangeOverride(finalSize);
        }

        #endregion
    }
    public class MagnifierManager : DependencyObject
    {
        #region Members

        private MagnifierAdorner _adorner;
        private UIElement _element;

        #endregion //Members

        #region Properties

        public static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached("Magnifier", typeof(Magnifier), typeof(UIElement), new FrameworkPropertyMetadata(null, OnMagnifierChanged));
        public static void SetMagnifier(UIElement element, Magnifier value)
        {
            element.SetValue(CurrentProperty, value);
        }
        public static Magnifier GetMagnifier(UIElement element)
        {
            return (Magnifier)element.GetValue(CurrentProperty);
        }

        private static void OnMagnifierChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            UIElement target = d as UIElement;

            if (target == null)
                throw new ArgumentException("Magnifier can only be attached to a UIElement.");

            MagnifierManager manager = new MagnifierManager();
            manager.AttachToMagnifier(target, e.NewValue as Magnifier);
        }

        #endregion //Properties

        #region Event Handlers

        void Element_MouseLeave(object sender, MouseEventArgs e)
        {
            HideAdorner();
        }

        void Element_MouseEnter(object sender, MouseEventArgs e)
        {
            ShowAdorner();
        }

        #endregion //Event Handlers

        #region Methods

        private void AttachToMagnifier(UIElement element, Magnifier magnifier)
        {
            _element = element;
            _element.MouseEnter += Element_MouseEnter;
            _element.MouseLeave += Element_MouseLeave;

            magnifier.Target = _element;

            _adorner = new MagnifierAdorner(_element, magnifier);
        }

        void ShowAdorner()
        {
            VerifyAdornerLayer();
            _adorner.Visibility = Visibility.Visible;
        }

        bool VerifyAdornerLayer()
        {
            if (_adorner.Parent != null)
                return true;

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(_element);
            if (layer == null)
                return false;

            layer.Add(_adorner);
            return true;
        }

        void HideAdorner()
        {
            if (_adorner.Visibility == Visibility.Visible)
            {
                _adorner.Visibility = Visibility.Collapsed;
            }
        }

        #endregion //Methods
    }
    public enum FrameType
    {
        Circle,
        Rectangle
    }
}
