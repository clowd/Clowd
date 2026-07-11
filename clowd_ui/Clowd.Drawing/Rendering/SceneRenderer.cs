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
    /// (-bounds.Left, -bounds.Top), Background brush fills the full bitmap.
    /// </summary>
    internal readonly record struct SceneRenderOptions(
        DpiScale UiScale,
        bool DrawChrome,
        Vector Offset,
        IBrush Background,
        Color ArtworkBackground,
        Rect ContentBounds);

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

        private static void RenderContent(DrawingContext ctx, IReadOnlyList<GraphicBase> graphics,
                                          ShadowSpriteCache shadows, in SceneRenderOptions o)
        {
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

                // while a text graphic is being edited the screen pass hides its text (pastel rect
                // only) but the sprite was baked from the committed text — blitting it would show
                // a ghost shadow of the OLD body. Skip on the chrome (screen) path only; export
                // draws the full text, and undo/commit resets Editing.
                bool hideEditingTextShadow = o.DrawChrome && g is GraphicText { Editing: true };

                if (!hideEditingTextShadow && g.DropShadowEffect && shadows != null && shadows.TryGet(g, out var sprite))
                    ctx.DrawImage(sprite.Bitmap, sprite.GetDestRect(g)); // canvas-space blit under the ink

                if (o.DrawChrome)
                    g.Draw(ctx, o.UiScale); // object + selection chrome
                else
                    g.DrawObject(ctx); // export: ink only
            }
        }
    }
}
