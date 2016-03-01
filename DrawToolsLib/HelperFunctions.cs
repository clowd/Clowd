using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.Generic;
using System.Diagnostics;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    /// <summary>
    /// Helper class which contains general helper functions and properties.
    /// 
    /// Most functions in this class replace VisualCollection-derived class
    /// methods, because I cannot derive from VisualCollection.
    /// They make different operations with GraphicsBase list.
    /// </summary>
    internal static class HelperFunctions
    {
        /// <summary>
        /// Default cursor
        /// </summary>
        public static Cursor DefaultCursor => Cursors.Arrow;

        public static void SelectAll(DrawingCanvas drawingCanvas)
        {
            for (int i = 0; i < drawingCanvas.Count; i++)
            {
                drawingCanvas[i].IsSelected = true;
            }
        }

        public static void UnselectAll(DrawingCanvas drawingCanvas)
        {
            for (int i = 0; i < drawingCanvas.Count; i++)
            {
                drawingCanvas[i].IsSelected = false;
            }
        }

        public static void DeleteSelection(DrawingCanvas drawingCanvas)
        {
            CommandDelete command = new CommandDelete(drawingCanvas);
            bool wasChange = false;

            for (int i = drawingCanvas.Count - 1; i >= 0; i--)
            {
                if (drawingCanvas[i].IsSelected)
                {
                    drawingCanvas.GraphicsList.RemoveAt(i);
                    wasChange = true;
                }
            }

            if (wasChange)
            {
                drawingCanvas.AddCommandToHistory(command);
            }
        }

        public static void DeleteAll(DrawingCanvas drawingCanvas)
        {
            if (drawingCanvas.GraphicsList.Count > 0)
            {
                drawingCanvas.AddCommandToHistory(new CommandDeleteAll(drawingCanvas));

                drawingCanvas.GraphicsList.Clear();
            }

        }

        public static void MoveSelectionToFront(DrawingCanvas drawingCanvas)
        {
            // Moving to front of z-order means moving
            // to the end of VisualCollection.

            // Read GraphicsList in the reverse order, and move every selected object
            // to temporary list.

            List<GraphicsVisual> list = new List<GraphicsVisual>();

            CommandChangeOrder command = new CommandChangeOrder(drawingCanvas);

            for (int i = drawingCanvas.Count - 1; i >= 0; i--)
            {
                if (drawingCanvas[i].IsSelected)
                {
                    list.Insert(0, drawingCanvas[i]);
                    drawingCanvas.GraphicsList.RemoveAt(i);
                }
            }

            // Add all items from temporary list to the end of GraphicsList
            foreach (GraphicsVisual g in list)
            {
                drawingCanvas.GraphicsList.Add(g);
            }

            if (list.Count > 0)
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

            List<GraphicsVisual> list = new List<GraphicsVisual>();

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
            foreach (GraphicsVisual g in list)
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

            foreach (GraphicsVisual g in drawingCanvas.Selection)
            {
                if (g.Graphic is GraphicsText || g.Graphic is GraphicsSelectionRectangle)
                    continue;
                if (g.LineWidth != value)
                {
                    g.LineWidth = value;
                    wasChange = true;
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
        /// Apply new color
        /// </summary>
        public static bool ApplyColor(DrawingCanvas drawingCanvas, Color value, bool addToHistory)
        {
            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            foreach (GraphicsVisual g in drawingCanvas.Selection)
            {
                if (g.ObjectColor != value)
                {
                    g.ObjectColor = value;
                    wasChange = true;
                }
            }

            if (wasChange && addToHistory)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }

            return wasChange;
        }

        public static bool ApplyProperty<T>(DrawingCanvas canvas, Func<T, bool> action, bool addToHistory, bool selectedOnly)
            where T : GraphicsBase
        {
            CommandChangeState command = new CommandChangeState(canvas);
            bool wasChange = false;
            foreach (GraphicsVisual g in canvas.Selection)
            {
                if (g.IsSelected || selectedOnly)
                {
                    T gt = g.Graphic as T;
                    if (gt != null)
                    {
                        wasChange = action(gt);
                    }
                }
            }

            if (wasChange && addToHistory)
            {
                command.NewState(canvas);
                canvas.AddCommandToHistory(command);
            }
            return wasChange;
        }

        public static bool ApplyFontFamily(DrawingCanvas drawingCanvas, string value, bool addToHistory)
        {
            return ApplyProperty<GraphicsText>(drawingCanvas,
                (text) =>
                {
                    if (text.FontFamily == value) return false;
                    text.FontFamily = value;
                    return true;
                },
                addToHistory, false);
        }
        public static bool ApplyFontStyle(DrawingCanvas drawingCanvas, FontStyle value, bool addToHistory)
        {
            return ApplyProperty<GraphicsText>(drawingCanvas,
                (text) =>
                {
                    var con = FontConversions.FontStyleToString(value);
                    if (text.FontStyle == con) return false;
                    text.FontStyle = con;
                    return true;
                }, addToHistory, false);
        }
        public static bool ApplyFontWeight(DrawingCanvas drawingCanvas, FontWeight value, bool addToHistory)
        {
            return ApplyProperty<GraphicsText>(drawingCanvas,
                (text) =>
                {
                    var con = FontConversions.FontWeightToString(value);
                    if (text.FontWeight == con) return false;
                    text.FontWeight = con;
                    return true;
                }, addToHistory, false);
        }
        public static bool ApplyFontStretch(DrawingCanvas drawingCanvas, FontStretch value, bool addToHistory)
        {
            return ApplyProperty<GraphicsText>(drawingCanvas,
                (text) =>
                {
                    var con = FontConversions.FontStretchToString(value);
                    if (text.FontStretch == con) return false;
                    text.FontStretch = con;
                    return true;
                }, addToHistory, false);
        }

        public static bool ApplyFontSize(DrawingCanvas drawingCanvas, double value, bool addToHistory)
        {
            return ApplyProperty<GraphicsText>(drawingCanvas,
                (text) =>
                {
                    if ((int) text.FontSize == (int) value)
                        return false;
                    text.FontSize = value;
                    return true;
                }, 
                addToHistory, false);
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
            foreach (GraphicsVisual graphicsBase in drawingCanvas.GraphicsList)
            {
                if (!graphicsBase.IsSelected)
                    continue;

                if (graphicsBase.ObjectColor != drawingCanvas.ObjectColor)
                    return true;

                GraphicsText graphicsText = graphicsBase.Graphic as GraphicsText;
                if (graphicsText == null)
                {
                    // LineWidth - used in all objects except of GraphicsText
                    if (graphicsBase.LineWidth != drawingCanvas.LineWidth)
                    {
                        return true;
                    }
                }
                else
                {
                    // Font - for GraphicsText
                    if (graphicsText.FontFamily != drawingCanvas.TextFontFamilyName)
                        return true;
                    if (graphicsText.FontSize != drawingCanvas.TextFontSize)
                        return true;
                    if (graphicsText.FontStretch != FontConversions.FontStretchToString(drawingCanvas.TextFontStretch))
                        return true;
                    if (graphicsText.FontStyle != FontConversions.FontStyleToString(drawingCanvas.TextFontStyle))
                        return true;
                    if (graphicsText.FontWeight != FontConversions.FontWeightToString(drawingCanvas.TextFontWeight))
                        return true;
                }
            }

            return false;
        }

        public static void ApplyProperties(DrawingCanvas drawingCanvas)
        {
            // Apply every property.
            // Call every Apply* function with addToHistory = false.
            // History is updated here and not in called functions.

            CommandChangeState command = new CommandChangeState(drawingCanvas);
            bool wasChange = false;

            if (ApplyLineWidth(drawingCanvas, drawingCanvas.LineWidth, false))
                wasChange = true;

            if (ApplyColor(drawingCanvas, drawingCanvas.ObjectColor, false))
                wasChange = true;

            if (ApplyFontFamily(drawingCanvas, drawingCanvas.TextFontFamilyName, false))
                wasChange = true;

            if (ApplyFontSize(drawingCanvas, drawingCanvas.TextFontSize, false))
                wasChange = true;

            if (ApplyFontStretch(drawingCanvas, drawingCanvas.TextFontStretch, false))
                wasChange = true;

            if (ApplyFontStyle(drawingCanvas, drawingCanvas.TextFontStyle, false))
                wasChange = true;

            if (ApplyFontWeight(drawingCanvas, drawingCanvas.TextFontWeight, false))
                wasChange = true;

            if (wasChange)
            {
                command.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(command);
            }
        }

        public static Rect CreateRectSafe(double Left, double Top, double Right, double Bottom)
        {
            double l, t, w, h;

            if (Left <= Right)
            {
                l = Left;
                w = Right - Left;
            }
            else
            {
                l = Right;
                w = Left - Right;
            }

            if (Top <= Bottom)
            {
                t = Top;
                h = Bottom - Top;
            }
            else
            {
                t = Bottom;
                h = Top - Bottom;
            }

            return new Rect(l, t, w, h);
        }

    }
}
