using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.Generic;
using System.Diagnostics;



namespace DrawToolsLib
{
    /// <summary>
    /// Helper class which contains general helper functions and properties.
    /// 
    /// Most functions in this class replace VisualCollection-derived class
    /// methods, because I cannot derive from VisualCollection.
    /// They make different operations with GraphicsBase list.
    /// </summary>
    static class HelperFunctions
    {
        /// <summary>
        /// Default cursor
        /// </summary>
        public static Cursor DefaultCursor
        {
            get
            {
                return Cursors.Arrow;
            }
        }

        /// <summary>
        /// Select all graphic objects
        /// </summary>
        public static void SelectAll(DrawingCanvas drawingCanvas)
        {
            for(int i = 0; i < drawingCanvas.Count; i++)
            {
                drawingCanvas[i].IsSelected = true;
            }
        }

        /// <summary>
        /// Unselect all graphic objects
        /// </summary>
        public static void UnselectAll(DrawingCanvas drawingCanvas)
        {
            for (int i = 0; i < drawingCanvas.Count; i++)
            {
                drawingCanvas[i].IsSelected = false;
            }
        }

        /// <summary>
        /// Delete selected graphic objects
        /// </summary>
        public static void DeleteSelection(DrawingCanvas drawingCanvas)
        {
            CommandDelete command = new CommandDelete(drawingCanvas);
            bool wasChange = false;

            for (int i = drawingCanvas.Count - 1; i >= 0; i--)
            {
                if ( drawingCanvas[i].IsSelected )
                {
                    drawingCanvas.GraphicsList.RemoveAt(i);
                    wasChange = true;
                }
            }

            if ( wasChange )
            {
                drawingCanvas.AddCommandToHistory(command);
            }
        }

        /// <summary>
        /// Delete all graphic objects
        /// </summary>
        public static void DeleteAll(DrawingCanvas drawingCanvas)
        {
            if (drawingCanvas.GraphicsList.Count > 0 )
            {
                drawingCanvas.AddCommandToHistory(new CommandDeleteAll(drawingCanvas));

                drawingCanvas.GraphicsList.Clear();
            }

        }

        /// <summary>
        /// Move selection to front
        /// </summary>
        public static void MoveSelectionToFront(DrawingCanvas drawingCanvas)
        {
            // Moving to front of z-order means moving
            // to the end of VisualCollection.

            // Read GraphicsList in the reverse order, and move every selected object
            // to temporary list.

            List<GraphicsBase> list = new List<GraphicsBase>();

            CommandChangeOrder command = new CommandChangeOrder(drawingCanvas);

            for(int i = drawingCanvas.Count - 1; i >= 0; i--)
            {
                if ( drawingCanvas[i].IsSelected )
                {
                    list.Insert(0, drawingCanvas[i]);
                    drawingCanvas.GraphicsList.RemoveAt(i);
                }
            }

            // Add all items from temporary list to the end of GraphicsList
            foreach(GraphicsBase g in list)
            {
                drawingCanvas.GraphicsList.Add(g);
            }

            if ( list.Count > 0 )
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }
        }

