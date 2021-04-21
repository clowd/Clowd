using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DrawToolsLib
{
    internal class OriginIndicator : DrawingVisual
    {
        public OriginIndicator()
        {
            this.Effect = new DropShadowEffect()
            {
                BlurRadius = 1,
                Color = Color.FromArgb(128, 0, 0, 0),
                ShadowDepth = 0,
            };
            using (var ctx = this.RenderOpen())
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), 1);
                ctx.DrawLine(pen, new Point(0, -10), new Point(0, 10));
                ctx.DrawLine(pen, new Point(-10, 0), new Point(10, 0));
            }
        }
    }
}
