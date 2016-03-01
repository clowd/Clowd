using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    ///  Rectangle graphics object.
    /// </summary>
    public class GraphicsRectangle : GraphicsRectangleBase
    {
        #region Constructors

        public GraphicsRectangle(double left, double top, double right, double bottom,
            double lineWidth, Color objectColor, double actualScale)
        {
            this.rectangleLeft = left;
            this.rectangleTop = top;
            this.rectangleRight = right;
            this.rectangleBottom = bottom;
            this.graphicsLineWidth = lineWidth;
            this.graphicsObjectColor = objectColor;
            this.graphicsActualScale = actualScale;

            //RefreshDrawng();
        }

        public GraphicsRectangle()
            :
            this(0.0, 0.0, 100.0, 100.0, 1.0, Colors.Black, 1.0)
        {
        }

        #endregion Constructors

        #region Overrides

        /// <summary>
        /// Draw object
        /// </summary>
        public override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                throw new ArgumentNullException("drawingContext");
            }

            drawingContext.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(ObjectColor), ActualLineWidth),
                Rectangle);

            base.Draw(drawingContext);
        }

        /// <summary>
        /// Test whether object contains point
        /// </summary>
        public override bool Contains(Point point)
        {
            return this.Rectangle.Contains(point);
        }

        /// <summary>
        /// Serialization support
        /// </summary>
        public override PropertiesGraphicsBase CreateSerializedObject()
        {
            return new PropertiesGraphicsRectangle(this);
        }


        #endregion Overrides

    }
}
