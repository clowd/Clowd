using System;
using System.Globalization;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.Services
{
    /// <summary>
    /// The frame rate a render is encoded at. There is no project-wide rate to pick any more: the
    /// ceiling is the fastest video clip in the edit (frames above it would only repeat pictures
    /// that already exist), and a render request can only cap below that — exactly as the size cap
    /// never upscales. Rates stay rationals so 29.97 material renders as 30000/1001, not as 30.
    /// </summary>
    public static class RenderFrameRate
    {
        /// <summary>The ceiling when the edit has no video with a rate (text cards and stills):
        /// smooth enough for any motion an effect adds, without doubling the file for nothing.</summary>
        public const int NoVideoCeilingFps = 60;

        /// <summary>The most the project can usefully be rendered at — what "Actual" means.</summary>
        public static (int Num, int Den) Ceiling(Project project) =>
            EditorSession.GetMaxFrameRate(project) ?? (NoVideoCeilingFps, 1);

        /// <summary>The rate a render with cap <paramref name="maxFps"/> (0 for none) is encoded at.</summary>
        public static (int Num, int Den) Resolve(Project project, int maxFps) => Resolve(Ceiling(project), maxFps);

        /// <summary>The ceiling when the cap is none or at/above it, else the cap itself.</summary>
        public static (int Num, int Den) Resolve((int Num, int Den) ceiling, int maxFps)
        {
            if (maxFps <= 0 || !IsBelow(maxFps, ceiling))
                return ceiling;

            return (maxFps, 1);
        }

        /// <summary>Whether a whole-number cap would actually lower <paramref name="ceiling"/>: 30
        /// lowers 59.94 but not 29.97 (a 30 cap on 29.97 material keeps the material's rate).</summary>
        public static bool IsBelow(int fps, (int Num, int Den) ceiling) =>
            fps > 0 && (long)fps * ceiling.Den < ceiling.Num;

        /// <summary>A copy of <paramref name="project"/> set to the rate the render is encoded at:
        /// the renderer composes on the output rate's grid, so this is the whole of applying the
        /// cap. The editor's own project is never touched.</summary>
        public static Project Apply(Project project, int maxFps)
        {
            var (num, den) = Resolve(project, maxFps);
            var copy = Project.FromJson(project.ToJson());
            copy.Output.FpsNum = num;
            copy.Output.FpsDen = den;
            return copy;
        }

        /// <summary>"60 fps", or "29.97 fps" for the broadcast rationals.</summary>
        public static string Describe((int Num, int Den) rate) => Format(rate) + " fps";

        /// <summary>"60", or "29.97" — two decimals is what every video tool shows for 30000/1001,
        /// and trailing zeros are trimmed so nothing reads as "25.00".</summary>
        public static string Format((int Num, int Den) rate)
        {
            if (rate.Num <= 0 || rate.Den <= 0)
                return "0";
            if (rate.Num % rate.Den == 0)
                return (rate.Num / rate.Den).ToString(CultureInfo.InvariantCulture);

            return (rate.Num / (double)rate.Den).ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