        /// <summary>
        /// Move selection to back
        /// </summary>
        public static void MoveSelectionToBack(DrawingCanvas drawingCanvas)
        {
            // Moving to back of z-order means moving
            // to the beginning of VisualCollection.

            // Read GraphicsList in the reverse order, and move every selected object
            // to temporary list.

            List<GraphicsBase> list = new List<GraphicsBase>();

            CommandChangeOrder command = new CommandChangeOrder(drawingCanvas);

            for (int i = drawingCanvas.Count - 1; i >= 0; i--)
            {
                if (drawingCanvas[i].IsSelected)
                {
                    list.Add(drawingCanvas[i]);
                    drawingCanvas.GraphicsList.RemoveAt(i);
                }
            }

            // Add all items from temporary list to the beginning of GraphicsList
            foreach (GraphicsBase g in list)
            {
                drawingCanvas.GraphicsList.Insert(0, g);
            }

            if (list.Count > 0)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }
        }

        /// <summary>
        /// Apply new line width
        /// </summary>
        public static bool ApplyLineWidth(DrawingCanvas drawingCanvas, double value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;


            // LineWidth is set for all objects except of GraphicsText.
            // Though GraphicsText has this property, it should remain constant.

            foreach(GraphicsBase g in drawingCanvas.Selection)
            {
                if (g is GraphicsRectangle ||
                     g is GraphicsEllipse ||
                     g is GraphicsLine ||
                     g is GraphicsPolyLine)
                {
                    if ( g.LineWidth != value )
                    {
                        g.LineWidth = value;
                        wasChange = true;
                    }
                }
            }

            if (wasChange  && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new color
        /// </summary>
        public static bool ApplyColor(DrawingCanvas drawingCanvas, Color value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                if (g.ObjectColor != value)
                {
                    g.ObjectColor = value;
                    wasChange = true;
                }
            }

            if ( wasChange && addToHistory )
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new font family
        /// </summary>
        public static bool ApplyFontFamily(DrawingCanvas drawingCanvas, string value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                GraphicsText gt = g as GraphicsText;

                if (gt != null)
                {
                    if (gt.TextFontFamilyName != value)
                    {
                        gt.TextFontFamilyName = value;
                        wasChange = true;
                    }
                }
            }

            if (wasChange  && addToHistory )
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new font style
        /// </summary>
        public static bool ApplyFontStyle(DrawingCanvas drawingCanvas, FontStyle value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.GraphicsList)
            {
                if (g.IsSelected)
                {
                    GraphicsText gt = g as GraphicsText;

                    if (gt != null)
                    {
                        if (gt.TextFontStyle != value)
                        {
                            gt.TextFontStyle = value;
                            wasChange = true;
                        }
                    }
                }
            }

            if (wasChange  && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new font weight
        /// </summary>
        public static bool ApplyFontWeight(DrawingCanvas drawingCanvas, FontWeight value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                GraphicsText gt = g as GraphicsText;

                if (gt != null)
                {
                    if (gt.TextFontWeight != value)
                    {
                        gt.TextFontWeight = value;
                        wasChange = true;
                    }
                }
            }

            if (wasChange  && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new font stretch
        /// </summary>
        public static bool ApplyFontStretch(DrawingCanvas drawingCanvas, FontStretch value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                GraphicsText gt = g as GraphicsText;

                if (gt != null)
                {
                    if (gt.TextFontStretch != value)
                    {
                        gt.TextFontStretch = value;
                        wasChange = true;
                    }
                }
            }

            if (wasChange  && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        /// <summary>
        /// Apply new font size
        /// </summary>
        public static bool ApplyFontSize(DrawingCanvas drawingCanvas, double value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                GraphicsText gt = g as GraphicsText;

                if (gt != null)
                {
                    if (gt.TextFontSize != value)
                    {
                        gt.TextFontSize = value;
                        wasChange = true;
                    }
                }
            }

            if (wasChange && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }


        /// <summary>
        /// Dump graphics list (for debugging)
        /// </summary>
        [Conditional("DEBUG")]
        public static void Dump(VisualCollection graphicsList, string header)
        {
            Trace.WriteLine("");
            Trace.WriteLine(header);
            Trace.WriteLine("");

            foreach(GraphicsBase g in graphicsList)
            {
                g.Dump();
            }
        }

        /// <summary>
        /// Dump graphics list overload
        /// </summary>
        [Conditional("DEBUG")]
        public static void Dump(VisualCollection graphicsList)
        {
            Dump(graphicsList, "Graphics List");
        }

        /// <summary>
        /// Return true if currently active properties (line width, color etc.)
        /// can be applied to selected items.
        /// 
        /// If at least one selected object has property different from currently
        /// active property value, properties can be applied.
        /// </summary>
        public static bool CanApplyProperties(DrawingCanvas drawingCanvas)
        {
            foreach(GraphicsBase graphicsBase in drawingCanvas.GraphicsList)
            {
                if ( ! graphicsBase.IsSelected )
                {
                    continue;
                }

                // ObjectColor - used in all graphics objects
                if ( graphicsBase.ObjectColor != drawingCanvas.ObjectColor )
                {
                    return true;
                }

                GraphicsText graphicsText = graphicsBase as GraphicsText;

                if ( graphicsText == null )
                {
                    // LineWidth - used in all objects except of GraphicsText
                    if ( graphicsBase.LineWidth != drawingCanvas.LineWidth )
                    {
                        return true;
                    }
                }
                else
                {
                    // Font - for GraphicsText

                    if ( graphicsText.TextFontFamilyName != drawingCanvas.TextFontFamilyName )
                    {
                        return true;
                    }

                    if ( graphicsText.TextFontSize != drawingCanvas.TextFontSize )
                    {
                        return true;
                    }

                    if ( graphicsText.TextFontStretch != drawingCanvas.TextFontStretch )
                    {
                        return true;
                    }

                    if ( graphicsText.TextFontStyle != drawingCanvas.TextFontStyle )
                    {
                        return true;
                    }

                    if ( graphicsText.TextFontWeight != drawingCanvas.TextFontWeight )
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Apply currently active properties to selected objects
        /// </summary>
        public static void ApplyProperties(DrawingCanvas drawingCanvas)
        {
            // Apply every property.
            // Call every Apply* function with addToHistory = false.
            // History is updated here and not in called functions.

            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            // Line Width
            if ( ApplyLineWidth(drawingCanvas, drawingCanvas.LineWidth, false))
            {
                wasChange = true;
            }

            // Color
            if ( ApplyColor(drawingCanvas, drawingCanvas.ObjectColor, false) )
            {
                wasChange = true;
            }

            // Font properties
            if ( ApplyFontFamily(drawingCanvas, drawingCanvas.TextFontFamilyName, false) )
            {
                wasChange = true;
            }

            if ( ApplyFontSize(drawingCanvas, drawingCanvas.TextFontSize, false) )
            {
                wasChange = true;
            }

            if ( ApplyFontStretch(drawingCanvas, drawingCanvas.TextFontStretch, false) )
            {
                wasChange = true;
            }

            if ( ApplyFontStyle(drawingCanvas, drawingCanvas.TextFontStyle, false) )
            {
                wasChange = true;
            }

            if ( ApplyFontWeight(drawingCanvas, drawingCanvas.TextFontWeight, false) )
            {
                wasChange = true;
            }

            if ( wasChange )
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }
        }

    }
}
