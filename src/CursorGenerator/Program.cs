using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;

namespace CursorGenerator;

internal class Program
{
    private delegate void DrawDelegate(float scale, int lineWidth, Graphics g);
    private delegate Point DrawSizeDelegate(float scale, int lineWidth, Graphics g);

    static Brush Stroke { get; set; } = Brushes.Black;

    static Brush Fill { get; set; } = Brushes.White;

    static string CursorFileDirectory { get; set; }

    static StringBuilder HtmlIndex { get; set; } = new StringBuilder();

    static StringBuilder CsProjItems { get; set; } = new StringBuilder();

    static StringBuilder CsEmbed { get; set; } = new StringBuilder();

    private static void Main(string[] args)
    {
        var di = new DirectoryInfo(AppContext.BaseDirectory);
        bool found = false;
        do
        {
            di = di.Parent;
            if (di.EnumerateFiles().Any(f => f.Name == "Clowd.sln"))
            {
                found = true;
                break;
            }
        } while (di.Parent != null);

        if (!found)
        {
            Console.WriteLine("Cant find sln");
            return;
        }

        CursorFileDirectory = Path.Combine(di.FullName, "src", "Clowd.Drawing", "Cursors");

        if (!Directory.Exists(CursorFileDirectory))
        {
            Console.WriteLine("Directory does not exist: " + CursorFileDirectory);
            return;
        }

        var sizes = new int[] { 32, 40, 48, 56, 64, 72, 128, 192, 256 };
        //sizes = new int[] { 32 };
        var angles = Enumerable.Range(0, 32).Select(x => x * 11.25f).ToArray();
        //angles = new float[] { 0, 22, 45, 60 };

        HtmlIndex.AppendLine("<html><body style=\"background-color: coral\">");
        //sb.AppendLine("<html><body>");

        CsEmbed.AppendLine("using System;");
        CsEmbed.AppendLine("using System.IO;");
        CsEmbed.AppendLine("using System.Reflection;");
        CsEmbed.AppendLine("using System.Windows.Input;");
        CsEmbed.AppendLine("namespace Clowd.Drawing;");
        CsEmbed.AppendLine("internal partial class CursorResources : EmbeddedResource {");
        CsEmbed.AppendLine("private const string RSX_NS = \"Clowd.Drawing.Cursors\";");
        CsEmbed.AppendLine("public CursorResources() : base(Assembly.GetExecutingAssembly(), RSX_NS) { }");

        CsProjItems.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        CsProjItems.AppendLine("<ItemGroup>");

        DrawSizes(sizes, "Default", DrawBaseCursor);
        DrawSizes(sizes, "Rect", DrawBaseCursor, DrawRect);
        DrawSizes(sizes, "Ellipse", DrawBaseCursor, DrawEllipse);
        DrawSizes(sizes, "Line", DrawBaseCursor, DrawLine);
        DrawSizes(sizes, "Arrow", DrawBaseCursor, DrawArrow);
        DrawSizes(sizes, "Text", DrawBaseCursor, DrawT);
        DrawSizes(sizes, "Numerical", DrawBaseCursor, DrawHash);
        DrawSizes(sizes, "Pen", DrawBaseCursor, DrawPen);
        DrawSizes(sizes, "Rotate", DrawRotate);
        DrawSizes(sizes, "Obscure", DrawBaseCursor, DrawObscure);
        DrawSizes(sizes, "Move", DrawBaseCursor, DrawResizeCursorSmall);
        DrawSizes(sizes, "SizeAll", (a1, a2, a3) => DrawResizeCursorNew(a1, a2, a3, 0, true));

        for (var i = 0; i < angles.Length; i++)
        {
            var f = angles[i];
            DrawSizes(sizes, "Size" + i, (a1, a2, a3) => DrawResizeCursorNew(a1, a2, a3, f));
        }


        CsEmbed.AppendLine("public static Cursor GetResizeCursor(int num) {");
        CsEmbed.AppendLine("return num switch {");

        for (var i = 0; i < angles.Length; i++)
        {
            var name = "Size" + i;
            CsEmbed.AppendLine($"{i} => {name},");
        }

        CsEmbed.AppendLine("_ => throw new ArgumentOutOfRangeException(),");
        CsEmbed.AppendLine("};");
        CsEmbed.AppendLine("}");

        CsEmbed.AppendLine("}");
        HtmlIndex.AppendLine("</body></html>");
        CsProjItems.AppendLine("</ItemGroup>");
        CsProjItems.AppendLine("</Project>");

        File.WriteAllText("index.html", HtmlIndex.ToString());
        File.WriteAllText(Path.Combine(CursorFileDirectory, "CursorResources.cs"), CsEmbed.ToString());
        File.WriteAllText(Path.Combine(CursorFileDirectory, "Cursors.targets"), CsProjItems.ToString());
    }

