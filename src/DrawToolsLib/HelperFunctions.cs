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

        public static Rect CreateRectSafeRounded(double Left, double Top, double Right, double Bottom)
        {
            var r = CreateRectSafe(Left, Top, Right, Bottom);
            return new Rect(Math.Round(r.Left), Math.Round(r.Top), Math.Round(r.Width), Math.Round(r.Height));
        }
    }
}
