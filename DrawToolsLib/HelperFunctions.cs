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

    internal static class HelperFunctions
    {
        public static Cursor DefaultCursor => Cursors.Arrow;

        public static bool ApplyProperty<T>(DrawingCanvas canvas, Func<T, bool> action, bool addToHistory, bool selectedOnly)
            where T : GraphicsBase
        {
            CommandChangeState command = new CommandChangeState(canvas);
            bool wasChange = false;
            foreach (GraphicsBase g in canvas.Selection)
            {
                if (g.IsSelected || selectedOnly)
                {
                    T gt = g as T;
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
            return false;
        }
        public static bool ApplyFontStyle(DrawingCanvas drawingCanvas, FontStyle value, bool addToHistory)
        {
            return false;
        }
        public static bool ApplyFontWeight(DrawingCanvas drawingCanvas, FontWeight value, bool addToHistory)
        {
            return false;
        }
        public static bool ApplyFontStretch(DrawingCanvas drawingCanvas, FontStretch value, bool addToHistory)
        {
            return false;
        }
        public static bool ApplyFontSize(DrawingCanvas drawingCanvas, double value, bool addToHistory)
        {
            return false;
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
