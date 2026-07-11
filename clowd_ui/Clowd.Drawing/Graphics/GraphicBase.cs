using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    public abstract class GraphicBase : SimpleNotifyObject
    {
        public string Id
        {
            get => _id;
            set => Set(ref _id, value);
        }

        public virtual Color ObjectColor
        {
            get => _objectColor;
            set
            {
                // the shadow sprite is baked from the ink's ALPHA silhouette only, so an
                // opaque-to-opaque color change must not re-bake it (a color scrub would burn a
                // full-res bake per slider tick for a bitwise-identical sprite). Clear BEFORE the
                // raise so the collection funnel's NeedsBake probe observes the new ShadowRev.
                if (_objectColor != value && _objectColor.A != value.A)
                    RenderCache.Clear(InvalidationAspects.Shadow);
                Set(ref _objectColor, value);
            }
        }

        public virtual double LineWidth
        {
            get => _lineWidth;
            set => Set(ref _lineWidth, value);
        }

        public virtual bool DropShadowEffect
        {
            get => _dropShadowEffect;
            set => Set(ref _dropShadowEffect, value);
        }

        public virtual bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        private string _id = Guid.NewGuid().ToString();
        private Color _objectColor;
        private double _lineWidth;
        private bool _dropShadowEffect;
        [Transient] private bool _isSelected; // not persisted by GraphicsSerializer

        /// <summary>
        /// Transient sidecar of derived render state (cached bounds, geometry/text slots, shadow
        /// revision) — final-design §A.5/§C.2. Never serialized; a deserialized graphic starts
        /// with a fresh empty cache via the field initializer. See the PORT NOTEs in
        /// <see cref="GraphicRenderCache"/> for which slot a ported type uses.
        /// </summary>
        [Transient] internal readonly GraphicRenderCache RenderCache = new GraphicRenderCache();

        /// <summary>
        /// PORT NOTE (_translating fast path): Move() implementations set this to true around
        /// their field mutations + raises (try/finally), call
        /// <c>RenderCache.TranslateCachedBounds(dx, dy)</c> once, then the usual bare
        /// OnPropertyChanged(). While true, each raise clears only the Geometry aspect — bounds
        /// are offset instead of recomputed and shadow/text caches survive — so the drag hot path
        /// does zero geometry and zero shadow work per pointer event (final-design §A.4).
        /// See GraphicRectangle.Move for the exemplar.
        /// </summary>
        [Transient] protected bool _translating;

        // the resolved per-type property→aspect map (see DeclarePropertyEffects), cached on the
        // instance so the per-raise lookup is one field read + one dictionary probe
        [Transient] private Dictionary<string, InvalidationAspects> _propertyEffects;

        private static readonly ConcurrentDictionary<Type, Dictionary<string, InvalidationAspects>> _propertyEffectsByType =
            new ConcurrentDictionary<Type, Dictionary<string, InvalidationAspects>>();

        // what a bare (nameless) raise — and any property name missing from the map — invalidates
        private const InvalidationAspects ConservativeAspects =
            InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;

        protected GraphicBase()
        { }

        protected GraphicBase(Color objectColor, double lineWidth) : this(objectColor, lineWidth, true)
        { }

        protected GraphicBase(Color objectColor, double lineWidth, bool dropShadowEffect)
        {
            _objectColor = objectColor;
            _lineWidth = lineWidth;
            _dropShadowEffect = dropShadowEffect;
        }

        /// <summary>
        /// Artwork bounds in canvas space, including rotation and stroke widening. Read for every
        /// graphic whenever the content bounds are recomputed — a hot path — so the result is
        /// cached in <see cref="RenderCache"/> and reads are allocation-free field accesses after
        /// the first compute. Ported types implement <see cref="ComputeBounds"/>; legacy
        /// (not-yet-ported) types still override this getter directly and bypass the cache.
        /// </summary>
        public virtual Rect Bounds => RenderCache.CachedBounds ??= ComputeBounds();

        /// <summary>
        /// PORT NOTE (ComputeBounds): a ported graphic moves its old Bounds getter body here and
        /// deletes its Bounds override so the cached base getter takes over. ComputeBounds must
        /// never raise PropertyChanged; sidecar RenderCache fills are permitted (CachedBounds
        /// itself is such a write — GraphicPolyLine also refits GeometryBounds/GeometryTransform
        /// here so Bounds is order-independent w.r.t. rendering) — and it must produce exactly
        /// what the old getter did (rotation, stroke widening, rounding included).
        /// </summary>
        protected virtual Rect ComputeBounds() =>
            throw new NotSupportedException($"{GetType().Name} must override either ComputeBounds() (ported) or Bounds (legacy).");

        /// <summary>
        /// Current shadow revision — the shadow sprite cache keys on (Id, ShadowRev, zoomBucket).
        /// Bumped only when a Shadow-aspect property changes; never by selection or translation.
        /// </summary>
        internal int ShadowRev => RenderCache.ShadowRev;

        internal abstract int HandleCount { get; }

        internal const double UnscaledControlSize = 12.0;
        internal const double UnscaledBorderSize = 2.0;
        internal static IBrush HandleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0, 0, 255));
        internal static readonly IBrush HandleBrush2 = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        internal abstract bool Contains(Point point);
        internal abstract void Move(double deltaX, double deltaY);
        internal abstract void MoveHandleTo(Point point, int handleNumber);
        internal abstract Cursor GetHandleCursor(int handleNumber);
        internal abstract Point GetHandle(int handleNumber, DpiScale uiscale);

        internal void DisconnectFromParent() => ClearPropertyChangedHandlers();

        /// <summary>
        /// Declares which cache aspects each property invalidates when it changes (final-design
        /// §C.2). The map is built once per concrete type and merged down the hierarchy. Names
        /// missing from the map fall back to the conservative Bounds|Geometry|Shadow (same as a
        /// bare raise), so not-yet-ported types remain correct by default.
        ///
        /// PORT NOTE (aspect map entry): overrides call base.DeclarePropertyEffects(map) FIRST,
        /// then add one entry per property the type declares (e.g. GraphicLine:
        /// LineStart/LineEnd → Bounds|Geometry|Shadow; GraphicText: Body/Font* →
        /// Bounds|Geometry|Shadow|Text). Only deviate from the conservative default when it is
        /// provably safe — the canonical exceptions live here (IsSelected → None,
        /// ObjectColor → Shadow).
        /// </summary>
        internal virtual void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            map[nameof(Id)] = InvalidationAspects.None;
            map[nameof(IsSelected)] = InvalidationAspects.None; // selection never dirties caches (select-all must not queue shadow re-bakes)
            map[nameof(ObjectColor)] = InvalidationAspects.None; // ink repaints via the view invalidation; only the ALPHA feeds the shadow silhouette — the setter clears Shadow itself when alpha changes
            map[nameof(LineWidth)] = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
            map[nameof(DropShadowEffect)] = InvalidationAspects.Shadow;
        }

        /// <summary>
        /// The aspects invalidated by a change to <paramref name="propertyName"/> (null/empty =
        /// bare raise). O(1); used both by our own OnPropertyChanged override and by the
        /// collection-level invalidation funnel.
        /// </summary>
        internal InvalidationAspects GetPropertyEffects(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return ConservativeAspects; // bare raise from Move/MoveHandleTo

            var map = _propertyEffects ??= _propertyEffectsByType.GetOrAdd(
                GetType(),
                static (_, self) =>
                {
                    var m = new Dictionary<string, InvalidationAspects>(StringComparer.Ordinal);
                    self.DeclarePropertyEffects(m);
                    return m;
                },
                this);

            return map.TryGetValue(propertyName, out var aspects) ? aspects : ConservativeAspects;
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs args)
        {
            // apply the aspect map to our own cache BEFORE raising, so subscribers (the
            // collection invalidation funnel) always observe consistent state
            if (_translating)
                RenderCache.Clear(InvalidationAspects.Geometry); // Move() already offset CachedBounds; shadow/text survive a pure translation
            else
                RenderCache.Clear(GetPropertyEffects(args.PropertyName));

            base.OnPropertyChanged(args);
        }

        /// <summary>
        /// Called by the history engine after an undo/redo wrote restored values directly into
        /// fields (bypassing property setters, so no PropertyChanged fired).
        /// <paramref name="changedJsonNames"/> holds the serializer JSON names ("left", "points",
        /// "bitmapFilePath", …) of the field slots that were written. The default nukes every
        /// derived cache (and thereby bumps ShadowRev) — always safe. A type overrides this only
        /// to keep caches that provably don't depend on the changed fields (e.g. GraphicImage
        /// keeps its decoded bitmaps unless an image-affecting field changed).
        /// </summary>
        internal virtual void OnFieldsRestored(IReadOnlyCollection<string> changedJsonNames)
        {
            RenderCache.Clear(InvalidationAspects.All);
        }

        /// <summary>
        /// Drops memory-heavy transient caches. The history engine calls this on the live
        /// instances it retains for deleted graphics, so a retained 4K image costs a field
        /// record, not a decoded bitmap. Default drops the whole RenderCache; GraphicImage
        /// additionally nulls its bitmap caches.
        /// </summary>
        internal virtual void TrimTransientCaches()
        {
            RenderCache.Clear(InvalidationAspects.All);
        }

        internal virtual void Activate(DrawingCanvas canvas) { }

        internal virtual void Normalize() { }

        internal virtual int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (IsSelected)
                for (int i = 1; i <= HandleCount; i++)
                    if (GetHandleRectangle(i, uiscale).Contains(point))
                        return i;

            if (Contains(point))
                return 0;

            return -1;
        }

        internal virtual void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            DrawObject(ctx);
            if (IsSelected)
                DrawTrackers(ctx, uiscale);
        }

        internal virtual void DrawObject(DrawingContext ctx)
        { }

        protected virtual void DrawDashedBorder(DrawingContext ctx, Rect rect, double lineWidth = 2)
        {
            ctx.DrawRectangle(null, RenderResources.GetPen(Color.FromArgb(127, 255, 255, 255), lineWidth), rect);
            ctx.DrawRectangle(null, RenderResources.GetPen(Color.FromArgb(127, 0, 0, 0), lineWidth, RenderResources.Dash4x4), rect);
        }

        protected virtual void DrawTrackers(DrawingContext ctx, DpiScale uiscale)
        {
            for (int i = 1; i <= HandleCount; i++)
                DrawSingleTracker(ctx, i, uiscale);
        }

        protected virtual void DrawSingleTracker(DrawingContext ctx, int handleNum, DpiScale uiscale)
        {
            var rectangle = GetHandleRectangle(handleNum, uiscale);
            var scaledline = 1 * uiscale.DpiScaleX;
            var ellRadius = rectangle.Width / 2;
            var ellCenter = new Point(rectangle.Left + ellRadius, rectangle.Top + ellRadius);

            ctx.DrawEllipse(HandleBrush, null, ellCenter, ellRadius, ellRadius);
            ellRadius -= scaledline;
            ctx.DrawEllipse(HandleBrush2, null, ellCenter, ellRadius, ellRadius);
            ellRadius -= (scaledline * 2);
            ctx.DrawEllipse(HandleBrush, null, ellCenter, ellRadius, ellRadius);
        }

        protected virtual Rect GetHandleRectangle(int handleNumber, DpiScale uiscale)
        {
            // Handle rectangle should scale with window DPI
            Point point = GetHandle(handleNumber, uiscale);
            double size = UnscaledControlSize * uiscale.DpiScaleX;
            return new Rect(point.X - size / 2, point.Y - size / 2, size, size);
        }

        protected virtual bool SetAndNormalize<T>(ref T storage, T value, [CallerMemberName] string propertyName = null,
                                                  params string[] dependentProperties)
        {
            var changed = Set(ref storage, value, propertyName, dependentProperties);
            if (changed) Normalize();
            return changed;
        }
    }
}
