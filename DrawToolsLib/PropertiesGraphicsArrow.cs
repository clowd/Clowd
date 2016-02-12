using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    /// Arrow object properties
    /// </summary>
    public class PropertiesGraphicsArrow : PropertiesGraphicsBase
    {
        private Point start;
        private Point end;
        private double lineWidth;
        private Color objectColor;

        public PropertiesGraphicsArrow()
        {

        }

        public PropertiesGraphicsArrow(GraphicsArrow arrow)
        {
            if ( arrow == null )
            {
                throw new ArgumentNullException(nameof(arrow));
            }

            start = arrow.Start;
            end = arrow.End;
            lineWidth = arrow.LineWidth;
            objectColor = arrow.ObjectColor;
            actualScale = arrow.ActualScale;
            ID = arrow.Id;
            selected = arrow.IsSelected;
        }

        public override GraphicsBase CreateGraphics()
        {
            GraphicsBase b = new GraphicsArrow(start, end, lineWidth, objectColor, actualScale);

            if (this.ID != 0)
            {
                b.Id = this.ID;
                b.IsSelected = this.selected;
            }

            return b;
        }

        #region Properties

        /// <summary>
        /// Start Point
        /// </summary>
        public Point Start
        {
            get { return start; }
            set { start = value; }
        }

        /// <summary>
        /// End Point
        /// </summary>
        public Point End
        {
            get { return end; }
            set { end = value; }
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