    static float floor(float p) => (float)Math.Floor(p);
    static float ceil(float p) => (float)Math.Ceiling(p);
    static float round(float p) => (float)Math.Round(p);
    static PointF mkpt(float x, float y) => new PointF(x, y);

    private static void DrawSizes(int[] sizes, string variation, params DrawDelegate[] stuff)
    {
        IconFile f = new IconFile();

        foreach (var size in sizes)
        {
            var bmp = new Bitmap(size, size);

            float scale = size / 32f;
            int lineWidth = scale <= 1.5 ? 1 : (int)Math.Round(scale);

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            foreach (var fn in stuff)
                fn(scale, lineWidth, g);

            var name = variation + "-" + size.ToString() + ".png";
            bmp.Save(name);

            HtmlIndex.AppendLine($"<div>{name}</div>");
            HtmlIndex.AppendLine($"<img src=\"{Path.GetFullPath(name)}\" alt=\"{name}\" />");

            var h = (ushort)floor(1 * scale);
            f.Add(bmp, h, h);
        }

        var fileName = variation + ".cur";
        var file = Path.Combine(CursorFileDirectory, fileName);
        f.Save(file, format: IconFile.FileFormat.Cur);
        CsEmbed.AppendLine("public static Cursor " + variation + " { get; } = new Cursor(GetStream(RSX_NS, \"" + fileName + "\"), true);");
        CsProjItems.AppendLine("<EmbeddedResource Include=\"Cursors\\" + fileName + "\" />");
    }

    private static void DrawSizes(int[] sizes, string variation, DrawSizeDelegate fn)
    {
        IconFile f = new IconFile();

        foreach (var size in sizes)
        {
            var bmp = new Bitmap(size, size);

            float scale = size / 32f;
            int lineWidth = scale <= 1.5 ? 1 : (int)Math.Round(scale);

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var hotspot = fn(scale, lineWidth, g);

            var name = variation + "-" + size.ToString() + ".png";
            bmp.Save(name);

            HtmlIndex.AppendLine($"<div>{name}</div>");
            HtmlIndex.AppendLine($"<img src=\"{Path.GetFullPath(name)}\" alt=\"{name}\" />");

            f.Add(bmp, (ushort)hotspot.X, (ushort)hotspot.Y);
        }

        var fileName = variation + ".cur";
        var file = Path.Combine(CursorFileDirectory, fileName);
        f.Save(file, format: IconFile.FileFormat.Cur);
        CsEmbed.AppendLine("public static Cursor " + variation + " { get; } = new Cursor(GetStream(RSX_NS, \"" + fileName + "\"), true);");
        CsProjItems.AppendLine("<EmbeddedResource Include=\"Cursors\\" + fileName + "\" />");
    }

    private static void DrawRect(float scale, int lineWidth, Graphics g)
    {
        using var pw = new Pen(Stroke, lineWidth);

        float off = (lineWidth % 2) == 0 ? 0.5f : 0;

        var r = new Rectangle(7, 16, 12, 9);

        var x = (float)Math.Round(r.X * scale) + off;
        var y = (float)Math.Round(r.Y * scale) + off;
        var width = (float)Math.Round(r.Width * scale);
        var height = (float)Math.Round(r.Height * scale);

        g.DrawRectangle(pw, x, y, width, height);

        if (lineWidth < 2)
        {
            using var pb = new Pen(Fill, lineWidth * 2);

            x += (1.5f * lineWidth);
            y += (1.5f * lineWidth);
            width -= (3 * lineWidth);
            height -= (3 * lineWidth);

            g.DrawRectangle(pb, x, y, width, height);
        }
        else
        {
            using var pb = new Pen(Fill, lineWidth);

            x += (1 * lineWidth);
            y += (1 * lineWidth);
            width -= (2 * lineWidth);
            height -= (2 * lineWidth);

            g.DrawRectangle(pb, x, y, width, height);
        }
    }

