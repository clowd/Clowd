using System;
using System.IO;
using System.Reflection;
using System.Windows.Input;

namespace Clowd.Drawing
{
    internal class Resource : EmbeddedResource
    {
        public Stream Resize00 => GetStream("Resize0.cur");
        public Stream Resize01 => GetStream("Resize1.cur");
        public Stream Resize02 => GetStream("Resize2.cur");
        public Stream Resize03 => GetStream("Resize3.cur");
        public Stream Resize04 => GetStream("Resize4.cur");
        public Stream Resize05 => GetStream("Resize5.cur");
        public Stream Resize06 => GetStream("Resize6.cur");
        public Stream Resize07 => GetStream("Resize7.cur");
        public Stream Resize08 => GetStream("Resize8.cur");
        public Stream Resize09 => GetStream("Resize9.cur");
        public Stream Resize10 => GetStream("Resize10.cur");
        public Stream Resize11 => GetStream("Resize11.cur");
        public Stream Resize12 => GetStream("Resize12.cur");
        public Stream Resize13 => GetStream("Resize13.cur");
        public Stream Resize14 => GetStream("Resize14.cur");
        public Stream Resize15 => GetStream("Resize15.cur");
        public Stream Resize16 => GetStream("Resize16.cur");
        public Stream Resize17 => GetStream("Resize17.cur");
        public Stream Resize18 => GetStream("Resize18.cur");
        public Stream Resize19 => GetStream("Resize19.cur");
        public Stream Resize20 => GetStream("Resize20.cur");
        public Stream Resize21 => GetStream("Resize21.cur");
        public Stream Resize22 => GetStream("Resize22.cur");
        public Stream Resize23 => GetStream("Resize23.cur");
        public Stream Resize24 => GetStream("Resize24.cur");
        public Stream Resize25 => GetStream("Resize25.cur");
        public Stream Resize26 => GetStream("Resize26.cur");
        public Stream Resize27 => GetStream("Resize27.cur");
        public Stream Resize28 => GetStream("Resize28.cur");
        public Stream Resize29 => GetStream("Resize29.cur");
        public Stream Resize30 => GetStream("Resize30.cur");
        public Stream Resize31 => GetStream("Resize31.cur");
        public Stream Resize32 => GetStream("Resize32.cur");
        public Stream Resize33 => GetStream("Resize33.cur");
        public Stream Resize34 => GetStream("Resize34.cur");
        public Stream Resize35 => GetStream("Resize35.cur");
        public Stream Arrow => GetStream("Arrow.cur");
        public Stream Ellipse => GetStream("Ellipse.cur");
        public Stream Line => GetStream("Line.cur");
        public Stream Pencil => GetStream("Pencil.cur");
        public Stream PolyHandle => GetStream("PolyHandle.cur");
        public Stream Rectangle => GetStream("Rectangle.cur");
        public Stream Rotate => GetStream("Rotate.cur");
        public Stream Text => GetStream("Text.cur");
        public Stream ResizePng => GetStream("Resize.png");
        public Stream ResizeSvg => GetStream("Resize.svg");

