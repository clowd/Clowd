using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>One entry of the top bar's resolution picker. <see cref="ToString"/> is the label:
    /// the dropdown lists items with the default template, exactly as the image editor's dash and
    /// obscure-mode pickers list their enum values.</summary>
    public sealed class ResolutionOption
    {
        private ResolutionOption(int widthPx, int heightPx, string label, bool isCustomPrompt)
        {
            WidthPx = widthPx;
            HeightPx = heightPx;
            Label = label;
            IsCustomPrompt = isCustomPrompt;
        }

        public int WidthPx { get; }

        public int HeightPx { get; }

        public string Label { get; }

        /// <summary>The trailing "Custom…" row, which is not a size: picking it opens the
        /// <see cref="CustomResolutionDialog"/> instead of resizing anything.</summary>
        public bool IsCustomPrompt { get; }

        public static ResolutionOption Size(int widthPx, int heightPx, string suffix = null) =>
            new ResolutionOption(widthPx, heightPx,
                String.Create(CultureInfo.InvariantCulture, $"{widthPx}x{heightPx}") +
                (String.IsNullOrEmpty(suffix) ? "" : " " + suffix), false);

        public static ResolutionOption CustomPrompt() => new ResolutionOption(0, 0, "Custom…", true);

        public bool Matches(int widthPx, int heightPx) =>
            !IsCustomPrompt && WidthPx == widthPx && HeightPx == heightPx;

        public override string ToString() => Label;
    }

    /// <summary>
    /// Builds the resolution picker's list for a project, in three groups: the native size and
    /// downscales of it (which keep the material's own aspect — downscaling a 16:10 recording should
    /// not letterbox it), then the fixed standards every video tool offers regardless of the
    /// material (16:9 and square, for an edit destined somewhere with its own shape), then
    /// "Custom…". Duplicates collapse to the first entry that produced them, so a 16:9 recording
    /// simply does not repeat 1920x1080 in the standards group.
    /// </summary>
    internal static class ResolutionOptions
    {
        /// <summary>Heights offered as downscales of the native size, at the native aspect.</summary>
        private static readonly int[] NativeScaleHeights = { 2160, 1440, 1080, 720, 480 };

        /// <summary>The material-independent sizes, offered whatever was recorded.</summary>
        private static readonly (int W, int H, string Label)[] StandardSizes =
        {
            (1920, 1080, "(16:9)"),
            (1280, 720, "(16:9)"),
            (854, 480, "(16:9)"),
            (1080, 1080, "(1:1)"),
            (720, 720, "(1:1)"),
        };

        public static List<ResolutionOption> Build(Project project)
        {
            var native = EditorSession.GetNativeSize(project);
            var options = new List<ResolutionOption>();

            if (native != null)
            {
                options.Add(ResolutionOption.Size(
                    EditorSession.ClampOutputDimension(native.Value.WidthPx),
                    EditorSession.ClampOutputDimension(native.Value.HeightPx),
                    "(Native)"));

                foreach (var height in NativeScaleHeights)
                {
                    // an "upscale" of the material is not a resolution anyone wants offered; the
                    // native entry above is the ceiling.
                    if (height >= native.Value.HeightPx)
                        continue;

                    var width = (int)Math.Round((double)height * native.Value.WidthPx / native.Value.HeightPx);
                    Add(options, EditorSession.ClampOutputDimension(width),
                        EditorSession.ClampOutputDimension(height), null);
                }
            }

            foreach (var (w, h, label) in StandardSizes)
                Add(options, w, h, label);

            // whatever the project is set to now must be in the list — it is the selected item, and
            // a previous Custom… (or an aspect the standard heights do not produce) is not otherwise
            // among the entries above.
            var output = project?.Output;
            if (output != null)
                Add(options, output.WidthPx, output.HeightPx, null);

            options.Add(ResolutionOption.CustomPrompt());
            return options;
        }

        /// <summary>The entry for the project's current size — always present, see
        /// <see cref="Build"/>.</summary>
        public static ResolutionOption FindCurrent(List<ResolutionOption> options, Project project)
        {
            var output = project?.Output;
            if (output == null)
                return null;

            foreach (var option in options)
            {
                if (option.Matches(output.WidthPx, output.HeightPx))
                    return option;
            }

            return null;
        }

        private static void Add(List<ResolutionOption> options, int widthPx, int heightPx, string suffix)
        {
            if (widthPx <= 0 || heightPx <= 0)
                return;

            foreach (var existing in options)
            {
                if (existing.Matches(widthPx, heightPx))
                    return;
            }

            options.Add(ResolutionOption.Size(widthPx, heightPx, suffix));
        }
    }
}
