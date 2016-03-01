using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using DrawToolsLib.Annotations;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public abstract class GraphicsBase : INotifyPropertyChanged, ICloneable
    {
        [XmlIgnore]
        public int ObjectId
        {
            get { return _objectId; }
            protected set { _objectId = value; }
        }
        [XmlIgnore]
        public double ActualScale
        {
            get { return _actualScale; }
            internal set
            {
                if (value.Equals(_actualScale)) return;
                _actualScale = value;
                OnPropertyChanged(nameof(ActualScale));
                OnPropertyChanged(nameof(ActualLineWidth));
            }
        }
        public Color ObjectColor
        {
            get { return _objectColor; }
            set
            {
                if (value.Equals(_objectColor)) return;
                _objectColor = value;
                OnPropertyChanged(nameof(ObjectColor));
            }
        }
        public double LineWidth
        {
            get { return _lineWidth; }
            set
            {
                if (value.Equals(_lineWidth)) return;
                _lineWidth = value;
                OnPropertyChanged(nameof(LineWidth));
                OnPropertyChanged(nameof(ActualLineWidth));
            }
        }
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (value == _isSelected) return;
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        [XmlIgnore]
        public double ActualLineWidth => ActualScale <= 0 ? LineWidth : LineWidth / ActualScale;
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler Invalidated;

        private int _objectId;
        private Rect _bounds;
        private double _actualScale;
        private Color _objectColor;
        private double _lineWidth;
        private bool _isSelected;

        [XmlIgnore]
        protected double LineHitTestWidth => Math.Max(8.0, ActualLineWidth);
        [XmlIgnore]
        protected const double HitTestWidth = 12.0;
        [XmlIgnore]
        internal static double HandleSize { get; set; } = 12.0;
        [XmlIgnore]
        internal static SolidColorBrush HandleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0, 0, 255));
        [XmlIgnore]
        protected static SolidColorBrush HandleBrush2 { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        protected GraphicsBase()
        {
            ObjectId = this.GetHashCode();
        }
        protected GraphicsBase(DrawingCanvas canvas)
            : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth)
        {
        }
        protected GraphicsBase(double scale, Color objectColor, double lineWidth)
            : this()
        {
            ActualScale = scale;
            ObjectColor = objectColor;
            LineWidth = lineWidth;
        }

        [XmlIgnore]
        public abstract Rect Bounds { get; }
        [XmlIgnore]
        internal abstract int HandleCount { get; }

        internal abstract bool Contains(Point point);
        internal abstract Point GetHandle(int handleNumber);
        internal abstract int MakeHitTest(Point point);
        internal abstract bool IntersectsWith(Rect rectangle);
        internal abstract void Move(double deltaX, double deltaY);
        internal abstract void MoveHandleTo(Point point, int handleNumber);
        internal abstract Cursor GetHandleCursor(int handleNumber);

        internal virtual void Normalize()
        {
            // Empty implementation is OK for classes which don't require normalization, like line.
        }
        internal virtual void Draw(DrawingContext drawingContext)
        {
            if (IsSelected)
            {
                DrawTracker(drawingContext);
            }
        }
        internal virtual void DrawTracker(DrawingContext drawingContext)
        {
            for (int i = 1; i <= HandleCount; i++)
            {
                var rectangle = GetHandleRectangle(i);
                drawingContext.DrawEllipse(HandleBrush, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 1, rectangle.Height / 2 - 1);
                drawingContext.DrawEllipse(HandleBrush2, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 2, rectangle.Height / 2 - 2);
                drawingContext.DrawEllipse(HandleBrush, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 3, rectangle.Height / 2 - 3);
            }
        }

        internal virtual GraphicsVisual CreateVisual()
        {
            // clear event handler, so if there are any un-disposed GraphicsVisual's we are de-referencing them.
            Invalidated = delegate { };

            var vis =  new GraphicsVisual(this);
            this.InvalidateVisual();
            return vis;
        }

        protected virtual Rect GetHandleRectangle(int handleNumber)
        {
            Point point = GetHandle(handleNumber);

            // Handle rectangle should have constant size, except of the case
            // when line is too width.
            double size = Math.Max(HandleSize / ActualScale, ActualLineWidth * 1.1);

            return new Rect(point.X - size / 2, point.Y - size / 2,
                size, size);
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            InvalidateVisual();
        }
        protected virtual void InvalidateVisual()
        {
            Invalidated?.Invoke(this, new EventArgs());
        }

        public abstract GraphicsBase Clone();
        object ICloneable.Clone()
        {
            return Clone();
        }
    }
}
