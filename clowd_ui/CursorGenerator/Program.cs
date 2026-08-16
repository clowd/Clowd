using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace CursorGenerator;

internal class Program
{
    private delegate void DrawDelegate(float scale, int lineWidth, Graphics g);
    private delegate Point DrawSizeDelegate(float scale, int lineWidth, Graphics g);

    static Brush Stroke { get; set; } = Brushes.Black;

    static Brush Fill { get; set; } = Brushes.White;

    static string CursorFileDirectory { get; set; }

    static string PreviewDirectory { get; set; }

    static StringBuilder HtmlIndex { get; set; } = new StringBuilder();

    static StringBuilder CsEmbed { get; set; } = new StringBuilder();

    private static void Main(string[] args)
    {
        var di = new DirectoryInfo(AppContext.BaseDirectory);
        bool found = false;
        do
        {
            di = di.Parent;
            if (di.EnumerateFiles().Any(f => f.Name == "Clowd.slnx"))
            {
                found = true;
                break;
            }
        } while (di.Parent != null);

        if (!found)
        {
            Console.WriteLine("Cant find Clowd.slnx");
            return;
        }

        CursorFileDirectory = Path.Combine(di.FullName, "clowd_ui", "Clowd.Drawing", "Cursors");

        if (!Directory.Exists(CursorFileDirectory))
        {
            Console.WriteLine("Directory does not exist: " + CursorFileDirectory);
            return;
        }

        PreviewDirectory = Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(PreviewDirectory);

        var sizes = new int[] { 32, 40, 48, 56, 64, 72, 128, 192, 256 };
        //sizes = new int[] { 32 };

        var numAngles = 36;
        var angles = Enumerable.Range(0, numAngles).Select(x => x * 5).ToArray();
        //angles = new float[] { 0, 22, 45, 60 };

        HtmlIndex.AppendLine("<html><body style=\"background-color: coral\">");

        CsEmbed.AppendLine("using System;");
        CsEmbed.AppendLine("using Avalonia.Input;");
        CsEmbed.AppendLine();
        CsEmbed.AppendLine("namespace Clowd.Drawing;");
        CsEmbed.AppendLine();
        CsEmbed.AppendLine("public partial class CursorResources {");

        DrawSizes(sizes, "Default", DrawBaseCursor);
        DrawSizes(sizes, "Rect", DrawBaseCursor, DrawRect);
        DrawSizes(sizes, "Ellipse", DrawBaseCursor, DrawEllipse);
        DrawSizes(sizes, "Line", DrawBaseCursor, DrawLine);
        DrawSizes(sizes, "Arrow", DrawBaseCursor, DrawArrow);
        DrawSizes(sizes, "Measure", DrawBaseCursor, DrawRuler);
        DrawSizes(sizes, "Text", DrawBaseCursor, DrawT);
        DrawSizes(sizes, "Numerical", DrawBaseCursor, DrawHash);
        DrawSizes(sizes, "Pen", DrawBaseCursor, DrawPen);
        DrawSizes(sizes, "Rotate", DrawRotate);
        DrawSizes(sizes, "Obscure", DrawBaseCursor, DrawObscure);
        DrawSizes(sizes, "Move", DrawBaseCursor, DrawResizeCursorSmall);
        DrawSizes(sizes, "SizeAll", (a1, a2, a3) => DrawResizeCursorNew(a1, a2, a3, 0, true));

        DrawSizes(sizes, "Grab", DrawGrabHand);
        DrawSizes(sizes, "Grabbing", DrawGrabbingHand);
        DrawSizes(sizes, "ColResize", DrawColResize);

        for (var i = 0; i < angles.Length; i++)
        {
            var f = angles[i];
            DrawSizes(sizes, "Size" + i, (a1, a2, a3) => DrawResizeCursorNew(a1, a2, a3, f));
        }

        CsEmbed.AppendLine("    public static Cursor GetResizeCursor(int num) {");
        CsEmbed.AppendLine("        return num switch {");
        for (var i = 0; i < angles.Length; i++)
            CsEmbed.AppendLine($"          {i} => Size{i},");
        CsEmbed.AppendLine("          _ => throw new ArgumentOutOfRangeException(),");
        CsEmbed.AppendLine("        };");
        CsEmbed.AppendLine("    }");
        CsEmbed.AppendLine("}");

        HtmlIndex.AppendLine("</body></html>");

        File.WriteAllText(Path.Combine(PreviewDirectory, "index.html"), HtmlIndex.ToString());
        File.WriteAllText(Path.Combine(CursorFileDirectory, "CursorResources.Table.cs"), CsEmbed.ToString());

        Console.WriteLine("Cursors written to: " + CursorFileDirectory);
        Console.WriteLine("Preview written to: " + Path.Combine(PreviewDirectory, "index.html"));
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
            bmp.Save(Path.Combine(PreviewDirectory, name));

            HtmlIndex.AppendLine($"<div>{name}</div>");
            HtmlIndex.AppendLine($"<img src=\"{name}\" alt=\"{name}\" />");

            var h = (ushort)floor(1 * scale);
            f.Add(bmp, h, h);
        }

