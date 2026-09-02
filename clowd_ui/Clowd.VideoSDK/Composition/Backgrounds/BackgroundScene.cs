using System;
using System.Threading;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One (style, theme) of the background library as something that draws itself: a
    /// wallpaper in its own viewBox space, at a loop phase. The composer and Clowd.Ui's tiles
    /// place it with <see cref="BackgroundRenderer.DrawScene"/>; nothing else needs to know
    /// whether it is a still or a loop.
    /// </summary>
    /// <remarks>
    /// Scenes are shared process-wide from <c>BackgroundAssets</c>' cache and drawn from
    /// several threads at once (the editor's render thread, an export's composer thread, the
    /// thumbnail lane). Every implementation is immutable after construction and holds only
    /// CPU Skia objects — never anything created from a <c>GRContext</c> — so concurrent draws
    /// onto canvases of different backends are safe. <see cref="Draw"/> is a pure function of
    /// (canvas, phase): no scene keeps a clock or a last-frame state, which is what makes the
    /// preview and the export agree frame for frame.
    /// </remarks>
    public abstract class BackgroundScene
    {
        internal BackgroundScene(SKRect viewBox, long periodTicks, bool needsIsolation)
        {
            ViewBox = viewBox;
            PeriodTicks = periodTicks;
            NeedsIsolation = needsIsolation;
        }

        /// <summary>The space <see cref="Draw"/> paints in. The art covers this rectangle.</summary>
        public SKRect ViewBox { get; }

        /// <summary>True when the picture changes with phase.</summary>
        public bool IsAnimated => PeriodTicks > 0;

        /// <summary>The loop length the art itself declares, in 100ns ticks; 0 for a still.
        /// The catalog declares the same number, and a test holds the two together.</summary>
        public long PeriodTicks { get; }

        /// <summary>True when the art must be composited through its own layer — it uses blend
        /// modes that would otherwise see whatever is under the wallpaper rather than the
        /// wallpaper itself (Monterey Dark).</summary>
        public bool NeedsIsolation { get; }

        /// <summary>
        /// Draws the wallpaper in viewBox space at <paramref name="phase"/> in [0, 1) — the
        /// caller has already set the transform and clip that place it (see
        /// <see cref="BackgroundRenderer.DrawScene"/>). A static scene ignores the phase.
        /// </summary>
        public abstract void Draw(SKCanvas canvas, double phase);
    }

    /// <summary>
    /// A parsed drawing. A still is recorded into an <see cref="SKPicture"/> on its first draw
    /// and replayed thereafter; a loop walks the tree each frame, since its geometry is a
    /// function of the phase.
    /// </summary>
    /// <remarks>
    /// The picture is recorded in viewBox space, so it is resolution-independent (one picture
    /// serves the preview, the export and a tile; no size key, no preview/export divergence)
    /// and its gradient shaders are built exactly once. <see cref="SKPicture"/> playback is
    /// immutable and thread-safe; the <see cref="Lazy{T}"/> guarantees one recording however
    /// many threads race to the first draw.
    /// </remarks>
    internal sealed class SvgBackgroundScene : BackgroundScene
    {
        private readonly SvgScene _scene;
        private readonly Lazy<SKPicture> _picture;

        internal SvgBackgroundScene(SvgScene scene)
            : base(scene.ViewBox, scene.PeriodTicks, scene.HasBlendModes)
        {
            _scene = scene;
            if (!scene.IsAnimated)
                _picture = new Lazy<SKPicture>(Record, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal SvgScene Scene => _scene;

        private SKPicture Record()
        {
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(_scene.ViewBox);
            _scene.Draw(canvas, 0);
            return recorder.EndRecording();
        }

        public override void Draw(SKCanvas canvas, double phase)
        {
            if (_picture != null)
                canvas.DrawPicture(_picture.Value);
            else
                _scene.Draw(canvas, phase);
        }
    }
}
