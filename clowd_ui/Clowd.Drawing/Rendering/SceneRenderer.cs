using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Options for one <see cref="SceneRenderer"/> pass. Screen: UiScale = CanvasUiElementScale,
    /// DrawChrome = true, Offset = (0,0), Background = null, ArtworkBackground fills
    /// ContentBounds. Export: UiScale = (1,1), DrawChrome = false, Offset =
    /// (-bounds.Left, -bounds.Top), Background brush fills the full bitmap. Hovered is the
    /// graphic the pointer tool would select on click, outlined on top of the scene (screen only);
    /// HoveredSet is the graphics the eraser's marquee encloses, each outlined the same way.
    /// </summary>
    internal readonly record struct SceneRenderOptions(
        DpiScale UiScale,
        bool DrawChrome,
        Vector Offset,
        IBrush Background,
        Color ArtworkBackground,
        Rect ContentBounds,
        GraphicBase Hovered = null,
        IReadOnlySet<GraphicBase> HoveredSet = null);

    /// <summary>
    /// The single render pass for the whole document (final-design §A.2) — background fill,
    /// drop shadows, graphics and selection chrome interleaved in z-order (list order).
    /// This one pass serves BOTH the screen (<see cref="ArtworkView"/>) and the export path, so
    /// screen and export match by construction.
    ///
    /// Drop shadows are drawn live: each shadowed graphic's silhouette goes into a Skia layer
    /// that is tinted and then blurred by an image filter when the layer is restored — on the GPU
    /// for the screen, and by Skia's CPU rasterizer for an export. Nothing is baked or cached, so
    /// zoom, edits and drags cost a re-record and no more.
    ///
    /// Render is PURE: it never raises PropertyChanged. Lazy cache
    /// builds inside the pass (geometry, FormattedText) write RenderCache fields only — the
    /// polyline "no PropertyChanged during render" rule, generalized to the whole pass.
    /// </summary>
    internal static class SceneRenderer
    {
        // drop shadow parameters (§2.5) — the spec look, shared by screen and export
        internal const double ShadowOffsetX = 1.414;
        internal const double ShadowOffsetY = 1.414;
        // Avalonia's Skia backend turns a blur radius into sigma = 0.288675·r + 0.5, so r = 10
        // gives sigma ≈ 3.39 — the blur of the original look
        internal const double ShadowBlurRadius = 10;
        internal const byte ShadowAlpha = 0x80;

        private static readonly IEffect ShadowBlur = new ImmutableBlurEffect(ShadowBlurRadius);

        // the shadow color as a 1x1 bitmap, so it can be drawn SourceIn over the silhouette:
        // DrawingContext has no color filter, but bitmaps take a blend mode. Created on first
        // draw, since a bitmap needs the render platform up.
        private static Bitmap _shadowTint;

        private static Bitmap CreateShadowTint()
        {
            var bmp = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock())
                System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, ShadowAlpha << 24); // premultiplied black
            return bmp;
        }

        /// <summary>The margin around a graphic's bounds, in canvas units, that holds its whole
        /// shadow: the blur falloff (3 sigma), any stroke drawn outside Bounds, and the offset.</summary>
        internal static double ShadowPad(double lineWidth) =>
            Math.Ceiling((0.288675 * ShadowBlurRadius + 0.5) * 3 + lineWidth + 2);

        public static void Render(DrawingContext ctx, IReadOnlyList<GraphicBase> graphics,
                                  in SceneRenderOptions o)
        {
            // export: the background brush covers the full bitmap, in bitmap space (before the
            // content translation), matching the old full-size background Border
            if (!o.DrawChrome && o.Background != null)
                ctx.FillRectangle(o.Background,
                                  new Rect(0, 0, Math.Ceiling(o.ContentBounds.Width), Math.Ceiling(o.ContentBounds.Height)));

            if (o.Offset != default)
            {
                using (ctx.PushTransform(Matrix.CreateTranslation(o.Offset.X, o.Offset.Y)))
                    RenderContent(ctx, graphics, in o);
            }
            else
            {
                RenderContent(ctx, graphics, in o);
            }
        }

        private static void DrawShadow(DrawingContext ctx, GraphicBase g)
        {
            var layer = g.Bounds.Inflate(ShadowPad(g.LineWidth));

            // the layer collects the silhouette at its own alpha, SourceIn then replaces its color
            // with the shadow color (keeping the coverage), and the blur runs as the layer is restored
            using (ctx.PushTransform(Matrix.CreateTranslation(ShadowOffsetX, ShadowOffsetY)))
            using (ctx.PushEffect(ShadowBlur, layer))
            {
                g.DrawShadowSilhouette(ctx);
                using (ctx.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.SourceIn }))
                    ctx.DrawImage(_shadowTint ??= CreateShadowTint(), new Rect(0, 0, 1, 1), layer);
            }
        }

        // the hover outline's width on screen, in device-independent pixels
        private const double HoverOutlineWidth = 1.5;

        private static void RenderContent(DrawingContext ctx, IReadOnlyList<GraphicBase> graphics,
                                          in SceneRenderOptions o)
        {
            GraphicBase hovered = null;
            List<GraphicBase> hoveredSet = null; // allocated only while a marquee hover set exists

            // screen: the first fill of the pass absorbs the old ArtworkBackgroundVisual — there
            // is no separate visual to invalidate, so the R5 cascade is structurally impossible
            if (o.DrawChrome)
                ctx.FillRectangle(RenderResources.GetBrush(o.ArtworkBackground), o.ContentBounds);

            // graphics in list order (z-order == list order); chrome interleaved so a graphic
            // above a selected one still occludes its trackers, exactly as before
            for (int i = 0; i < graphics.Count; i++)
            {
                var g = graphics[i];
                if (!o.DrawChrome && g is GraphicSelectionRectangle)
                    continue; // the marquee is never exported

                if (g.Hidden)
                    continue; // hidden graphics neither render nor export, and cast no shadow

                // the outline means "a click selects this", so a selected graphic never shows it
                // (a click there keeps the selection). Also only while the graphic is still in the
                // list, visible and canvas-selectable, and not open in the in-place text editor.
                if (o.DrawChrome && !g.IsSelected && !g.Locked && g is not GraphicText { Editing: true })
                {
                    if (ReferenceEquals(g, o.Hovered))
                        hovered = g;
                    else if (o.HoveredSet != null && o.HoveredSet.Contains(g))
                        (hoveredSet ??= new List<GraphicBase>()).Add(g);
                }

                // while a text graphic is being edited the screen pass hides its text (the editor
                // overlay shows the live text), so a shadow cast by the text would be a ghost of
                // the committed body. Skip on the chrome (screen) path only; export draws the full
                // text, and undo/commit resets Editing.
                bool hideEditingTextShadow = o.DrawChrome && g is GraphicText { Editing: true, ShadowIncludesText: true };

                if (!hideEditingTextShadow && g.DropShadowEffect)
                    DrawShadow(ctx, g); // under the ink

                if (o.DrawChrome)
                    g.Draw(ctx, o.UiScale); // object + selection chrome
                else
                    g.DrawObject(ctx); // export: ink only
            }

            // on top of everything, so the outline shows what a click picks even where graphics
            // above it cover part of it
            if (hovered == null && hoveredSet == null)
                return;

            var hoverPen = RenderResources.GetPen(GraphicBase.HandleColor, HoverOutlineWidth * o.UiScale.DpiScaleX,
                                                  lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            if (hovered != null)
                hovered.DrawHoverOutline(ctx, hoverPen);

            if (hoveredSet != null)
                foreach (var g in hoveredSet)
                    g.DrawHoverOutline(ctx, hoverPen);
        }
    }
}
