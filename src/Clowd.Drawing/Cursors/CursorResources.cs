using System;
using System.Windows.Input;

namespace Clowd.Drawing;

internal partial class CursorResources {
    public static Cursor Default => GetCursor("Default.cur");
    public static Cursor Rect => GetCursor("Rect.cur");
    public static Cursor Ellipse => GetCursor("Ellipse.cur");
    public static Cursor Line => GetCursor("Line.cur");
    public static Cursor Arrow => GetCursor("Arrow.cur");
    public static Cursor Text => GetCursor("Text.cur");
    public static Cursor Numerical => GetCursor("Numerical.cur");
    public static Cursor Pen => GetCursor("Pen.cur");
    public static Cursor Rotate => GetCursor("Rotate.cur");
    public static Cursor Obscure => GetCursor("Obscure.cur");
    public static Cursor Move => GetCursor("Move.cur");
    public static Cursor SizeAll => GetCursor("SizeAll.cur");
    public static Cursor Size0 => GetCursor("Size0.cur");
    public static Cursor Size1 => GetCursor("Size1.cur");
    public static Cursor Size2 => GetCursor("Size2.cur");
    public static Cursor Size3 => GetCursor("Size3.cur");
    public static Cursor Size4 => GetCursor("Size4.cur");
    public static Cursor Size5 => GetCursor("Size5.cur");
    public static Cursor Size6 => GetCursor("Size6.cur");
    public static Cursor Size7 => GetCursor("Size7.cur");
    public static Cursor Size8 => GetCursor("Size8.cur");
    public static Cursor Size9 => GetCursor("Size9.cur");
    public static Cursor Size10 => GetCursor("Size10.cur");
    public static Cursor Size11 => GetCursor("Size11.cur");
    public static Cursor Size12 => GetCursor("Size12.cur");
    public static Cursor Size13 => GetCursor("Size13.cur");
    public static Cursor Size14 => GetCursor("Size14.cur");
    public static Cursor Size15 => GetCursor("Size15.cur");
    public static Cursor Size16 => GetCursor("Size16.cur");
    public static Cursor Size17 => GetCursor("Size17.cur");
    public static Cursor Size18 => GetCursor("Size18.cur");
    public static Cursor Size19 => GetCursor("Size19.cur");
    public static Cursor Size20 => GetCursor("Size20.cur");
    public static Cursor Size21 => GetCursor("Size21.cur");
    public static Cursor Size22 => GetCursor("Size22.cur");
    public static Cursor Size23 => GetCursor("Size23.cur");
    public static Cursor Size24 => GetCursor("Size24.cur");
    public static Cursor Size25 => GetCursor("Size25.cur");
    public static Cursor Size26 => GetCursor("Size26.cur");
    public static Cursor Size27 => GetCursor("Size27.cur");
    public static Cursor Size28 => GetCursor("Size28.cur");
    public static Cursor Size29 => GetCursor("Size29.cur");
    public static Cursor Size30 => GetCursor("Size30.cur");
    public static Cursor Size31 => GetCursor("Size31.cur");
    public static Cursor Size32 => GetCursor("Size32.cur");
    public static Cursor Size33 => GetCursor("Size33.cur");
    public static Cursor Size34 => GetCursor("Size34.cur");
    public static Cursor Size35 => GetCursor("Size35.cur");
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
