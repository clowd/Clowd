using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


namespace DrawToolsLib
{
    /// <summary>
    /// Selection Rectangle graphics object, used for group selection.
    /// 
    /// Instance of this class should be created only for group selection
    /// and removed immediately after group selection finished.
    /// </summary>
    class GraphicsSelectionRectangle : GraphicsRectangleBase
    {
        #region Constructors

        public GraphicsSelectionRectangle(double left, double top, double right, double bottom, double actualScale)
        {
            this.rectangleLeft = left;
            this.rectangleTop = top;
            this.rectangleRight = right;
            this.rectangleBottom = bottom;
            this.graphicsLineWidth = 1.0;
            this.graphicsActualScale = actualScale;
        }

        public GraphicsSelectionRectangle()
            :
            this(0.0, 0.0, 100.0, 100.0, 1.0)
        {
        }

        #endregion Constructors

        #region Overrides

        /// <summary>
        /// Draw graphics object
        /// </summary>
        public override void Draw(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(
                null,
                new Pen(Brushes.White, ActualLineWidth),
                Rectangle);

            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);

            Pen dashedPen = new Pen(Brushes.Black, ActualLineWidth);
            dashedPen.DashStyle = dashStyle;


            drawingContext.DrawRectangle(
                null,
                dashedPen,
                Rectangle);
        }

        public override bool Contains(Point point)
        {
            return this.Rectangle.Contains(point);
        }

        public override PropertiesGraphicsBase CreateSerializedObject()
        {
            return null;        // not used
        }

        #endregion Overrides
    }
}
