using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
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
    /// baked shadow sprites, graphics and selection chrome interleaved in z-order (list order).
    /// This one pass serves BOTH the screen (<see cref="ArtworkView"/>) and the export path, so
    /// screen and export match by construction.
    ///
    /// Render is PURE: it never raises PropertyChanged and never bakes shadows. Lazy cache
    /// builds inside the pass (geometry, FormattedText) write RenderCache fields only — the
    /// polyline "no PropertyChanged during render" rule, generalized to the whole pass.
    /// </summary>
    internal static class SceneRenderer
    {
        public static void Render(DrawingContext ctx, IReadOnlyList<GraphicBase> graphics,
                                  ShadowSpriteCache shadows, in SceneRenderOptions o)
        {
            // export: the background brush covers the full bitmap, in bitmap space (before the
            // content translation), matching the old full-size background Border
            if (!o.DrawChrome && o.Background != null)
                ctx.FillRectangle(o.Background,
                                  new Rect(0, 0, Math.Ceiling(o.ContentBounds.Width), Math.Ceiling(o.ContentBounds.Height)));

            if (o.Offset != default)
            {
                using (ctx.PushTransform(Matrix.CreateTranslation(o.Offset.X, o.Offset.Y)))
                    RenderContent(ctx, graphics, shadows, in o);
            }
            else
            {
                RenderContent(ctx, graphics, shadows, in o);
            }
        }

        // the hover outline's width on screen, in device-independent pixels
        private const double HoverOutlineWidth = 1.5;

        private static void RenderContent(DrawingContext ctx, IReadOnlyList<GraphicBase> graphics,
                                          ShadowSpriteCache shadows, in SceneRenderOptions o)
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
                    continue; // hidden graphics neither render nor export, and their shadow is not blitted

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

                // while a text graphic is being edited the screen pass hides its text, but a sprite
                // cast by the text (ShadowIncludesText) was baked from the committed text — blitting it would show
                // a ghost shadow of the OLD body. Skip on the chrome (screen) path only; export
                // draws the full text, and undo/commit resets Editing.
                bool hideEditingTextShadow = o.DrawChrome && g is GraphicText { Editing: true, ShadowIncludesText: true };

                if (!hideEditingTextShadow && g.DropShadowEffect && shadows != null && shadows.TryGet(g, out var sprite))
                    ctx.DrawImage(sprite.Bitmap, sprite.GetDestRect(g)); // canvas-space blit under the ink

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
