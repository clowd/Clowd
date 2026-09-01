using System;
using System.Diagnostics;
using System.Threading;
using Clowd.UI.Preview.Icons;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// Lane A. The terminal producer: whatever else failed or was never there, a session still has
    /// a file type, and a file type always has a picture. This is the only producer that is not
    /// allowed to come back empty-handed in practice.
    /// </summary>
    /// <remarks>
    /// A thin adapter over <see cref="FileIconRenderer"/>, which owns the artwork, the lettered
    /// template and the memo caches. Two decisions live here rather than there: which tag a
    /// resolved source maps to, and what pixel size to rasterize at.
    /// </remarks>
    public static class FileIconPreviewProducer
    {
        /// <summary>
        /// The rasterization sizes. Each is a whole multiple (1x, 1.5x, 2x, 3x) of the icons8
        /// artwork's own 48-unit viewBox, so a glyph edge authored on a unit boundary lands on a
        /// pixel boundary at every one of them. Four sizes is also the whole point: the renderer's
        /// memo cache is keyed by (tag, size), and a continuous size would give every display scale
        /// in a mixed-DPI setup its own copy of all 77 icons.
        /// </summary>
        private static readonly int[] PixelSizes = { 48, 72, 96, 144 };

        // Read on Lane A workers, written on the UI thread. A double is not guaranteed atomic on
        // every architecture the runtime supports, so both ends go through Volatile — a torn read
        // here would be a garbled pixel size, which is harmless, but the discipline is free.
        private static double _renderScale = 1.0;

        /// <summary>
        /// The display scale icons should be rasterized for — <c>TopLevel.RenderScaling</c>, which
        /// is a UI-thread property and therefore cannot be read from a producer.
        /// </summary>
        /// <remarks>
        /// Set this from the UI thread when a tile attaches or its top level changes; it defaults to
        /// 1.0, which is a correct (merely slightly soft on a HiDPI display) answer until something
        /// says otherwise. It is deliberately a single process-wide value rather than a per-request
        /// one: an icon is shared by thousands of rows, and the cost of a mixed-DPI session getting
        /// one monitor's icons at the other monitor's scale is a marginally different resample of a
        /// 36x36 logical square — far less than doubling the icon cache to be exactly right.
        /// </remarks>
        public static double RenderScale
        {
            get => Volatile.Read(ref _renderScale);
            set
            {
                // A top level mid-teardown can report 0, and a bad monitor profile can report
                // something absurd; neither should be able to pick a rasterization size.
                if (Double.IsFinite(value) && value > 0)
                    Volatile.Write(ref _renderScale, Math.Clamp(value, 0.5, 8.0));
            }
        }

        /// <summary>
        /// The rasterization size for a display scale: the smallest of <see cref="PixelSizes"/> that
        /// covers the size the tile actually draws the icon at
        /// (<see cref="PreviewFormat.IconLogicalPx"/>) on that display, so the artwork is never
        /// upsampled by the time it reaches the screen. Quantizing off the artwork's 48-unit
        /// viewBox instead would under-render every icon the moment the tile draws one larger
        /// than 48 logical, which it does.
        /// </summary>
        public static int QuantizePixelSize(double renderScaling)
        {
            if (!Double.IsFinite(renderScaling) || renderScaling <= 0)
                renderScaling = 1.0;

            double wanted = PreviewFormat.IconLogicalPx * renderScaling;

            foreach (int size in PixelSizes)
            {
                if (size >= wanted)
                    return size;
            }

            return PixelSizes[PixelSizes.Length - 1];
        }

        /// <summary>
        /// Produces the file-type icon for a resolved source. Never null in practice — the renderer
        /// degrades a missing, malformed or unparsable asset to a drawn page rather than failing —
        /// so a null here means Skia could not give us a raster surface at all, which is a
        /// process-level condition and not something the engine should negative-cache.
        /// </summary>
        public static PreviewPixels Produce(in PreviewSource source, PreviewRequest request, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return null;

            try
            {
                return FileIconRenderer.Render(TagFor(source, request), QuantizePixelSize(RenderScale));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("FileIconPreviewProducer: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The icon tag for a resolved source. The extension is the better answer whenever there is
        /// one — it is what distinguishes a .psd upload from a .zip — and ContentKind is the
        /// fallback for a session that never had a file name at all.
        /// </summary>
        /// <remarks>
        /// The resolver deliberately leaves the extension null rather than choosing a tag itself, so
        /// that the icon catalog stays a concern of this layer. An extension that survives the
        /// resolver but normalizes away to nothing (a name that is all punctuation, say) is treated
        /// as no extension at all: falling through to ContentKind keeps at least the image / video /
        /// text distinction, where <see cref="FileIconCatalog.TagForExtension"/> would have
        /// collapsed it to the blank page.
        /// </remarks>
        private static string TagFor(in PreviewSource source, PreviewRequest request)
        {
            var extension = source.Extension;
            if (!String.IsNullOrEmpty(extension) && FileIconCatalog.NormalizeExtension(extension).Length > 0)
                return FileIconCatalog.TagForExtension(extension);

            return request == null
                ? FileIconCatalog.TagForContentKind(null, false)
                : FileIconCatalog.TagForContentKind(request.ContentKind, request.IsVideoProject);
        }
    }
}