    private static void DrawEllipse(float scale, int lineWidth, Graphics g)
    {
        using var pw = new Pen(Stroke, lineWidth);

        float off = (lineWidth % 2) == 0 ? 0.5f : 0;

        var r = new Rectangle(5, 16, 16, 11);

        var x = (float)Math.Round(r.X * scale) + off;
        var y = (float)Math.Round(r.Y * scale) + off;
        var width = (float)Math.Round(r.Width * scale);
        var height = (float)Math.Round(r.Height * scale);

        g.DrawEllipse(pw, x, y, width, height);

        if (lineWidth < 2)
        {
            //g.DrawEllipse(pw, x, y, width, height);
            //g.DrawEllipse(pw, x, y, width, height);

            using var pb = new Pen(Fill, lineWidth * 2);

            x += (1.5f * lineWidth);
            y += (1.5f * lineWidth);
            width -= (3 * lineWidth);
            height -= (3.1f * lineWidth);

            g.DrawEllipse(pb, x, y, width, height);
        }
        else
        {
            using var pb = new Pen(Fill, lineWidth);

            x += (1 * lineWidth);
            y += (1 * lineWidth);
            width -= (2 * lineWidth);
            height -= (2 * lineWidth);

            g.DrawEllipse(pb, x, y, width, height);
        }
    }

    private static void DrawLine(float scale, int lineWidth, Graphics g)
    {
        using var pw2 = new Pen(Stroke, lineWidth * 2);
        using var pw3 = new Pen(Stroke, lineWidth * 3);
        using var pb = new Pen(Fill, lineWidth);
        pw2.StartCap = pb.StartCap = pw2.EndCap = pb.EndCap = LineCap.RoundAnchor;

        if (lineWidth < 2)
        {
            pw3.StartCap = pw3.EndCap = LineCap.RoundAnchor;
        }

        float off = (lineWidth % 2) == 0 ? 0 : 0.5f;
        var p1 = new PointF((float)Math.Round(6 * scale) + off, (float)Math.Round((32 - 5) * scale) + off);
        var p2 = new PointF((float)Math.Round(19 * scale) + off, (float)Math.Round(14 * scale) + off);

        g.DrawLine(pw2, p1, p2);
        g.DrawLine(pw3, p1, p2);
        g.DrawLine(pb, p1, p2);
    }

    private static void DrawArrow(float scale, int lineWidth, Graphics g)
    {
        using var pw = new Pen(Stroke, lineWidth * 4);
        using var pb = new Pen(Fill, lineWidth * 2);
        pw.EndCap = LineCap.Round;
        pb.EndCap = LineCap.Round;

        GraphicsPath gpb = new GraphicsPath();
        gpb.AddPolygon(new PointF[] { new(1, -1), new(0, 0), new(-1, -1) });
        pb.CustomStartCap = new CustomLineCap(null, gpb);

        GraphicsPath gpw = new GraphicsPath();
        gpw.AddPolygon(new PointF[] { new(0.5f, -0.5f), new(0, 0), new(-0.5f, -0.5f) });
        pw.CustomStartCap = new CustomLineCap(null, gpw);

        float off = (lineWidth % 2) == 0 ? 0 : 0.5f;

        var p1 = new PointF((float)Math.Round(6 * scale) + off, (float)Math.Round((32 - 5) * scale) + off);
        var p2 = new PointF((float)Math.Round(19 * scale) + off, (float)Math.Round(14 * scale) + off);

        //g.DrawLine(pw, p1, p2);
        g.DrawLine(pw, p1, p2);
        g.DrawLine(pb, p1, p2);
    }

    private static void DrawT(float scale, int lineWidth, Graphics g)
    {
        //var pts = new PointF[]
        //{
        //    new(0,0),
        //    new(0,3),
        //    new(1,3),
        //    new(1,2),
        //    new(6,2),
        //    new(6,13),
        //    new(5,13),
        //    new(5,14),
        //    new(9,14),
        //    new(9,13),
        //    new(8,13),
        //    new(8,2),
        //    new(13,2),
        //    new(13,3),
        //    new(14,3),
        //    new(14,0),
        //};

        float positionX = (float)Math.Floor(7 * scale);
        float positionY = (float)Math.Floor(15 * scale);
        float offset = lineWidth + 0.5f;

        // to solve some rounding errors, this is only the left half of the shape
        var pts = new PointF[]
        {
            new(0,0),
            new(0,3),
            new(1,3),
            new(1,2),
            new(6,2),
            new(6,13),
            new(5,13),
            new(5,14),
            new(6,14),
        };

        // we first scale and round the first half
        var adjusted = pts.Select((p, i) => new PointF(
            (float)Math.Floor(p.X * scale),
            (float)Math.Floor(p.Y * scale)
        )).ToList();

        // then we invert the X coordinate and add the second half of the shape
        var max = (float)Math.Floor(14 * scale);
        for (int i = adjusted.Count - 1; i >= 0; i--)
        {
            var pt = adjusted[i];
            adjusted.Add(new PointF(max - pt.X, pt.Y));
        }

        // add offsets to all the points
        var arr = adjusted.Select((p, i) => new PointF(
            p.X + offset + positionX,
            p.Y + offset + positionY
        )).ToArray();

        using var pen = new Pen(Stroke, lineWidth * 2);
        g.DrawPolygon(pen, arr);
        g.FillPolygon(Fill, arr);
    }

