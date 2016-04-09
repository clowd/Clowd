using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using System.Windows.Media.Effects;
using System.Xml.Serialization;
using DrawToolsLib.Annotations;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public abstract class GraphicsBase : INotifyPropertyChanged, ICloneable
    {
        [XmlIgnore]
        public Effect Effect
        {
            get { return _effect; }
            protected set
            {
                OnPropertyChanged(nameof(Effect));
                _effect = value;
            }
        }

        [XmlIgnore]
        public int ObjectId
        {
            get { return _objectId; }
            protected set { _objectId = value; }
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

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler Invalidated;

        private int _objectId;
        private Color _objectColor;
        private double _lineWidth;
        private bool _isSelected;
        private Effect _effect;

        [XmlIgnore]
        protected double LineHitTestWidth => Math.Max(8.0, LineWidth);
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
            _effect = new DropShadowEffect() { Opacity = 0.5, ShadowDepth = 2, RenderingBias = RenderingBias.Performance };
        }
        protected GraphicsBase(DrawingCanvas canvas)
            : this(canvas.ObjectColor, canvas.LineWidth)
        {
        }
        protected GraphicsBase(Color objectColor, double lineWidth)
            : this()
        {
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

        internal virtual void DrawDashedBorder(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), LineWidth),
                    Bounds);
            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);
            Pen dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), LineWidth);
            dashedPen.DashStyle = dashStyle;
            drawingContext.DrawRectangle(null,
                dashedPen,
                Bounds);
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

            var vis = new GraphicsVisual(this);
            this.InvalidateVisual();
            return vis;
        }

        protected virtual Rect GetHandleRectangle(int handleNumber)
        {
            Point point = GetHandle(handleNumber);

            // Handle rectangle should have constant size, except of the case
            // when line is too width.
            double size = Math.Max(HandleSize, LineWidth * 1.1);

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
