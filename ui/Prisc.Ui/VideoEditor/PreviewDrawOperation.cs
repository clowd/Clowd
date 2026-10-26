using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using SkiaSharp;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The preview's picture: <c>FrameComposer.Compose</c> straight onto Avalonia's own leased
    /// <see cref="SKCanvas"/>, inside Avalonia's render pass. No intermediate bitmap and no second
    /// GPU context — the decoded frames are uploaded into textures on Avalonia's context (inside
    /// the lease, which is the only place that is legal) and the composed frame never leaves the
    /// GPU. When Avalonia is running its software renderer the lease yields a null
    /// <see cref="GRContext"/> and the very same code composes raster images instead.
    ///
    /// The operation is created fresh for every render pass and carries only immutable references;
    /// the one piece of cross-frame state — the texture cache — lives in the shared
    /// <see cref="PreviewGpuState"/>.
    /// </summary>
    internal sealed class PreviewDrawOperation : ICustomDrawOperation
    {
        private readonly PreviewGpuState _gpu;
        private readonly CompositionPlayer _player;
        private readonly Project _project;
        private readonly Rect _videoRect;

        /// <param name="bounds">The control's local bounds (Avalonia's dirty-rect bookkeeping).</param>
        /// <param name="videoRect">The letterboxed video rectangle, in the control's coordinates:
        /// the project's output canvas is composed into exactly this rectangle.</param>
        public PreviewDrawOperation(Rect bounds, Rect videoRect, PreviewGpuState gpu,
            CompositionPlayer player, Project project)
        {
            Bounds = bounds;
            _videoRect = videoRect;
            _gpu = gpu;
            _player = player;
            _project = project;
            _gpu.AddRef();
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation other) => false;

        /// <summary>Avalonia disposes every operation it took, on its render thread — which is the
        /// only thread allowed to release the GPU textures. That is why the texture cache is
        /// reference-counted through the operations rather than disposed by the control.</summary>
        public void Dispose() => _gpu.Release();

        public void Render(ImmediateDrawingContext context)
        {
            if (_project == null || _videoRect.Width < 1 || _videoRect.Height < 1)
                return;

            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null)
                return; // not the Skia backend — nothing we can compose onto

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            if (canvas == null)
                return;

            if (!_player.TryGetFrameSource(out var frames, out long timeTicks))
                return;

            try
            {
                Compose(canvas, lease.GrContext, frames, timeTicks);
            }
            catch (ObjectDisposedException)
            {
                // the window closed between this operation being queued and the render thread
                // reaching it; there is nothing left to draw and nothing to report.
            }
        }

        private void Compose(SKCanvas canvas, GRContext context, PlaybackFrameSource frames, long timeTicks)
        {
            // At the very end of the timeline no item covers the instant (items are half-open), so
            // composing there would clear to black. Hold the last frame instead, which is what the
            // paused/ended preview showed before.
            long duration = _project.GetDurationTicks();
            if (duration > 0 && timeTicks >= duration)
                timeTicks = duration - 1;

            // Texture upload is context-affine: it happens here, inside the lease, on Avalonia's
            // render thread — never on a decode thread.
            var cache = _gpu.GetCache(context);
            frames.Pump(cache);

            // Compose at preview resolution (the letterboxed rectangle), not at output resolution:
            // the model's geometry is normalized, so the picture is simply smaller. Decode is
            // already capped by VideoOpenOptions.MaxPresentHeight.
            int width = Math.Max(1, (int)Math.Round(_videoRect.Width));
            int height = Math.Max(1, (int)Math.Round(_videoRect.Height));

            int save = canvas.Save();
            try
            {
                // Compose clears to black; the clip keeps that inside the video rectangle so the
                // letterbox stays the panel's own background.
                canvas.ClipRect(SKRect.Create(
                    (float)_videoRect.X, (float)_videoRect.Y,
                    (float)_videoRect.Width, (float)_videoRect.Height));
                canvas.Translate((float)_videoRect.X, (float)_videoRect.Y);
                canvas.Scale((float)(_videoRect.Width / width), (float)(_videoRect.Height / height));

                FrameComposer.Compose(_project, timeTicks, frames, canvas, width, height);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }
    }

    /// <summary>
    /// The preview's cross-frame GPU state: one <see cref="FrameTextureCache"/> bound to whatever
    /// <see cref="GRContext"/> Avalonia is currently leasing.
    ///
    /// Lifetime is reference-counted rather than owned by the control, because the cached images
    /// are context-affine and may only be released on the render thread: the control holds one
    /// reference (dropped when it leaves the visual tree) and every in-flight draw operation holds
    /// another, so the last operation Avalonia disposes — on its render thread — is what frees the
    /// textures. If nothing was ever rendered the control's own release frees an empty cache.
    /// </summary>
    internal sealed class PreviewGpuState
    {
        private readonly LeasedSurfaceFactory _factory = new LeasedSurfaceFactory();
        private FrameTextureCache _cache;   // render thread only
        private GRContext _context;         // render thread only
        private int _refs = 1;              // the owning control's reference

        /// <summary>The cache for the leased context, recreated when the context changes (renderer
        /// restart, device loss) because textures never survive their context. Call only inside a
        /// Skia lease.</summary>
        public FrameTextureCache GetCache(GRContext context)
        {
            if (_cache != null && !ReferenceEquals(_context, context))
            {
                _cache.Dispose();
                _cache = null;
            }

            _context = context;
            _factory.Context = context;
            return _cache ??= new FrameTextureCache(_factory);
        }

        public void AddRef() => Interlocked.Increment(ref _refs);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0)
            {
                _cache?.Dispose();
                _cache = null;
                _context = null;
            }
        }

        /// <summary>Drops the control's own reference (leaving the visual tree / window close).</summary>
        public void Shutdown() => Release();
    }

    /// <summary>
    /// Adapts Avalonia's leased <see cref="GRContext"/> to the SDK's surface-factory contract.
    /// <see cref="FrameTextureCache"/> only ever asks a factory for its context — surfaces are the
    /// render path's business, and the preview draws onto Avalonia's own — so the surface members
    /// are deliberately unimplemented rather than quietly wrong.
    /// </summary>
    internal sealed class LeasedSurfaceFactory : ISurfaceFactory
    {
        public string BackendName => Context != null ? "Avalonia GPU" : "Avalonia CPU";

        /// <summary>The context of the current lease; null under Avalonia's software renderer, in
        /// which case the cache keeps raster images.</summary>
        public GRContext Context { get; set; }

        public SKSurface CreateSurface(int width, int height) =>
            throw new NotSupportedException("The preview composes onto Avalonia's own surface.");

        public bool TryReadPixels(SKSurface surface, int width, int height, IntPtr dst, int rowBytes) =>
            throw new NotSupportedException("The preview never reads pixels back.");

        public void Dispose()
        {
            // the context belongs to Avalonia.
            Context = null;
        }
    }
}