        var fileName = variation + ".cur";
        var file = Path.Combine(CursorFileDirectory, fileName);
        f.Save(file, format: IconFile.FileFormat.Cur);
        CsEmbed.AppendLine("    public static Cursor " + variation + " => GetCursor(\"" + fileName + "\");");
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
            bmp.Save(Path.Combine(PreviewDirectory, name));

            HtmlIndex.AppendLine($"<div>{name}</div>");
            HtmlIndex.AppendLine($"<img src=\"{name}\" alt=\"{name}\" />");

            f.Add(bmp, (ushort)hotspot.X, (ushort)hotspot.Y);
        }

        var fileName = variation + ".cur";
        var file = Path.Combine(CursorFileDirectory, fileName);
        f.Save(file, format: IconFile.FileFormat.Cur);
        CsEmbed.AppendLine("    public static Cursor " + variation + " => GetCursor(\"" + fileName + "\");");
    }

    // ================================================================ browser drag cursors
    // The drag cursors browsers provide but Windows lacks (CSS grab / grabbing / col-resize),
    // drawn as vectors so they stay crisp at every DPI size like the rest of the set. The hands
    // are traced from Clarity's cursor-hand-open-line / cursor-hand-grab-line icons (MIT,
    // github.com/vmware/clarity-assets), built the way the SVGs are: the outer silhouette filled
    // black, the inner contour filled white on top — the black border is the space between the
    // two contours, so adjacent fingers share a single border-width slit of black and no pen
    // stroke is involved. col-resize is the black glyph with a white halo, the static equivalent
    // of the XOR screen-invert cursor browsers ship on Windows.

    /// <summary>Clarity 36-box → an ~19px glyph in the 32 cursor box.</summary>
    private const float HandScale = 22f / 36f;
    private const float HandOffsetX = 5f;
    private const float HandOffsetY = 4f;

    /// <summary>Open hand. Hotspot at the palm centre.</summary>
    private static Point DrawGrabHand(float scale, int lineWidth, Graphics g)
    {
        PointF S(float x, float y) =>
            new PointF((x * HandScale + HandOffsetX) * scale, (y * HandScale + HandOffsetY) * scale);

        // outer silhouette: across the four finger caps, down the left side to the thumb lever,
        // around the palm and back up the right edge
        using var outer = new GraphicsPath();
        outer.StartFigure();
        outer.AddBezier(S(31.46f, 8.57f), S(31.30f, 6.90f), S(29.30f, 5.50f), S(27.00f, 5.75f));
        outer.AddBezier(S(27.00f, 5.75f), S(26.30f, 4.00f), S(24.40f, 3.00f), S(22.34f, 3.11f));
        outer.AddBezier(S(22.34f, 3.11f), S(20.80f, -0.60f), S(17.90f, -0.60f), S(15.92f, 2.35f));
        outer.AddBezier(S(15.92f, 2.35f), S(15.40f, 2.00f), S(14.80f, 1.85f), S(14.26f, 1.89f));
        outer.AddBezier(S(14.26f, 1.89f), S(12.40f, 2.00f), S(11.00f, 3.40f), S(11.00f, 5.18f));
        outer.AddLine(S(11.00f, 5.18f), S(11.00f, 17.84f));
        outer.AddBezier(S(11.00f, 17.84f), S(9.72f, 16.24f), S(8.47f, 14.66f), S(8.28f, 14.39f));
        outer.AddBezier(S(8.28f, 14.39f), S(7.60f, 13.40f), S(6.60f, 12.85f), S(5.56f, 12.90f));
        outer.AddBezier(S(5.56f, 12.90f), S(3.60f, 12.85f), S(2.05f, 14.50f), S(2.09f, 16.38f));
        outer.AddBezier(S(2.09f, 16.38f), S(2.18f, 18.18f), S(5.66f, 24.54f), S(8.00f, 28.00f));
        outer.AddBezier(S(8.00f, 28.00f), S(11.54f, 33.24f), S(14.92f, 34.00f), S(15.25f, 34.00f));
        outer.AddLine(S(15.25f, 34.00f), S(26.14f, 33.81f));
        outer.AddBezier(S(26.14f, 33.81f), S(27.70f, 32.60f), S(29.20f, 30.40f), S(29.89f, 27.68f));
        outer.AddBezier(S(29.89f, 27.68f), S(30.89f, 24.59f), S(31.42f, 20.15f), S(31.47f, 14.12f));
        outer.CloseFigure();

        // inner contour, finger slits included — everything white
        using var inner = new GraphicsPath();
        inner.StartFigure();
        inner.AddBezier(S(28.18f, 27.12f), S(27.60f, 29.20f), S(26.50f, 31.10f), S(25.24f, 32.20f));
        inner.AddLine(S(25.24f, 32.20f), S(15.33f, 32.20f));
        inner.AddBezier(S(15.33f, 32.20f), S(14.86f, 32.06f), S(12.26f, 31.10f), S(9.46f, 26.95f));
        inner.AddBezier(S(9.46f, 26.95f), S(6.66f, 22.80f), S(3.94f, 17.27f), S(3.89f, 16.29f));
        inner.AddBezier(S(3.89f, 16.29f), S(3.86f, 15.86f), S(4.02f, 15.42f), S(4.34f, 15.16f));
        inner.AddBezier(S(4.34f, 15.16f), S(4.66f, 14.84f), S(5.06f, 14.70f), S(5.48f, 14.70f));
        inner.AddBezier(S(5.48f, 14.70f), S(6.00f, 14.72f), S(6.47f, 15.00f), S(6.80f, 15.41f));
        inner.AddBezier(S(6.80f, 15.41f), S(7.09f, 15.84f), S(9.16f, 18.41f), S(10.37f, 19.94f));
        inner.AddLine(S(10.37f, 19.94f), S(12.80f, 18.30f));
        inner.AddLine(S(12.80f, 18.30f), S(12.80f, 5.18f));
        inner.AddBezier(S(12.80f, 5.18f), S(12.80f, 3.21f), S(15.75f, 3.21f), S(15.75f, 5.18f));
        inner.AddLine(S(15.75f, 5.18f), S(15.75f, 16.32f));
        inner.AddLine(S(15.75f, 16.32f), S(17.55f, 16.32f));
        inner.AddLine(S(17.55f, 16.32f), S(17.55f, 3.32f));
        inner.AddBezier(S(17.55f, 3.32f), S(17.55f, 1.32f), S(20.55f, 1.32f), S(20.55f, 3.32f));
        inner.AddLine(S(20.55f, 3.32f), S(20.55f, 16.45f));
        inner.AddLine(S(20.55f, 16.45f), S(22.35f, 16.45f));
        inner.AddLine(S(22.35f, 16.45f), S(22.35f, 6.00f));
        inner.AddBezier(S(22.35f, 6.00f), S(22.35f, 4.10f), S(25.20f, 4.10f), S(25.20f, 6.00f));
        inner.AddLine(S(25.20f, 6.00f), S(25.20f, 17.44f));
        inner.AddLine(S(25.20f, 17.44f), S(27.00f, 17.44f));
        inner.AddLine(S(27.00f, 17.44f), S(27.00f, 8.54f));
        inner.AddBezier(S(27.00f, 8.54f), S(27.00f, 6.77f), S(29.65f, 6.77f), S(29.65f, 8.54f));
        inner.AddLine(S(29.65f, 8.54f), S(29.65f, 14.09f));
        inner.AddBezier(S(29.65f, 14.09f), S(29.65f, 20.03f), S(29.17f, 24.21f), S(28.18f, 27.12f));
        inner.CloseFigure();

        g.FillPath(Stroke, outer);
        g.FillPath(Fill, inner);
        return new Point((int)S(17.5f, 21f).X, (int)S(17.5f, 21f).Y);
    }

    /// <summary>Closed fist. Hotspot at the palm centre.</summary>
    private static Point DrawGrabbingHand(float scale, int lineWidth, Graphics g)
    {
        PointF S(float x, float y) =>
            new PointF((x * HandScale + HandOffsetX) * scale, (y * HandScale + HandOffsetY) * scale);

        // outer silhouette: across the curled finger caps, down the left side, around the palm
        // and back up over the thumb
        using var outer = new GraphicsPath();
        outer.StartFigure();
        outer.AddLine(S(28.09f, 9.74f), S(26.93f, 9.93f));
        outer.AddBezier(S(26.93f, 9.93f), S(26.74f, 8.69f), S(25.38f, 7.75f), S(23.66f, 7.75f));
        outer.AddBezier(S(23.66f, 7.75f), S(23.10f, 7.75f), S(22.60f, 7.83f), S(22.13f, 8.00f));
        outer.AddBezier(S(22.13f, 8.00f), S(21.20f, 6.90f), S(20.20f, 6.30f), S(19.00f, 6.30f));
        outer.AddBezier(S(19.00f, 6.30f), S(17.80f, 6.35f), S(16.90f, 6.85f), S(16.13f, 7.62f));
        outer.AddBezier(S(16.13f, 7.62f), S(15.50f, 7.35f), S(14.90f, 7.10f), S(14.24f, 7.11f));
        outer.AddBezier(S(14.24f, 7.11f), S(12.40f, 7.35f), S(11.00f, 8.35f), S(11.00f, 9.89f));
        outer.AddLine(S(11.00f, 9.89f), S(11.00f, 10.80f));
        outer.AddBezier(S(11.00f, 10.80f), S(9.94f, 11.20f), S(6.89f, 12.60f), S(6.09f, 15.64f));
        outer.AddBezier(S(6.09f, 15.64f), S(5.29f, 18.68f), S(6.43f, 23.64f), S(8.78f, 27.42f));
        outer.AddBezier(S(8.78f, 27.42f), S(10.80f, 30.30f), S(12.60f, 32.50f), S(15.21f, 34.00f));
        outer.AddLine(S(15.21f, 34.00f), S(26.10f, 33.81f));
        outer.AddBezier(S(26.10f, 33.81f), S(27.70f, 32.60f), S(29.20f, 30.40f), S(29.85f, 27.68f));
        outer.AddBezier(S(29.85f, 27.68f), S(30.70f, 24.50f), S(31.41f, 21.30f), S(31.41f, 18.00f));
        outer.AddLine(S(31.41f, 18.00f), S(31.41f, 12.50f));
        outer.AddBezier(S(31.41f, 12.50f), S(31.30f, 10.70f), S(29.90f, 9.70f), S(28.09f, 9.74f));
        outer.CloseFigure();

        // inner contour, finger slits included
        using var inner = new GraphicsPath();
        inner.StartFigure();
        inner.AddBezier(S(29.61f, 18.00f), S(29.61f, 21.10f), S(28.90f, 24.40f), S(28.14f, 27.15f));
        inner.AddBezier(S(28.14f, 27.15f), S(27.55f, 29.20f), S(26.50f, 31.10f), S(25.20f, 32.20f));
        inner.AddLine(S(25.20f, 32.20f), S(15.47f, 32.20f));
        inner.AddBezier(S(15.47f, 32.20f), S(13.50f, 30.60f), S(11.70f, 28.60f), S(10.27f, 26.48f));
        inner.AddBezier(S(10.27f, 26.48f), S(7.90f, 22.62f), S(7.27f, 18.25f), S(7.79f, 16.09f));
        inner.AddBezier(S(7.79f, 16.09f), S(8.25f, 14.40f), S(9.50f, 13.20f), S(11.00f, 12.76f));
        inner.AddLine(S(11.00f, 12.76f), S(11.00f, 20.41f));
        inner.AddBezier(S(11.00f, 20.41f), S(11.00f, 21.60f), S(12.80f, 21.60f), S(12.80f, 20.41f));
        inner.AddLine(S(12.80f, 20.41f), S(12.80f, 9.89f));
        inner.AddBezier(S(12.80f, 9.89f), S(12.80f, 8.60f), S(15.75f, 8.60f), S(15.75f, 9.89f));
        inner.AddLine(S(15.75f, 9.89f), S(15.75f, 15.61f));
        inner.AddLine(S(15.75f, 15.61f), S(17.55f, 15.61f));
        inner.AddLine(S(17.55f, 15.61f), S(17.55f, 8.81f));
        inner.AddBezier(S(17.55f, 8.81f), S(17.55f, 7.70f), S(20.55f, 7.70f), S(20.55f, 8.85f));
        inner.AddLine(S(20.55f, 8.85f), S(20.55f, 15.74f));
        inner.AddLine(S(20.55f, 15.74f), S(22.35f, 15.74f));
        inner.AddLine(S(22.35f, 15.74f), S(22.35f, 10.00f));
        inner.AddBezier(S(22.35f, 10.00f), S(22.70f, 9.50f), S(24.60f, 9.50f), S(25.23f, 10.24f));
        inner.AddLine(S(25.23f, 10.24f), S(25.23f, 16.74f));
        inner.AddLine(S(25.23f, 16.74f), S(27.00f, 16.74f));
        inner.AddLine(S(27.00f, 16.74f), S(27.00f, 11.87f));
        inner.AddBezier(S(27.00f, 11.87f), S(27.60f, 11.40f), S(28.90f, 11.60f), S(29.61f, 12.48f));
        inner.CloseFigure();

        g.FillPath(Stroke, outer);
        g.FillPath(Fill, inner);
        return new Point((int)S(18f, 20f).X, (int)S(18f, 20f).Y);
    }

    /// <summary>CSS col-resize: two parallel vertical bars with an arrow out each side, black
    /// with a white halo. Axis-aligned pixel art, so it is built the way DrawRuler is — every
    /// coordinate rounded to a pixel boundary, 1px features sized by lineWidth, HighQuality
    /// pixel offset — rather than from fractional geometry the rasterizer has to smear.
    /// Hotspot dead centre between the bars, on the arrows' axis.</summary>
    private static Point DrawColResize(float scale, int lineWidth, Graphics g)
    {
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var barLeft = round(10 * scale);          // bars and the gap between them are 1px features
        var barRight = barLeft + lineWidth * 2;
        var barTop = round(2 * scale);
        var barHeight = round(16 * scale);
        var stemTop = round(9 * scale);           // the arrows' stem, also a 1px feature
        var reach = round(9 * scale);             // bar to arrow tip
        var headLength = round(5 * scale);
        var wing = round(3 * scale);              // head half-height beyond the stem
        var tipLeft = barLeft - reach;
        var tipRight = barRight + lineWidth + reach;
        var cy = stemTop + lineWidth / 2f;

        using var gp = new GraphicsPath(FillMode.Winding);
        gp.AddRectangle(new RectangleF(barLeft, barTop, lineWidth, barHeight));
        gp.CloseFigure();
        gp.AddRectangle(new RectangleF(barRight, barTop, lineWidth, barHeight));
        gp.CloseFigure();

        // left arrow: head + stem running to the bars
        gp.AddPolygon(new[]
        {
            mkpt(tipLeft, cy),
            mkpt(tipLeft + headLength, stemTop - wing),
            mkpt(tipLeft + headLength, stemTop),
            mkpt(barLeft, stemTop),
            mkpt(barLeft, stemTop + lineWidth),
            mkpt(tipLeft + headLength, stemTop + lineWidth),
            mkpt(tipLeft + headLength, stemTop + lineWidth + wing),
        });
        gp.CloseFigure();

        // right arrow, mirrored
        gp.AddPolygon(new[]
        {
            mkpt(tipRight, cy),
            mkpt(tipRight - headLength, stemTop - wing),
            mkpt(tipRight - headLength, stemTop),
            mkpt(barRight + lineWidth, stemTop),
            mkpt(barRight + lineWidth, stemTop + lineWidth),
            mkpt(tipRight - headLength, stemTop + lineWidth),
            mkpt(tipRight - headLength, stemTop + lineWidth + wing),
        });
        gp.CloseFigure();

        // white halo first, black glyph on top — deliberately the inverse of the other cursors'
        // fill/outline, matching how Mozilla's invert cursor reads over light content. Stroking
        // each bar separately would notch the halo between their end caps, so the bar pair also
        // gets one solid white rect containing both bars and the gap.
        using var halo = new Pen(Fill, lineWidth * 2);
        halo.LineJoin = LineJoin.Round;
        g.DrawPath(halo, gp);
        g.FillRectangle(Fill, barLeft - lineWidth, barTop - lineWidth,
            lineWidth * 5, barHeight + lineWidth * 2);
        g.FillPath(Stroke, gp);

        return new Point((int)(barLeft + lineWidth + lineWidth / 2), (int)cy);
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

        g.DrawLine(pw, p1, p2);
        g.DrawLine(pb, p1, p2);
    }

    private static void DrawRuler(float scale, int lineWidth, Graphics g)
    {
        // every edge here is axis aligned, so the shape is built from fills rather than pens:
        // a centred pen of width lineWidth*2 leaves a one pixel border on two sides and two on
        // the others once the interior is filled over it, which is visible at 32px
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var left = round(6 * scale);
        var top = round(18 * scale);
        var width = round(19 * scale);
        var height = round(8 * scale);

        g.FillRectangle(Stroke, left, top, width, height);
        g.FillRectangle(Fill, left + lineWidth, top + lineWidth, width - (lineWidth * 2), height - (lineWidth * 2));

        // graduations hang off the top edge alternating long/short, otherwise the badge is
        // indistinguishable from the rectangle cursor at 32px
        var tickLong = round(4 * scale);
        var tickShort = round(2 * scale);

        for (int i = 0; i < 5; i++)
        {
            var x = round((9 + (i * 3)) * scale);
            var len = (i % 2) == 0 ? tickLong : tickShort;
            g.FillRectangle(Stroke, x, top + lineWidth, lineWidth, len);
        }
    }

    private static void DrawT(float scale, int lineWidth, Graphics g)
    {
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

    private static Point DrawRotate(float scale, int lineWidth, Graphics g)
    {
        float offset = (lineWidth % 2) == 0 ? 0 : 0.5f;
        var max = floor(scale * 32);
        var margin = ceil(scale * 8);
        var headY = margin - (scale * 1.5f) - (lineWidth / 2);
        var headSize = ceil(scale * 9);
        var center = round(scale * 17f);

        var rect = new RectangleF(margin, margin + offset, max - margin * 2, max - margin * 2);

        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var p1 = new Pen(Stroke, scale * 3 + (lineWidth * 2));
        g.DrawEllipse(p1, rect);

        using var p2 = new Pen(Fill, scale * 3);
        g.DrawEllipse(p2, rect);

        using var p3 = new Pen(Stroke, lineWidth);
        var headPts = new PointF[] { new(center + headSize - offset, headY), new(center - offset, headY), new(center - offset, headY + headSize) };
        g.FillPolygon(Fill, headPts);
        g.DrawPolygon(p3, headPts);

        g.Clip = new Region(new RectangleF(center + lineWidth * 3, 0, max, max));
        g.DrawEllipse(p2, rect);

        var halfmax = (int)(max / 2);
        return new Point(halfmax, halfmax);
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
        g.FillPath(Fill, gp);

        if (scale > 1.5)
        {
            return new Point((int)center, (int)(center - halfLine));
        }
        else
        {
            return new Point((int)(center - halfLine), (int)(center - halfLine));
        }
    }

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
    }
}
