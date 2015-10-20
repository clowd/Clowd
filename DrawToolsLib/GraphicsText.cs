using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    ///  Text graphics object.
    /// </summary>
    public class GraphicsText : GraphicsRectangleBase
    {
        #region Class Members

        private string text;
        private string textFontFamilyName;
        private FontStyle textFontStyle;
        private FontWeight textFontWeight;
        private FontStretch textFontStretch;
        private double textFontSize;

        // For internal use
        FormattedText formattedText;

        #endregion Class Members

        #region Constructors

        public GraphicsText(
            string text,
            double left, 
            double top, 
            double right, 
            double bottom,
            Color objectColor,
            double textFontSize,
            string textFontFamilyName,
            FontStyle textFontStyle,
            FontWeight textFontWeight,
            FontStretch textFontStretch,
            double actualScale)
        {
            this.text = text;
            this.rectangleLeft = left;
            this.rectangleTop = top;
            this.rectangleRight = right;
            this.rectangleBottom = bottom;
            this.graphicsObjectColor = objectColor;
            this.textFontSize = textFontSize;
            this.textFontFamilyName = textFontFamilyName;
            this.textFontStyle = textFontStyle;
            this.textFontWeight = textFontWeight;
            this.textFontStretch = textFontStretch;
            this.graphicsActualScale = actualScale;

            graphicsLineWidth = 2;      // used for drawing bounding rectangle when selected


            //RefreshDrawng();
        }

        public GraphicsText()
            :
            this(Properties.Settings.Default.UnknownText, 0, 0, 0, 0, Colors.Black, 12, 
                 Properties.Settings.Default.DefaultFontFamily, FontStyles.Normal,
                 FontWeights.Normal, FontStretches.Normal, 1.0)
        {
        }

        #endregion Constructors

        #region Properties

        public string Text
        {
            get 
            { 
                return text; 
            }
            set 
            { 
                text = value;
                RefreshDrawing();
            }
        }

        public string TextFontFamilyName
        {
            get 
            { 
                return textFontFamilyName; 
            }
            set 
            { 
                textFontFamilyName = value;
                RefreshDrawing();
            }
        }

        public FontStyle TextFontStyle
        {
            get 
            { 
                return textFontStyle; 
            }
            set 
            { 
                textFontStyle = value;
                RefreshDrawing();
            }
        }

        public FontWeight TextFontWeight
        {
            get 
            { 
                return textFontWeight; 
            }
            set 
            { 
                textFontWeight = value;
                RefreshDrawing();
            }
        }

        public FontStretch TextFontStretch
        {
            get 
            { 
                return textFontStretch; 
            }
            set 
            { 
                textFontStretch = value;
                RefreshDrawing();
            }
        }

        public double TextFontSize
        {
            get 
            { 
                return textFontSize; 
            }
            set 
            { 
                textFontSize = value;
                RefreshDrawing();
            }
        }

        #endregion Properties

        #region Overrides

        /// <summary>
        /// Draw text
        /// </summary>
        public override void Draw(DrawingContext drawingContext)
        {
            if ( drawingContext == null )
            {
                throw new ArgumentNullException("drawingContext");
            }

            CreateFormattedText();

            Rect rect = Rectangle;

            drawingContext.PushClip(new RectangleGeometry(rect));

            drawingContext.DrawRectangle(Brushes.LightYellow, new Pen(Brushes.Black, 1), rect);
            drawingContext.DrawText(formattedText, new Point(rect.Left, rect.Top));

            drawingContext.Pop();

            if (IsSelected)
            {
                drawingContext.DrawRectangle(
                    null,
                    new Pen(new SolidColorBrush(graphicsObjectColor), ActualLineWidth),
                    rect);
            }

            // Draw tracker
            base.Draw(drawingContext);
        }

        /// <summary>
        /// Update rectangle to fit actual text size.
        /// </summary>
        public void UpdateRectangle()
        {
            /*
             * 
             * Trying to find acceptable adjustment algorithm, I decided finally
             * to leave rectangle completely under user control.
            
            CreateFormattedText();

            Rect rect = Rectangle;

            if (formattedText.Width > rect.Width)
            {
                formattedText.MaxTextWidth = rect.Width;
            }
            else
            {
                rect.Width = formattedText.Width;
            }

            if (rect.Height > formattedText.Height)
            {
                rect.Height = formattedText.Height;
            }

            this.left = rect.Left;
            this.top = rect.Top;
            this.right = rect.Right;
            this.bottom = rect.Bottom;
             */

            RefreshDrawing();
             
        }

        /// <summary>
        /// Create formatted text.
        /// It is required for drawing and updating bounding rectangle.
        /// </summary>
        void CreateFormattedText()
        {
            // Number of corrections I have done after trying to open
            // XML file with correct object names, but incorrect field names.
            if ( String.IsNullOrEmpty(textFontFamilyName) )
            {
                textFontFamilyName = Properties.Settings.Default.DefaultFontFamily;
            }

            if (text == null)
            {
                text = "";
            }

            if ( textFontSize <= 0.0 )
            {
                textFontSize = 12.0;
            }

            Typeface typeface = new Typeface(
                new FontFamily(textFontFamilyName),
                textFontStyle,
                textFontWeight,
                textFontStretch);

            formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                textFontSize,
                new SolidColorBrush(graphicsObjectColor));

            formattedText.MaxTextWidth = Rectangle.Width;
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
            return new PropertiesGraphicsText(this);
        }

        #endregion Overrides
    }
}