    private static void DrawHash(float scale, int lineWidth, Graphics g)
    {
        PointF translate(float x, float y) => new PointF((float)Math.Round(x * scale) + 0.5f, (float)Math.Round(y * scale) + 0.5f);

        using GraphicsPath gp = new GraphicsPath();
        gp.AddLine(translate(9, 20), translate(20, 20));
        gp.CloseFigure();
        gp.AddLine(translate(9, 25), translate(20, 25));
        gp.CloseFigure();
        gp.AddLine(translate(13, 17), translate(11, 28));
        gp.CloseFigure();
        gp.AddLine(translate(18, 17), translate(16, 28));

        var p = new Pen(Stroke, lineWidth * 4);
        p.LineJoin = LineJoin.Round;
        p.SetLineCap(LineCap.Round, LineCap.Round, DashCap.Round);

        var p2 = new Pen(Fill, lineWidth * 2);
        p2.LineJoin = LineJoin.Round;
        p2.SetLineCap(LineCap.Round, LineCap.Round, DashCap.Round);

        g.DrawPath(p, gp);
        g.DrawPath(p2, gp);
    }

    private static void DrawRotate(float scale, int lineWidth, Graphics g)
    {
        float offset = (lineWidth % 2) == 0 ? 0 : 0.5f;
        var max = floor(scale * 32);
        var margin = ceil(scale * 8);
        var headY = margin - (scale * 1.5f) - (lineWidth / 2); //- (lineWidth * 2f) + offset;
        var headSize = ceil(scale * 9);
        var center = round(scale * 17f);

        var rect = new RectangleF(margin, margin + offset, max - margin * 2, max - margin * 2);

        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var p1 = new Pen(Stroke, scale * 3 + (lineWidth * 2));
        g.DrawEllipse(p1, rect);
        //g.DrawEllipse(p1, rect);
        //g.DrawEllipse(p1, rect);

        using var p2 = new Pen(Fill, scale * 3);
        g.DrawEllipse(p2, rect);


        using var p3 = new Pen(Stroke, lineWidth);
        var headPts = new PointF[] { new(center + headSize - offset, headY), new(center - offset, headY), new(center - offset, headY + headSize) };
        g.FillPolygon(Fill, headPts);
        g.DrawPolygon(p3, headPts);

        g.Clip = new Region(new RectangleF(center + lineWidth * 3, 0, max, max));
        g.DrawEllipse(p2, rect);
    }

    private static void DrawObscure(float scale, int lineWidth, Graphics g)
    {
        var size = floor(4 * scale);
        var initial = new PointF(floor(9 * scale), floor(17 * scale));

        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        bool white = false;
        for (float x = initial.X; x < size * 3 + initial.X; x += size)
        {
            for (float y = initial.Y; y < size * 3 + initial.Y; y += size)
            {
                g.FillRectangle(white ? Stroke : Fill, new RectangleF(x, y, size, size));
                white = !white;
            }
        }

        var p = new Pen(Stroke, lineWidth);

        if ((lineWidth % 2) == 0)
            g.DrawRectangle(p, initial.X - 1, initial.Y - 1, size * 3 + 2, size * 3 + 2);
        else
            g.DrawRectangle(p, initial.X - 0.5f, initial.Y - 0.5f, size * 3 + 1, size * 3 + 1);
    }

