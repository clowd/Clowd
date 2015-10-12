using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


namespace DrawToolsLib
{
    /// <summary>
    /// Polyline object properties
    /// </summary>
    public class PropertiesGraphicsPolyLine : PropertiesGraphicsBase
    {
        private Point[] points;
        private double lineWidth;
        private Color objectColor;


        public PropertiesGraphicsPolyLine()
        {

        }

        public PropertiesGraphicsPolyLine(GraphicsPolyLine polyLine)
        {
            if (polyLine == null)
            {
                throw new ArgumentNullException("polyLine");
            }


            points = polyLine.GetPoints();
            lineWidth = polyLine.LineWidth;
            objectColor = polyLine.ObjectColor;
            actualScale = polyLine.ActualScale;
            ID = polyLine.Id;
            selected = polyLine.IsSelected;
        }

        public override GraphicsBase CreateGraphics()
        {
            GraphicsBase b = new GraphicsPolyLine(points, lineWidth, objectColor, actualScale);

            if (this.ID != 0)
            {
                b.Id = this.ID;
                b.IsSelected = this.selected;
            }

            return b;

        }

        #region Properties


        /// <summary>
        /// Points
        /// </summary>
        public Point[] Points
        {
            get { return points; }
            set { points = value; }
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
