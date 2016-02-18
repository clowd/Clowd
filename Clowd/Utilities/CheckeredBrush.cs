using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using PropertyChanged;

namespace Clowd.Utilities
{
    [ImplementPropertyChanged]
    [MarkupExtensionReturnType(typeof(Brush))]
    public class CheckeredBrush : MarkupExtension
    {
        public double BlockSize { get; set; } = 25d;
        public Brush Background { get; set; } = Brushes.Black;
        public Brush Foreground { get; set; } = Brushes.White;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var half = BlockSize/2;
            var brush = new DrawingBrush();
            brush.Stretch = Stretch.None;
            brush.TileMode = TileMode.Tile;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            brush.Viewport = new Rect(0, 0, BlockSize, BlockSize);
            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing(Background, null, new RectangleGeometry(new Rect(0, 0, BlockSize, BlockSize))));
            var geometryGroup = new GeometryGroup();
            geometryGroup.Children.Add(new RectangleGeometry(new Rect(0, 0, half, half)));
            geometryGroup.Children.Add(new RectangleGeometry(new Rect(half, half, half, half)));
            drawingGroup.Children.Add(new GeometryDrawing(Foreground, null, geometryGroup));
            brush.Drawing = drawingGroup;
            brush.Freeze();
            return brush;
        }
    }
}
