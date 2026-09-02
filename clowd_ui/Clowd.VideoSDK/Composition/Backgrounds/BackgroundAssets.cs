using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The embedded wallpaper files as drawable scenes, loaded on first use and kept for the
    /// life of the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two caches on the <c>FrameComposer.ImageCache</c> pattern (one lock, null cached for a
    /// missing resource, unbounded): parsed SVGs by asset path, so each of the five generative
    /// files is parsed once and recolored per theme, and scenes by (style, theme). At most
    /// 4 + 2 + 10 + 1 + 1 + 5 x 7 = 53 entries, and only those a project actually draws.
    /// </para>
    /// <para>
    /// Lazy on purpose, where <c>CursorAssets</c> builds its table eagerly: a project with no
    /// background must pay nothing, and the first compose of one that has a background pays for
    /// one file (the largest parse is the 86 KB blob). The lock is held across a parse, which
    /// stalls a concurrent composer for the few milliseconds it takes, the same trade
    /// <c>ImageCache</c> makes across a decode.
    /// </para>
    /// <para>
    /// Everything cached is a CPU Skia object (<see cref="SKPath"/>, <see cref="SKShader"/>,
    /// <see cref="SKPicture"/>, a raster <see cref="SKImage"/>); nothing is bound to a
    /// <c>GRContext</c>, so the editor's Avalonia context and a concurrent export's D3D12/Metal
    /// context may draw the same scene at once.
    /// </para>
    /// </remarks>
    internal static class BackgroundAssets
    {
        /// <summary>The prefix the csproj's <c>LogicalName</c> gives every file under
        /// <c>Composition/Backgrounds/Art</c>; an asset path's <c>/</c> becomes a <c>.</c>.</summary>
        internal const string ResourceRoot = "Clowd.VideoSDK.Composition.Backgrounds.Art.";

        private static readonly Assembly Assembly = typeof(BackgroundAssets).Assembly;

        internal static readonly string[] ResourceNames = Assembly.GetManifestResourceNames();

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, SvgScene> ParsedByAsset = new Dictionary<string, SvgScene>(StringComparer.Ordinal);
        private static readonly Dictionary<(string Style, string Theme), BackgroundScene> Scenes
            = new Dictionary<(string, string), BackgroundScene>();

        /// <summary>The manifest name of a catalog asset path such as <c>big-sur/teal.svg</c>.</summary>
        internal static string ResourceNameOf(string asset)
            => ResourceRoot + asset.Replace('/', '.').Replace('\\', '.');

        internal static bool ResourceExists(string asset)
            => Array.IndexOf(ResourceNames, ResourceNameOf(asset)) >= 0;

        /// <summary>The parsed XML of an SVG asset, or null when the resource is absent.</summary>
        internal static XElement TryReadXml(string asset)
        {
            using var stream = TryOpen(asset);
            if (stream == null)
                return null;
            using var reader = new StreamReader(stream);
            return XDocument.Parse(reader.ReadToEnd()).Root;
        }

        private static Stream TryOpen(string asset)
        {
            string name = ResourceNameOf(asset);
            return Array.IndexOf(ResourceNames, name) < 0 ? null : Assembly.GetManifestResourceStream(name);
        }

        /// <summary>
        /// The scene for a (style, theme) pair. The ids are resolved through the catalog first
        /// (unknown style: the default; unknown theme: the style's first), so the answer is
        /// never null for a missing id — only for a missing embedded file, which the tests rule
        /// out for every catalog row.
        /// </summary>
        internal static BackgroundScene GetScene(string styleId, string themeId)
        {
            var spec = BackgroundCatalog.ResolveSpec(styleId, themeId);
            var style = BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(styleId));
            var key = (style.Id, spec.Id ?? string.Empty);

            lock (Sync)
            {
                if (Scenes.TryGetValue(key, out var cached))
                    return cached;

                BackgroundScene scene = null;
                try
                {
                    scene = Load(spec);
                }
                catch (Exception)
                {
                    // A file this build cannot read draws nothing rather than failing a frame;
                    // the catalog tests load every row and would report it.
                }
                Scenes[key] = scene;
                return scene;
            }
        }

        private static BackgroundScene Load(BackgroundTheme spec)
        {
            // the solid style has no file to load: its picture is the item's color, which the
            // composer and the pickers fill themselves (BackgroundStyle.IsSolid)
            if (spec.Asset == null)
                return null;

            var parsed = GetParsed(spec.Asset);
            return parsed == null ? null : new SvgBackgroundScene(parsed.Recolor(spec.Palette));
        }

        /// <summary>One parse per SVG file; must be called under <see cref="Sync"/>.</summary>
        private static SvgScene GetParsed(string asset)
        {
            if (ParsedByAsset.TryGetValue(asset, out var scene))
                return scene;
            var root = TryReadXml(asset);
            scene = root == null ? null : BackgroundSvgReader.Read(root);
            ParsedByAsset[asset] = scene;
            return scene;
        }
    }
}
