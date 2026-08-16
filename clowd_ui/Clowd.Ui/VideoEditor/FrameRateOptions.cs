using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor
{
    /// <summary>One entry of the top bar's frame-rate picker. The rate is the rational the model
    /// stores, never a double, so 30000/1001 round-trips as itself; <see cref="ToString"/> is the
    /// label, since the dropdown lists items with the default template.</summary>
    public sealed class FrameRateOption
    {
        private FrameRateOption(int num, int den, string label)
        {
            Num = num;
            Den = den;
            Label = label;
        }

        public int Num { get; }

        public int Den { get; }

        public string Label { get; }

        // no "fps" in the label: the bar already says Fps:, exactly as the resolution picker's rows
        // are "1920x1080" under a Resolution: label.
        public static FrameRateOption Rate(int num, int den, string suffix = null) =>
            new FrameRateOption(num, den,
                Format(num, den) + (String.IsNullOrEmpty(suffix) ? "" : " " + suffix));

        public bool Matches(int num, int den)
        {
            if (num <= 0 || den <= 0)
                return false;

            // cross-multiply rather than reduce: 60/1 and 120/2 are the same rate and must not both
            // be offered, and the products cannot overflow at any rate a video file can hold.
            return (long)Num * den == (long)num * Den;
        }

        public override string ToString() => Label;

        /// <summary>"30" for an exact rate, "29.97" for the broadcast rationals — two decimals is
        /// what every video tool shows for 30000/1001 and 24000/1001, and trailing zeros are
        /// trimmed so nothing reads as "25.00".</summary>
        private static string Format(int num, int den)
        {
            if (den > 0 && num % den == 0)
                return (num / den).ToString(CultureInfo.InvariantCulture);

            var fps = den > 0 ? (double)num / den : 0d;
            return fps.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Builds the frame-rate picker's list for a project: the material's own rate first, then the
    /// fixed rates every video tool offers. Duplicates collapse to the first entry that
    /// produced them, so a 30fps recording does not list 30 twice — while a 29.97 one keeps both its
    /// native rate and the plain 30 below it, because those are genuinely different rates.
    /// </summary>
    internal static class FrameRateOptions
    {
        /// <summary>The material-independent rates, offered whatever was recorded.</summary>
        private static readonly int[] StandardRates = { 24, 25, 30, 60, 120 };

        public static List<FrameRateOption> Build(Project project)
        {
            var options = new List<FrameRateOption>();

            var native = EditorSession.GetNativeFrameRate(project);
            if (native != null)
                Add(options, native.Value.Num, native.Value.Den, "(Native)");

            foreach (var fps in StandardRates)
                Add(options, fps, 1, null);

            // whatever the project is set to now must be in the list — it is the selected item, and
            // an edit whose rate came from imported material is not otherwise among the entries
            // above (the native row follows the first source, which a relink can change).
            var output = project?.Output;
            if (output != null)
                Add(options, output.FpsNum, output.FpsDen, null);

            return options;
        }

        /// <summary>The entry for the project's current rate — always present, see
        /// <see cref="Build"/>.</summary>
        public static FrameRateOption FindCurrent(List<FrameRateOption> options, Project project)
        {
            var output = project?.Output;
            if (output == null)
                return null;

            foreach (var option in options)
            {
                if (option.Matches(output.FpsNum, output.FpsDen))
                    return option;
            }

            return null;
        }

        private static void Add(List<FrameRateOption> options, int num, int den, string suffix)
        {
            if (num <= 0 || den <= 0)
                return;

            foreach (var existing in options)
            {
                if (existing.Matches(num, den))
                    return;
            }

            options.Add(FrameRateOption.Rate(num, den, suffix));
        }
    }
}