    private static void DrawPen(float scale, int lineWidth, Graphics g)
    {
        PointF translate(float x, float y) => new PointF(x, y);

        using GraphicsPath gp = new GraphicsPath();
        gp.AddRectangle(new RectangleF(0, 0, 3, 2));
        gp.CloseFigure();
        gp.AddRectangle(new RectangleF(0, 3, 3, 1));
        gp.CloseFigure();

        gp.AddPolygon(new PointF[] {
            translate(0, 5),
            translate(0, 17),
            translate(1.5f, 18.5f),
            translate(3, 17),
            translate(3, 5),
        });

        gp.Transform(new Matrix());

        Matrix myMatrix = new Matrix();
        myMatrix.RotateAt(45, translate(2.5f * scale, 9 * scale));
        myMatrix.Translate(17 * scale, 0 * scale);
        myMatrix.Scale(scale, scale);

        gp.Transform(myMatrix);

        using var p1 = new Pen(Stroke, lineWidth * 2);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);
        g.FillPath(Fill, gp);
    }

    private static void DrawResizeCursorSmall(float scale, int lineWidth, Graphics g) => DrawResizeCursorSmall(scale, lineWidth, g, 0, true);

    private static void DrawResizeCursorSmall(float scale, int lineWidth, Graphics g, float angle, bool draw90 = true)
    {
        float offset = (lineWidth % 2) == 0 ? 0 : 0.5f;

        var margin = floor(8 * scale);
        var center = round(16 * scale) - offset;
        var max = floor(32 * scale);
        var halfLine = lineWidth / 2f;
        var headWidthHalf = ceil(2 * scale);
        var headHeight = ceil(2 * scale);

        var ptsAuto = new PointF[] {
            // left half of top arrow
            mkpt(center, margin),
            mkpt(center - halfLine - headWidthHalf, margin + headHeight + lineWidth),
            mkpt(center - halfLine, margin + headHeight + lineWidth),

            // bottom arrow
            mkpt(center - halfLine, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center - halfLine - headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center, max - margin - lineWidth),
            mkpt(center + halfLine + headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center + halfLine, max - margin - headHeight - lineWidth - lineWidth),

            // right half of top arrow
            mkpt(center + halfLine, margin + headHeight + lineWidth),
            mkpt(center + halfLine + headWidthHalf, margin + headHeight + lineWidth),
        };

        var tx = floor(-4 * scale);
        var ty = floor(8 * scale);

        PointF[] CopyWithRotate(float ang)
        {
            var p = ptsAuto.ToArray();
            var m = new Matrix();

            if (scale > 1.5)
            {
                m.RotateAt(ang, new PointF(center, center - halfLine));
            }
            else
            {
                m.RotateAt(ang, new PointF(center, center));
            }

            m.TransformPoints(p);
            return p.Select(p => new PointF(p.X + tx, p.Y + ty)).ToArray();
        }

        using GraphicsPath gp = new GraphicsPath(FillMode.Winding);
        gp.AddPolygon(CopyWithRotate(angle));
        gp.CloseFigure();

        if (draw90)
        {
            gp.AddPolygon(CopyWithRotate(angle + 90));
            gp.CloseFigure();
        }

        if ((angle % 90) == 0)
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var p1 = new Pen(Stroke, Math.Max(lineWidth, 2));
        p1.LineJoin = LineJoin.Round;

        g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);

        g.FillPath(Fill, gp);
    }

    private static Point DrawResizeCursorNew(float scale, int lineWidth, Graphics g, float angle, bool draw90 = false)
    {
        float offset = (lineWidth % 2) == 0 ? 0 : 0.5f;

        var margin = floor(3 * scale);
        var center = round(16 * scale) - offset;
        var max = floor(32 * scale);
        var halfLine = lineWidth / 2f;
        var headWidthHalf = ceil(4 * scale);
        var headHeight = ceil(4 * scale);

        //var ptsAuto = new PointF[] {
        //    // left half of top arrow
        //    mkpt(center - halfLine, margin),
        //    mkpt(center - halfLine - headWidthHalf, margin + headHeight),
        //    mkpt(center - halfLine - headWidthHalf, margin + headHeight + lineWidth),
        //    mkpt(center - halfLine, margin + headHeight + lineWidth),

        //    // bottom arrow
        //    mkpt(center - halfLine, max - margin - headHeight - lineWidth - lineWidth),
        //    mkpt(center - halfLine - headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
        //    mkpt(center - halfLine - headWidthHalf, max - margin - headHeight - lineWidth),
        //    mkpt(center - halfLine, max - margin - lineWidth),
        //    mkpt(center + halfLine, max - margin - lineWidth),
        //    mkpt(center + halfLine + headWidthHalf, max - margin - headHeight - lineWidth),
        //    mkpt(center + halfLine + headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
        //    mkpt(center + halfLine, max - margin - headHeight - lineWidth - lineWidth),

        //    // right half of top arrow
        //    mkpt(center + halfLine, margin + headHeight + lineWidth),
        //    mkpt(center + halfLine + headWidthHalf, margin + headHeight + lineWidth),
        //    mkpt(center + halfLine + headWidthHalf, margin + headHeight),
        //    mkpt(center + halfLine, margin),
        //};

        var ptsAuto = new PointF[] {
            // left half of top arrow
            mkpt(center, margin),
            mkpt(center - halfLine - headWidthHalf, margin + headHeight + lineWidth),
            mkpt(center - halfLine, margin + headHeight + lineWidth),

            // bottom arrow
            mkpt(center - halfLine, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center - halfLine - headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center, max - margin - lineWidth),
            mkpt(center + halfLine + headWidthHalf, max - margin - headHeight - lineWidth - lineWidth),
            mkpt(center + halfLine, max - margin - headHeight - lineWidth - lineWidth),

            // right half of top arrow
            mkpt(center + halfLine, margin + headHeight + lineWidth),
            mkpt(center + halfLine + headWidthHalf, margin + headHeight + lineWidth),
        };

        PointF[] CopyWithRotate(float ang)
        {
            var p = ptsAuto.ToArray();
            var m = new Matrix();

            if (scale > 1.5)
            {
                m.RotateAt(ang, new PointF(center, center - halfLine));
            }
            else
            {
                m.RotateAt(ang, new PointF(center, center));
            }

            m.TransformPoints(p);
            return p;
        }

        using GraphicsPath gp = new GraphicsPath(FillMode.Winding);
        gp.AddPolygon(CopyWithRotate(angle));
        gp.CloseFigure();

        if (draw90)
        {
            gp.AddPolygon(CopyWithRotate(angle + 90));
            gp.CloseFigure();
        }

        if ((angle % 90) == 0)
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var p1 = new Pen(Stroke, Math.Max(lineWidth, 2));
        p1.LineJoin = LineJoin.Round;

        g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);
        //g.DrawPath(p1, gp);

        g.FillPath(Fill, gp);

        if (scale > 1.5)
        {
            //g.FillRectangle(Brushes.Red, new RectangleF(center, center - halfLine, 1, 1));
            return new Point((int)center, (int)(center - halfLine));
        }
        else
        {
            //g.FillRectangle(Brushes.Red, new RectangleF(center - halfLine, center - halfLine, 1, 1));
            return new Point((int)(center - halfLine), (int)(center - halfLine));
        }
    }

    //private static void DrawCursorPlus(float scale, int lineWidth, Graphics g)
    //{
    //    float offset = (lineWidth % 2) == 0 ? 0.5f : 0;
    //    PointF translate(float x, float y) => new PointF((float)Math.Ceiling(x * scale) + offset, (float)Math.Ceiling(y * scale) + offset);

    //    using var p1 = new Pen(Foreground, lineWidth);
    //    p1.SetLineCap(LineCap.Round, LineCap.Round, DashCap.Round);

    //    g.DrawLine(p1, translate(4, 7), translate(4, 11));
    //    g.DrawLine(p1, translate(2, 9), translate(6, 9));
    //}

    private static void DrawBaseCursor(float scale, int lineWidth, Graphics g)
    {

        float lineOffset = lineWidth > 1 ? lineWidth / 2f : 0;

        lineOffset += (lineWidth % 2) == 0 ? 0.5f : 0;


        using var p = new Pen(Stroke, lineWidth);
        p.LineJoin = LineJoin.Round;
        p.SetLineCap(LineCap.Round, LineCap.Round, DashCap.Round);

        var pts = new PointF[] { new(13, 13), new(0, 0), new(0, 18), new(5.5f, 13) }
            .Select(p => new PointF((float)Math.Round(p.X * scale) + lineOffset, (float)Math.Round(p.Y * scale) + lineOffset))
            .ToArray();

        var b = new LinearGradientBrush(new Rectangle(0, 0, (int)floor(32 * scale), (int)floor(32 * scale)), Color.White, Color.FromArgb(200, 200, 200), 0f);

        g.FillPolygon(b, pts);
        g.DrawPolygon(p, pts);

        //if (lineWidth < 2)
        //{  // darken the line at 1px
        //    g.DrawPolygon(p, pts);
        //    g.DrawPolygon(p, pts);
        //}
    }
}