        private const string RSX_NS = "Clowd.Drawing.Images";
        public static Cursor CursorResize00 { get; } = new Cursor(GetStream(RSX_NS, "Resize0.cur"), true);
        public static Cursor CursorResize01 { get; } = new Cursor(GetStream(RSX_NS, "Resize1.cur"), true);
        public static Cursor CursorResize02 { get; } = new Cursor(GetStream(RSX_NS, "Resize2.cur"), true);
        public static Cursor CursorResize03 { get; } = new Cursor(GetStream(RSX_NS, "Resize3.cur"), true);
        public static Cursor CursorResize04 { get; } = new Cursor(GetStream(RSX_NS, "Resize4.cur"), true);
        public static Cursor CursorResize05 { get; } = new Cursor(GetStream(RSX_NS, "Resize5.cur"), true);
        public static Cursor CursorResize06 { get; } = new Cursor(GetStream(RSX_NS, "Resize6.cur"), true);
        public static Cursor CursorResize07 { get; } = new Cursor(GetStream(RSX_NS, "Resize7.cur"), true);
        public static Cursor CursorResize08 { get; } = new Cursor(GetStream(RSX_NS, "Resize8.cur"), true);
        public static Cursor CursorResize09 { get; } = new Cursor(GetStream(RSX_NS, "Resize9.cur"), true);
        public static Cursor CursorResize10 { get; } = new Cursor(GetStream(RSX_NS, "Resize10.cur"), true);
        public static Cursor CursorResize11 { get; } = new Cursor(GetStream(RSX_NS, "Resize11.cur"), true);
        public static Cursor CursorResize12 { get; } = new Cursor(GetStream(RSX_NS, "Resize12.cur"), true);
        public static Cursor CursorResize13 { get; } = new Cursor(GetStream(RSX_NS, "Resize13.cur"), true);
        public static Cursor CursorResize14 { get; } = new Cursor(GetStream(RSX_NS, "Resize14.cur"), true);
        public static Cursor CursorResize15 { get; } = new Cursor(GetStream(RSX_NS, "Resize15.cur"), true);
        public static Cursor CursorResize16 { get; } = new Cursor(GetStream(RSX_NS, "Resize16.cur"), true);
        public static Cursor CursorResize17 { get; } = new Cursor(GetStream(RSX_NS, "Resize17.cur"), true);
        public static Cursor CursorResize18 { get; } = new Cursor(GetStream(RSX_NS, "Resize18.cur"), true);
        public static Cursor CursorResize19 { get; } = new Cursor(GetStream(RSX_NS, "Resize19.cur"), true);
        public static Cursor CursorResize20 { get; } = new Cursor(GetStream(RSX_NS, "Resize20.cur"), true);
        public static Cursor CursorResize21 { get; } = new Cursor(GetStream(RSX_NS, "Resize21.cur"), true);
        public static Cursor CursorResize22 { get; } = new Cursor(GetStream(RSX_NS, "Resize22.cur"), true);
        public static Cursor CursorResize23 { get; } = new Cursor(GetStream(RSX_NS, "Resize23.cur"), true);
        public static Cursor CursorResize24 { get; } = new Cursor(GetStream(RSX_NS, "Resize24.cur"), true);
        public static Cursor CursorResize25 { get; } = new Cursor(GetStream(RSX_NS, "Resize25.cur"), true);
        public static Cursor CursorResize26 { get; } = new Cursor(GetStream(RSX_NS, "Resize26.cur"), true);
        public static Cursor CursorResize27 { get; } = new Cursor(GetStream(RSX_NS, "Resize27.cur"), true);
        public static Cursor CursorResize28 { get; } = new Cursor(GetStream(RSX_NS, "Resize28.cur"), true);
        public static Cursor CursorResize29 { get; } = new Cursor(GetStream(RSX_NS, "Resize29.cur"), true);
        public static Cursor CursorResize30 { get; } = new Cursor(GetStream(RSX_NS, "Resize30.cur"), true);
        public static Cursor CursorResize31 { get; } = new Cursor(GetStream(RSX_NS, "Resize31.cur"), true);
        public static Cursor CursorResize32 { get; } = new Cursor(GetStream(RSX_NS, "Resize32.cur"), true);
        public static Cursor CursorResize33 { get; } = new Cursor(GetStream(RSX_NS, "Resize33.cur"), true);
        public static Cursor CursorResize34 { get; } = new Cursor(GetStream(RSX_NS, "Resize34.cur"), true);
        public static Cursor CursorResize35 { get; } = new Cursor(GetStream(RSX_NS, "Resize35.cur"), true);
        public static Cursor CursorArrow { get; } = new Cursor(GetStream(RSX_NS, "Arrow.cur"), true);
        public static Cursor CursorEllipse { get; } = new Cursor(GetStream(RSX_NS, "Ellipse.cur"), true);
        public static Cursor CursorLine { get; } = new Cursor(GetStream(RSX_NS, "Line.cur"), true);
        public static Cursor CursorPencil { get; } = new Cursor(GetStream(RSX_NS, "Pencil.cur"), true);
        public static Cursor CursorPolyHandle { get; } = new Cursor(GetStream(RSX_NS, "PolyHandle.cur"), true);
        public static Cursor CursorRectangle { get; } = new Cursor(GetStream(RSX_NS, "Rectangle.cur"), true);
        public static Cursor CursorRotate { get; } = new Cursor(GetStream(RSX_NS, "Rotate.cur"), true);
        public static Cursor CursorText { get; } = new Cursor(GetStream(RSX_NS, "Text.cur"), true);

        public static Cursor GetResizeCursor(int num)
        {
            return num switch
            {
                00 => CursorResize00,
                01 => CursorResize01,
                02 => CursorResize02,
                03 => CursorResize03,
                04 => CursorResize04,
                05 => CursorResize05,
                06 => CursorResize06,
                07 => CursorResize07,
                08 => CursorResize08,
                09 => CursorResize09,
                10 => CursorResize10,
                11 => CursorResize11,
                12 => CursorResize12,
                13 => CursorResize13,
                14 => CursorResize14,
                15 => CursorResize15,
                16 => CursorResize16,
                17 => CursorResize17,
                18 => CursorResize18,
                19 => CursorResize19,
                20 => CursorResize20,
                21 => CursorResize21,
                22 => CursorResize22,
                23 => CursorResize23,
                24 => CursorResize24,
                25 => CursorResize25,
                26 => CursorResize26,
                27 => CursorResize27,
                28 => CursorResize28,
                29 => CursorResize29,
                30 => CursorResize30,
                31 => CursorResize31,
                32 => CursorResize32,
                33 => CursorResize33,
                34 => CursorResize34,
                35 => CursorResize35,
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        public Resource() : base(Assembly.GetExecutingAssembly(), "Clowd.Drawing.Images")
        { }
    }
}
