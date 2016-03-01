using System;
using System.Windows.Media;

namespace DrawToolsLib
{
    [Serializable]
    public class PropertiesGraphicsEllipse : PropertiesGraphicsBase
    {
        private double left;
        private double top;
        private double right;
        private double bottom;
        private double lineWidth;
        private Color objectColor;

        public PropertiesGraphicsEllipse()
        {
        }

        public PropertiesGraphicsEllipse(GraphicsEllipse ellipse)
        {
            if (ellipse == null)
            {
                throw new ArgumentNullException("ellipse");
            }

            left = ellipse.Left;
            top = ellipse.Top;
            right = ellipse.Right;
            bottom = ellipse.Bottom;

            lineWidth = ellipse.LineWidth;
            objectColor = ellipse.ObjectColor;
            ActualScale = ellipse.ActualScale;
            ID = ellipse.Id;
            Selected = ellipse.IsSelected;
        }

        public override GraphicsBase CreateGraphics()
        {
            GraphicsBase b =  new GraphicsEllipse(left, top, right, bottom, lineWidth, objectColor, ActualScale);

            if ( this.ID != 0 )
            {
                b.Id = this.ID;
                b.IsSelected = this.Selected;
            }

            return b;
        }

        #region Properties

        /// <summary>
        /// Left bounding rectangle side, X
        /// </summary>
        public double Left
        {
            get { return left; }
            set { left = value; }
        }

        /// <summary>
        /// Top bounding rectangle side, Y
        /// </summary>
        public double Top
        {
            get { return top; }
            set { top = value; }
        }

        /// <summary>
        /// Right bounding rectangle side, X
        /// </summary>
        public double Right
        {
            get { return right; }
            set { right = value; }
        }

        /// <summary>
        /// Bottom bounding rectangle side, Y
        /// </summary>
        public double Bottom
        {
            get { return bottom; }
            set { bottom = value; }
        }

        /// <summary>
        /// Line Width
        /// </summary>
        public double LineWidth
        {
            get { return lineWidth; }
            set { lineWidth = value; }
        }

        /// <summary>
        /// Color
        /// </summary>
        public Color ObjectColor
        {
            get { return objectColor; }
            set { objectColor = value; }
        }

        #endregion Properties
    }
}
