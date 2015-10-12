using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    /// Line object properties
    /// </summary>
    public class PropertiesGraphicsLine : PropertiesGraphicsBase
    {
        private Point start;
        private Point end;
        private double lineWidth;
        private Color objectColor;

        public PropertiesGraphicsLine()
        {

        }

        public PropertiesGraphicsLine(GraphicsLine line)
        {
            if ( line == null )
            {
                throw new ArgumentNullException("line");
            }

            start = line.Start;
            end = line.End;
            lineWidth = line.LineWidth;
            objectColor = line.ObjectColor;
            actualScale = line.ActualScale;
            ID = line.Id;
            selected = line.IsSelected;
        }

        public override GraphicsBase CreateGraphics()
        {
            GraphicsBase b = new GraphicsLine(start, end, lineWidth, objectColor, actualScale);

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
