using System;
using System.IO;
using System.Reflection;
using System.Windows.Input;
namespace Clowd.Drawing;
internal partial class CursorResources : EmbeddedResource {
private const string RSX_NS = "Clowd.Drawing.Cursors";
public CursorResources() : base(Assembly.GetExecutingAssembly(), RSX_NS) { }
public static Cursor Default { get; } = new Cursor(GetStream(RSX_NS, "Default.cur"), true);
public static Cursor Rect { get; } = new Cursor(GetStream(RSX_NS, "Rect.cur"), true);
public static Cursor Ellipse { get; } = new Cursor(GetStream(RSX_NS, "Ellipse.cur"), true);
public static Cursor Line { get; } = new Cursor(GetStream(RSX_NS, "Line.cur"), true);
public static Cursor Arrow { get; } = new Cursor(GetStream(RSX_NS, "Arrow.cur"), true);
public static Cursor Text { get; } = new Cursor(GetStream(RSX_NS, "Text.cur"), true);
public static Cursor Numerical { get; } = new Cursor(GetStream(RSX_NS, "Numerical.cur"), true);
public static Cursor Pen { get; } = new Cursor(GetStream(RSX_NS, "Pen.cur"), true);
public static Cursor Rotate { get; } = new Cursor(GetStream(RSX_NS, "Rotate.cur"), true);
public static Cursor Obscure { get; } = new Cursor(GetStream(RSX_NS, "Obscure.cur"), true);
public static Cursor Move { get; } = new Cursor(GetStream(RSX_NS, "Move.cur"), true);
public static Cursor SizeAll { get; } = new Cursor(GetStream(RSX_NS, "SizeAll.cur"), true);
public static Cursor Size0 { get; } = new Cursor(GetStream(RSX_NS, "Size0.cur"), true);
public static Cursor Size1 { get; } = new Cursor(GetStream(RSX_NS, "Size1.cur"), true);
public static Cursor Size2 { get; } = new Cursor(GetStream(RSX_NS, "Size2.cur"), true);
public static Cursor Size3 { get; } = new Cursor(GetStream(RSX_NS, "Size3.cur"), true);
public static Cursor Size4 { get; } = new Cursor(GetStream(RSX_NS, "Size4.cur"), true);
public static Cursor Size5 { get; } = new Cursor(GetStream(RSX_NS, "Size5.cur"), true);
public static Cursor Size6 { get; } = new Cursor(GetStream(RSX_NS, "Size6.cur"), true);
public static Cursor Size7 { get; } = new Cursor(GetStream(RSX_NS, "Size7.cur"), true);
public static Cursor Size8 { get; } = new Cursor(GetStream(RSX_NS, "Size8.cur"), true);
public static Cursor Size9 { get; } = new Cursor(GetStream(RSX_NS, "Size9.cur"), true);
public static Cursor Size10 { get; } = new Cursor(GetStream(RSX_NS, "Size10.cur"), true);
public static Cursor Size11 { get; } = new Cursor(GetStream(RSX_NS, "Size11.cur"), true);
public static Cursor Size12 { get; } = new Cursor(GetStream(RSX_NS, "Size12.cur"), true);
public static Cursor Size13 { get; } = new Cursor(GetStream(RSX_NS, "Size13.cur"), true);
public static Cursor Size14 { get; } = new Cursor(GetStream(RSX_NS, "Size14.cur"), true);
public static Cursor Size15 { get; } = new Cursor(GetStream(RSX_NS, "Size15.cur"), true);
public static Cursor Size16 { get; } = new Cursor(GetStream(RSX_NS, "Size16.cur"), true);
public static Cursor Size17 { get; } = new Cursor(GetStream(RSX_NS, "Size17.cur"), true);
public static Cursor Size18 { get; } = new Cursor(GetStream(RSX_NS, "Size18.cur"), true);
public static Cursor Size19 { get; } = new Cursor(GetStream(RSX_NS, "Size19.cur"), true);
public static Cursor Size20 { get; } = new Cursor(GetStream(RSX_NS, "Size20.cur"), true);
public static Cursor Size21 { get; } = new Cursor(GetStream(RSX_NS, "Size21.cur"), true);
public static Cursor Size22 { get; } = new Cursor(GetStream(RSX_NS, "Size22.cur"), true);
public static Cursor Size23 { get; } = new Cursor(GetStream(RSX_NS, "Size23.cur"), true);
public static Cursor Size24 { get; } = new Cursor(GetStream(RSX_NS, "Size24.cur"), true);
public static Cursor Size25 { get; } = new Cursor(GetStream(RSX_NS, "Size25.cur"), true);
public static Cursor Size26 { get; } = new Cursor(GetStream(RSX_NS, "Size26.cur"), true);
public static Cursor Size27 { get; } = new Cursor(GetStream(RSX_NS, "Size27.cur"), true);
public static Cursor Size28 { get; } = new Cursor(GetStream(RSX_NS, "Size28.cur"), true);
public static Cursor Size29 { get; } = new Cursor(GetStream(RSX_NS, "Size29.cur"), true);
public static Cursor Size30 { get; } = new Cursor(GetStream(RSX_NS, "Size30.cur"), true);
public static Cursor Size31 { get; } = new Cursor(GetStream(RSX_NS, "Size31.cur"), true);
public static Cursor Size32 { get; } = new Cursor(GetStream(RSX_NS, "Size32.cur"), true);
public static Cursor Size33 { get; } = new Cursor(GetStream(RSX_NS, "Size33.cur"), true);
public static Cursor Size34 { get; } = new Cursor(GetStream(RSX_NS, "Size34.cur"), true);
public static Cursor Size35 { get; } = new Cursor(GetStream(RSX_NS, "Size35.cur"), true);
public static Cursor GetResizeCursor(int num) {
return num switch {
0 => Size0,
1 => Size1,
2 => Size2,
3 => Size3,
4 => Size4,
5 => Size5,
6 => Size6,
7 => Size7,
8 => Size8,
9 => Size9,
10 => Size10,
11 => Size11,
12 => Size12,
13 => Size13,
14 => Size14,
15 => Size15,
16 => Size16,
17 => Size17,
18 => Size18,
19 => Size19,
20 => Size20,
21 => Size21,
22 => Size22,
23 => Size23,
24 => Size24,
25 => Size25,
26 => Size26,
27 => Size27,
28 => Size28,
29 => Size29,
30 => Size30,
31 => Size31,
32 => Size32,
33 => Size33,
34 => Size34,
35 => Size35,
_ => throw new ArgumentOutOfRangeException(),
};
}
}
