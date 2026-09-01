using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia.Platform;
using SkiaSharp;

namespace Clowd.UI.Preview.Icons
{
    /// <summary>
    /// The typefaces the preview engine draws with — the lettering on generated file-type icons and the
    /// monospace face a text excerpt is typeset in — loaded straight out of the app's own embedded
    /// resources rather than looked up by family name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never use <c>SKTypeface.FromFamilyName("Inter")</c> here.</b> Inter ships with the app as an Avalonia
    /// resource and is registered with Avalonia's FontManager by <c>Program.cs</c>'s <c>.WithInterFont()</c>;
    /// it is not installed with the OS and Skia's font manager cannot see it. That call therefore does not
    /// fail — it silently substitutes the system default (Segoe UI on Windows), whose cap height and side
    /// bearings differ enough that the cap-height-solved lettering in <c>FileIconRenderer</c> would sit at the
    /// wrong size against the shipped art, with nothing in the output to say why.
    /// </para>
    /// <para>
    /// Skia keeps reading glyph data out of the stream for the life of the typeface, so both the stream and
    /// the typeface are pinned in statics and never disposed. That is the intent: there is exactly one of
    /// each per process, ~300 KB, held until exit, and the alternative is a use-after-free in native code.
    /// </para>
    /// <para>
    /// The load itself opens an embedded resource and decodes font tables, so it sits behind a
    /// <see cref="Lazy{T}"/> and must be first touched from a preview worker — never the UI or render thread,
    /// per the cold-start rule.
    /// </para>
    /// </remarks>
    public static class IconFonts
    {
        // Verified against the Avalonia.Fonts.Inter 12.1.0 string table; the package embeds Thin/Light/
        // Regular/Medium/SemiBold/Bold under /Assets.
        private const string InterAssetUri = "avares://Avalonia.Fonts.Inter/Assets/Inter-SemiBold.ttf";

        // Already in this app's resources for the editor's monospace text tool. A last-ditch face that at
        // least has real metrics of its own.
        private const string CascadiaAssetUri = "avares://Clowd.Ui/Assets/Fonts/CascadiaCode-Regular.ttf";

        private static readonly Lazy<SKTypeface> _lettering =
            new Lazy<SKTypeface>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<SKTypeface> _monospace =
            new Lazy<SKTypeface>(LoadMonospace, LazyThreadSafetyMode.ExecutionAndPublication);

        // Held for process life alongside the typefaces; see the remarks above. A list rather than a
        // field because more than one face is loaded this way now, and dropping the first one's stream
        // when the second loaded would be a use-after-free in native code rather than a leak.
        private static readonly List<MemoryStream> _fontData = new List<MemoryStream>();

        private static bool _isInter;

        /// <summary>Inter SemiBold, or the fallback chain's best effort. Never null.</summary>
        public static SKTypeface Lettering => _lettering.Value;

        /// <summary>
        /// A fixed-pitch face, for laying out a text file's first lines on a preview tile. Never null.
        /// </summary>
        /// <remarks>
        /// Cascadia Code ships in this app's own resources for the editor's monospace text tool, so it is
        /// the first choice and the only one guaranteed to be there. The fallbacks — an OS-installed
        /// Consolas, then Skia's default — are only reachable if that resource goes missing, and the
        /// warning about <c>FromFamilyName</c> on <see cref="Lettering"/> does not apply to them: Consolas
        /// really is OS-installed on Windows, and unlike the icon lettering nothing here is solved against
        /// a reference cap height, so a substitute face costs a slightly different column count rather
        /// than art that sits wrong beside the shipped icons.
        /// </remarks>
        public static SKTypeface Monospace => _monospace.Value;

        /// <summary>
        /// True when <see cref="Lettering"/> really is the embedded Inter. False means the lettering metrics
        /// are a substitute's, which is worth knowing when a generated icon looks off next to a shipped one.
        /// Forces the load, so treat it as worker-thread-only too.
        /// </summary>
        public static bool LetteringIsInter
        {
            get
            {
                _ = _lettering.Value;
                return _isInter;
            }
        }

        private static SKTypeface Load()
        {
            var inter = TryLoad(InterAssetUri);
            if (inter != null)
            {
                _isInter = true;
                return inter;
            }

            // Inside a Lazy, so this is the "log once" — the load is attempted exactly once per process and a
            // failure here is a packaging regression (a trimmed-away resource, a renamed asset path), not a
            // transient condition worth retrying.
            Debug.WriteLine("IconFonts: could not load " + InterAssetUri +
                            "; generated file icons will be lettered with a substitute face.");

            return TryLoad(CascadiaAssetUri) ?? SKTypeface.CreateDefault();
        }

        private static SKTypeface LoadMonospace()
        {
            var cascadia = TryLoad(CascadiaAssetUri);
            if (cascadia != null)
                return cascadia;

            Debug.WriteLine("IconFonts: could not load " + CascadiaAssetUri +
                            "; text previews will be typeset with a substitute face.");

            // Genuinely OS-installed on Windows, so unlike Inter this lookup either finds the real face
            // or falls through to the default — it cannot silently substitute something else under the
            // name we asked for.
            SKTypeface consolas = null;
            try { consolas = SKTypeface.FromFamilyName("Consolas"); }
            catch (Exception ex) { Debug.WriteLine("IconFonts: Consolas lookup failed: " + ex.Message); }

            return consolas ?? SKTypeface.CreateDefault();
        }

        private static SKTypeface TryLoad(string uri)
        {
            try
            {
                // Copied out of the resource stream because Avalonia's asset streams are not guaranteed to
                // stay seekable or open for as long as Skia wants to read from them.
                var buffer = new MemoryStream();
                using (var asset = AssetLoader.Open(new Uri(uri)))
                    asset.CopyTo(buffer);
                buffer.Position = 0;

                var typeface = SKTypeface.FromStream(buffer, 0);
                if (typeface == null)
                {
                    buffer.Dispose();
                    return null;
                }

                lock (_fontData)
                    _fontData.Add(buffer);

                return typeface;
            }
            catch (Exception ex)
            {
                // AssetLoader throws for a missing asset and can throw for a malformed URI; either way the
                // fallback chain is the answer, not a crash on a preview worker.
                Debug.WriteLine("IconFonts: " + uri + " failed to load: " + ex.Message);
                return null;
            }
        }
    }
}
