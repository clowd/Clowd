using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>One entry of the top bar's aspect-ratio picker. <see cref="ToString"/> is the label:
    /// the dropdown lists items with the default template, exactly as the image editor's dash and
    /// obscure-mode pickers list their enum values. The pixel size is what picking it resizes the
    /// canvas to — the preview cannot show resolution, so the picker only names the shape, and the
    /// render dialog's size cap is where a smaller file is asked for.</summary>
    public sealed class AspectRatioOption
    {
        public AspectRatioOption(int widthPx, int heightPx, string label, bool isRememberedCustom = false)
        {
            WidthPx = widthPx;
            HeightPx = heightPx;
            Label = label;
            IsRememberedCustom = isRememberedCustom;
        }

        private AspectRatioOption(string label)
        {
            Label = label;
            IsCustomPrompt = true;
        }

        public int WidthPx { get; }

        public int HeightPx { get; }

        public string Label { get; }

        /// <summary>The last size entered through "Custom…", kept at its exact pixel size (it is a
        /// resolution the user typed, not a shape), so it never stands in for a standard ratio.</summary>
        public bool IsRememberedCustom { get; }

        /// <summary>The trailing "Custom…" row, which is not a size: picking it opens the
        /// <see cref="CustomResolutionDialog"/> instead of resizing anything.</summary>
        public bool IsCustomPrompt { get; }

        public static AspectRatioOption CustomPrompt() => new AspectRatioOption("Custom…");

        /// <summary>Same shape as <paramref name="widthPx"/> x <paramref name="heightPx"/>, whatever
        /// the pixel count. The even-rounding of <see cref="EditorSession.ClampOutputDimension"/>
        /// moves a ratio by a fraction of a pixel, so equality is to within half a percent.</summary>
        public bool Matches(int widthPx, int heightPx) =>
            !IsCustomPrompt && widthPx > 0 && heightPx > 0 &&
            Math.Abs((double)WidthPx / HeightPx - (double)widthPx / heightPx) < 0.005 * WidthPx / HeightPx;

        public override string ToString() => Label;
    }

    /// <summary>
    /// Builds the aspect-ratio picker's list for a project: the material's own shape first, the
    /// last size entered through "Custom…" second, then the fixed ratios every video tool offers
    /// (landscape, portrait and square), and "Custom…" itself last. Every ratio is
    /// sized off the same short edge — the native material's, or the canvas's when nothing is
    /// imported — so switching 16:9 to 9:16 turns a 1920x1080 canvas into 1080x1920 rather than
    /// cropping it down to 608x1080, and switching back returns the exact size it started at.
    /// </summary>
    internal static class AspectRatioOptions
    {
        /// <summary>Landscape first, then square, then the portrait shapes phone apps ask for. The
        /// other orientation of any of them is one press of the rotate button away.</summary>
        private static readonly (int W, int H)[] StandardRatios =
        {
            (16, 9),
            (16, 10),
            (4, 3),
            (3, 2),
            (21, 9),
            (1, 1),
            (4, 5),
            (3, 4),
            (9, 16),
        };

        public static List<AspectRatioOption> Build(Project project, (int WidthPx, int HeightPx)? rememberedCustom = null)
        {
            var native = EditorSession.GetNativeSize(project);
            var output = project?.Output;
            var options = new List<AspectRatioOption>();

            var shortEdge = native != null
                ? Math.Min(native.Value.WidthPx, native.Value.HeightPx)
                : output != null ? Math.Min(output.WidthPx, output.HeightPx) : 0;
            if (shortEdge <= 0)
            {
                options.Add(AspectRatioOption.CustomPrompt());
                return options;
            }

            if (native != null)
            {
                var w = EditorSession.ClampOutputDimension(native.Value.WidthPx);
                var h = EditorSession.ClampOutputDimension(native.Value.HeightPx);
                options.Add(new AspectRatioOption(w, h, Describe(w, h)));
            }

            if (rememberedCustom is { WidthPx: > 0, HeightPx: > 0 } custom)
            {
                var w = EditorSession.ClampOutputDimension(custom.WidthPx);
                var h = EditorSession.ClampOutputDimension(custom.HeightPx);
                // the native row already is this exact size
                if (!options.Exists(o => o.WidthPx == w && o.HeightPx == h))
                    options.Add(new AspectRatioOption(w, h,
                        String.Create(CultureInfo.InvariantCulture, $"{w}x{h}"), isRememberedCustom: true));
            }

            foreach (var (rw, rh) in StandardRatios)
            {
                var (w, h) = SizeFor(rw, rh, shortEdge);
                Add(options, w, h, Format(rw, rh), ignoreRememberedCustom: true);
            }

            // whatever the canvas is set to now must be in the list — it is the selected item, and
            // a project saved with a hand-picked size is not otherwise among the entries above.
            if (output != null)
                Add(options, output.WidthPx, output.HeightPx, Describe(output.WidthPx, output.HeightPx), ignoreRememberedCustom: false);

            options.Add(AspectRatioOption.CustomPrompt());
            return options;
        }

        /// <summary>The entry for the project's current shape — always present, see
        /// <see cref="Build"/>. An exact size match wins over a same-shape one, so a canvas at the
        /// native size selects the native row rather than the standard ratio it happens to equal.</summary>
        public static AspectRatioOption FindCurrent(List<AspectRatioOption> options, Project project)
        {
            var output = project?.Output;
            if (output == null)
                return null;

            foreach (var option in options)
            {
                if (option.WidthPx == output.WidthPx && option.HeightPx == output.HeightPx)
                    return option;
            }

            foreach (var option in options)
            {
                if (option.Matches(output.WidthPx, output.HeightPx))
                    return option;
            }

            return null;
        }

        /// <summary>The canvas for <paramref name="ratioW"/>:<paramref name="ratioH"/> whose short
        /// edge is <paramref name="shortEdge"/>.</summary>
        private static (int W, int H) SizeFor(int ratioW, int ratioH, int shortEdge)
        {
            var longEdge = (int)Math.Round((double)shortEdge * Math.Max(ratioW, ratioH) / Math.Min(ratioW, ratioH));
            return ratioW >= ratioH
                ? (EditorSession.ClampOutputDimension(longEdge), EditorSession.ClampOutputDimension(shortEdge))
                : (EditorSession.ClampOutputDimension(shortEdge), EditorSession.ClampOutputDimension(longEdge));
        }

        /// <summary>A size as a ratio label: the standard ratio it is in either orientation (to
        /// within <see cref="AspectRatioOption.Matches"/>, so a rotated 16:10 reads 10:16 rather
        /// than 5:8), its reduced ratio when that stays readable, or the plain pixel size for
        /// shapes like 1366x768 whose ratio (683:384) says nothing.</summary>
        private static string Describe(int widthPx, int heightPx)
        {
            foreach (var (rw, rh) in StandardRatios)
            {
                if (new AspectRatioOption(rw, rh, null).Matches(widthPx, heightPx))
                    return Format(rw, rh);
                if (new AspectRatioOption(rh, rw, null).Matches(widthPx, heightPx))
                    return Format(rh, rw);
            }

            var gcd = Gcd(widthPx, heightPx);
            var (w, h) = (widthPx / gcd, heightPx / gcd);
            return w <= 32 && h <= 32
                ? Format(w, h)
                : String.Create(CultureInfo.InvariantCulture, $"{widthPx}x{heightPx}");
        }

        private static string Format(int w, int h) => String.Create(CultureInfo.InvariantCulture, $"{w}:{h}");

        private static int Gcd(int a, int b)
        {
            while (b != 0)
                (a, b) = (b, a % b);
            return a;
        }

        /// <summary>Adds a row unless one of the same shape is already listed: a 16:9 recording's
        /// native row is its 16:9 row. The remembered custom size is an exact resolution rather
        /// than a shape, so a standard ratio is still listed beside it
        /// (<paramref name="ignoreRememberedCustom"/>).</summary>
        private static void Add(List<AspectRatioOption> options, int widthPx, int heightPx, string label,
            bool ignoreRememberedCustom)
        {
            if (widthPx <= 0 || heightPx <= 0)
                return;

            foreach (var existing in options)
            {
                if (ignoreRememberedCustom && existing.IsRememberedCustom)
                    continue;
                if (existing.Matches(widthPx, heightPx))
                    return;
            }

            options.Add(new AspectRatioOption(widthPx, heightPx, label));
        }
    }
}
