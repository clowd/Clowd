using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing
{
    public sealed class GraphicCollection : SimpleNotifyObject, ICollection<GraphicBase>
    {
        public int Count => _graphics.Count;

        public bool IsReadOnly => false;

        /// <summary>
        /// Self-validating getter (final-design §A.4): selection changes only flag
        /// <c>_selectionDirty</c>; the array is rebuilt (and PropertyChanged raised if it
        /// actually changed) either on the next read — so every synchronous consumer sees
        /// today's values at today's call sites — or, if nobody reads it first, on the
        /// once-per-frame validation tick.
        /// </summary>
        public GraphicBase[] SelectedItems
        {
            get
            {
                if (_selectionDirty)
                    ValidateSelectedItems();
                return _selectedItems;
            }
        }

        /// <summary>
        /// Self-validating getter (final-design §A.4): graphic mutations only flag
        /// <c>_boundsDirty</c>; the union is recomputed over the per-graphic *cached* bounds
        /// (O(n) field reads — only changed graphics recompute their own geometry) on the next
        /// read or on the frame tick, raising PropertyChanged only if the union changed
        /// (preserving the DrawingCanvas _isAutoFit clearing).
        /// </summary>
        public Rect ContentBounds
        {
            get
            {
                if (_boundsDirty)
                    ValidateContentBounds();
                return _contentBounds;
            }
        }

        internal DpiScale Dpi
        {
            get => _dpi;
            set
            {
                // zoom (or monitor DPI) changed: selection chrome draws at on-screen-constant
                // size, so the artwork re-records ONCE (final-design §A.4 "zoom re-records
                // once"). Shadows are blurred by Skia at draw time, so they need nothing here.
                if (Set(ref _dpi, value))
                    _parent.InvalidateArtwork();
            }
        }

        /// <summary>
        /// Raised on every membership/order mutation (Add/Insert/AddRange/RemoveAt/Clear — reorders
        /// are RemoveAt+Insert). Exists because <c>Count</c> alone is not a reliable reorder signal
        /// for panels: a RemoveAt+Insert reorder round-trips the count to its original value, so a
        /// panel watching the count value sees no net change. The Layers panel subscribes to this to
        /// rebuild its row list whenever the document structure changes.
        /// </summary>
        public event EventHandler StructureChanged;

        private Rect _contentBounds;
        private DpiScale _dpi;
        private GraphicBase[] _selectedItems = new GraphicBase[0];
        private readonly List<GraphicBase> _graphics;
        private readonly Dictionary<string, GraphicBase> _byId;
        private readonly DrawingCanvas _parent;

        // frame-validation dirty flags (final-design §A.4)
        private bool _boundsDirty;
        private bool _selectionDirty;
        private bool _validationScheduled;
        private readonly Action _validateAction;

        // history dirt, consumed by the undo engine via ConsumeDirty() (final-design §B.2)
        private HashSet<GraphicBase> _dirtySinceCommit = new HashSet<GraphicBase>();
        private bool _structuralDirtySinceCommit;

        public GraphicCollection(DrawingCanvas parent)
        {
            _graphics = new List<GraphicBase>();
            _byId = new Dictionary<string, GraphicBase>(StringComparer.Ordinal);
            _validateAction = Validate;
            _parent = parent;
            _dpi = parent.CanvasUiElementScale;
        }

        public void Add(GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (_byId.ContainsKey(graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            _graphics.Add(graphic);
            _byId[graphic.Id] = graphic;
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, args);
            OnStructuralChange(graphic.IsSelected);
        }

        public void Insert(int index, GraphicBase graphic)
        {
            // we should not ever allow duplicate object id's
            if (_byId.ContainsKey(graphic.Id))
                graphic.Id = Guid.NewGuid().ToString();

            _graphics.Insert(index, graphic);
            _byId[graphic.Id] = graphic;
            graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(graphic, args);
            OnStructuralChange(graphic.IsSelected);
        }

        /// <summary>
        /// Bulk add used when restoring an undo snapshot: one bounds/selection/Count invalidation
        /// for the whole batch instead of per graphic. The id index makes the duplicate check O(1)
        /// per add.
        /// </summary>
        internal void AddRange(IEnumerable<GraphicBase> graphics)
        {
            bool anySelected = false;
            foreach (var graphic in graphics)
            {
                // we should not ever allow duplicate object id's
                if (_byId.ContainsKey(graphic.Id))
                    graphic.Id = Guid.NewGuid().ToString();

                var captured = graphic;
                _graphics.Add(graphic);
                _byId[graphic.Id] = graphic;
                graphic.PropertyChanged += (sender, args) => GraphicPropertyChanged(captured, args);
                anySelected |= graphic.IsSelected;
            }

            OnStructuralChange(anySelected);
        }

        private void GraphicPropertyChanged(GraphicBase graphic, PropertyChangedEventArgs e)
        {
            // every property change of every graphic funnels through here — a drag raises several
            // of these per pointer move — so this must be O(1) (final-design §A.4). The graphic
            // already cleared its own RenderCache aspects before raising; here we only flag
            // collection-level dirt and schedule the once-per-frame validation, which performs
            // the single artwork-view invalidation. No bounds union, no LINQ, no per-visual work.
            var aspects = graphic.GetPropertyEffects(e.PropertyName);

            if ((aspects & InvalidationAspects.Bounds) != 0)
                _boundsDirty = true;

            if (e.PropertyName == nameof(GraphicBase.IsSelected))
            {
                _selectionDirty = true;
            }
            else
            {
                // history dirt (§B.2), including bare raises from Move/MoveHandleTo. IsSelected is
                // the only high-frequency [Transient] property, so it is filtered here; any other
                // transient-only dirt (e.g. GraphicText.Editing) is dropped by the history
                // engine's persisted-field compare at commit time (an empty diff).
                _dirtySinceCommit.Add(graphic);
            }

            if (e.PropertyName == nameof(GraphicBase.Id))
                RebuildIdIndex(); // the old key is unknown; id rewrites are rare (dedup runs before subscription)

            ScheduleFrameValidation();
        }

        public bool Remove(GraphicBase graphic)
        {
            var index = _graphics.IndexOf(graphic);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            var g = _graphics[index];
            g.DisconnectFromParent();
            _graphics.RemoveAt(index);
            _byId.Remove(g.Id);
            // NOTE: g intentionally stays in _dirtySinceCommit if present — a graphic edited,
            // removed and re-inserted before one commit must not lose its field dirt. The history
            // engine ignores dirt for graphics no longer in the collection.
            OnStructuralChange(g.IsSelected);
        }

        public void Clear()
        {
            _graphics.ForEach(g => g?.DisconnectFromParent());
            _graphics.Clear();
            _byId.Clear();
            OnStructuralChange(true);
        }

        /// <summary>
        /// The single structural choke point (final-design §A.4/§B.2): every membership/order
        /// mutation (Add/Insert/AddRange/RemoveAt/Clear — reorders are RemoveAt+Insert) lands
        /// here to flag history + frame dirt and raise Count.
        /// </summary>
        private void OnStructuralChange(bool selectionMayHaveChanged)
        {
            _structuralDirtySinceCommit = true;
            _boundsDirty = true;
            if (selectionMayHaveChanged)
                _selectionDirty = true;
            ScheduleFrameValidation();
            OnPropertyChanged(nameof(Count));

            // unconditional structural signal (subscribers are first-party): panels need a reorder
            // signal that Count does not provide, since a RemoveAt+Insert reorder nets no count change
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        // ====================================================================
        // FROZEN SEAM — final-design §B.2 (WP2). The history engine (WP5) builds against this
        // exact member in parallel: do not rename it, change the tuple shape, or move it off
        // GraphicCollection without coordinating with the history package.
        // ====================================================================

        /// <summary>
        /// Transfers and resets the dirt accumulated since the last consume (the commit seam,
        /// final-design §B.2):
        /// <list type="bullet">
        /// <item><c>dirtyGraphics</c> — every graphic that raised any persisted-property (or bare)
        /// PropertyChanged since the last consume. This is a SUPERSET of the real changes: it may
        /// include transient-only edits (dropped by the engine's field compare) and graphics that
        /// have since been removed (the engine only diffs graphics still present).</item>
        /// <item><c>structuralDirty</c> — true if any Add/Insert/Remove/Clear/reorder happened
        /// since the last consume; the engine then compares live id order against its committed
        /// shadow.</item>
        /// </list>
        /// </summary>
        internal (HashSet<GraphicBase> dirtyGraphics, bool structuralDirty) ConsumeDirty()
        {
            var dirty = _dirtySinceCommit;
            var structural = _structuralDirtySinceCommit;
            _dirtySinceCommit = new HashSet<GraphicBase>();
            _structuralDirtySinceCommit = false;
            return (dirty, structural);
        }

        /// <summary>
        /// O(1) lookup into the id index (kept in sync by Add/Insert/AddRange/RemoveAt/Clear and
        /// rebuilt on the rare in-collection Id rewrite). The history engine locates live
        /// instances for in-place delta application through this.
        /// </summary>
        internal bool TryGetById(string id, out GraphicBase graphic) => _byId.TryGetValue(id, out graphic);

        private void RebuildIdIndex()
        {
            _byId.Clear();
            for (int i = 0; i < _graphics.Count; i++)
                _byId[_graphics[i].Id] = _graphics[i];
        }

        // ====================================================================
        // Batch selection (final-design §A.4) — used by DrawingCanvas SelectAll/UnselectAll/
        // UnselectAllExcept. With the O(1) funnel each IsSelected write costs one flag set, so
        // select-all over the document is O(n) total with ONE SelectedItems rebuild (on the next
        // read or frame tick) instead of the old O(N²) rebuild-per-item.
        // ====================================================================

        internal void SelectAll()
        {
            for (int i = 0; i < _graphics.Count; i++)
                _graphics[i].IsSelected = true;
        }

        internal void UnselectAll()
        {
            // property setters (not field writes) so per-type side effects are preserved
            // (GraphicImage.IsSelected = false ends an active crop, exactly as before)
            for (int i = 0; i < _graphics.Count; i++)
                _graphics[i].IsSelected = false;
        }

        internal void UnselectAllExcept(GraphicBase[] excluded)
        {
            foreach (var ob in SelectedItems)
            {
                bool keep = false;
                for (int i = 0; i < excluded.Length && !keep; i++)
                    keep = ReferenceEquals(ob, excluded[i]);
                if (!keep)
                    ob.IsSelected = false;
            }
        }

        // ====================================================================
        // Frame validation (final-design §A.4)
        // ====================================================================

        /// <summary>
        /// Posts one Validate() per frame at Render priority, no matter how many property events
        /// arrive in between. Synchronous readers never wait for it — the self-validating getters
        /// recompute on read — this tick only covers the "nobody read it" case, shadow baking and
        /// the single artwork-view invalidation.
        /// </summary>
        private void ScheduleFrameValidation()
        {
            if (_validationScheduled)
                return;
            _validationScheduled = true;
            Dispatcher.UIThread.Post(_validateAction, DispatcherPriority.Render);
        }

        /// <summary>
        /// External nudge for the validator, e.g. when a tool drag ends.
        /// </summary>
        internal void RequestValidation()
        {
            // callers may have cleared per-graphic Bounds caches directly (no PropertyChanged
            // raise, e.g. the drag-end re-round in ToolPointer) — propagate to ContentBounds
            _boundsDirty = true;
            ScheduleFrameValidation();
        }

        private void Validate()
        {
            _validationScheduled = false;

            // 1. content bounds union (raise-if-changed)
            if (_boundsDirty)
                ValidateContentBounds();

            // 2. selection array rebuild (raise-if-changed)
            if (_selectionDirty)
                ValidateSelectedItems();

            // 3. ONE view invalidation for everything that happened since the last tick — the
            //    whole document re-records in a single SceneRenderer pass
            _parent.InvalidateArtwork();
        }

        private void ValidateContentBounds()
        {
            _boundsDirty = false; // before the raise, so handlers reading ContentBounds don't recurse
            Set(ref _contentBounds, GetArtworkBounds(), nameof(ContentBounds));
        }

        private void ValidateSelectedItems()
        {
            _selectionDirty = false; // before the raise, so handlers reading SelectedItems don't recurse

            int count = 0;
            for (int i = 0; i < _graphics.Count; i++)
                if (_graphics[i].IsSelected)
                    count++;

            // allocation-free when the selection (membership AND order) is unchanged
            if (count == _selectedItems.Length)
            {
                bool same = true;
                int j = 0;
                for (int i = 0; i < _graphics.Count && same; i++)
                {
                    var g = _graphics[i];
                    if (g.IsSelected)
                        same = ReferenceEquals(_selectedItems[j++], g);
                }

                if (same)
                    return;
            }

            var selected = new GraphicBase[count];
            int k = 0;
            for (int i = 0; i < _graphics.Count; i++)
                if (_graphics[i].IsSelected)
                    selected[k++] = _graphics[i];

            Set(ref _selectedItems, selected, nameof(SelectedItems));
        }

        // ====================================================================
        // Render access (final-design §A.2) — consumed by ArtworkView/SceneRenderer
        // ====================================================================

        /// <summary>
        /// The live graphics list for the render pass. Render runs on the UI thread in Avalonia's
        /// deferred model (record now, compositor replays), so reading the live list is safe.
        /// </summary>
        internal IReadOnlyList<GraphicBase> GraphicsSnapshot() => _graphics;

        /// <summary>
        /// ContentBounds WITHOUT the self-validating raise, for use inside the render pass
        /// (Render must be pure — final-design §A.2). If the bounds are dirty mid-render the
        /// union is computed locally without being committed; the already-scheduled validation
        /// tick commits it and raises PropertyChanged afterwards.
        /// </summary>
        internal Rect GetContentBoundsForRender() => _boundsDirty ? GetArtworkBounds() : _contentBounds;

        /// <summary>
        /// Renders the artwork (no selection chrome) into a 96-dpi bitmap, preserving drop
        /// shadows, via the unified <see cref="SceneRenderer"/> pass on a single transient
        /// <see cref="SceneVisual"/> (final-design §A.2 / decision table #10 &amp; #11). Screen and
        /// export therefore match by construction. External behavior is byte-identical to the old
        /// GraphicVisual-forest pipeline: 96 dpi, ceil(ContentBounds), background brush first,
        /// null if bounds &lt; 1 px, marquee excluded (SceneRenderer skips it under DrawChrome =
        /// false). Shadows are blurred by Skia in the same pass, exactly as on screen.
        /// </summary>
        public Bitmap DrawGraphicsToBitmap(IBrush background)
        {
            var bounds = ContentBounds;

            if (bounds.Width < 1 || bounds.Height < 1)
                return null;

            // Snap the export rect outward to whole pixels BEFORE it becomes the render offset.
            // ContentBounds is a union of double-precision artwork rects, so any annotation sitting
            // at a fractional coordinate used to drag the whole scene — the screenshot underneath
            // included — onto a sub-pixel offset. Images are blitted, not re-rasterized, so that
            // offset makes the sampler resample every pixel (visibly soft) and leaves a partly
            // transparent edge row. An integral offset keeps pixel-aligned raster content aligned,
            // and snapping outward (floor origin / ceil extent) can never crop.
            var left = Math.Floor(bounds.X);
            var top = Math.Floor(bounds.Y);
            bounds = new Rect(left, top, Math.Ceiling(bounds.Right) - left, Math.Ceiling(bounds.Bottom) - top);

            var width = (int)bounds.Width;
            var height = (int)bounds.Height;

            // one SceneRenderer pass: background brush over the full bitmap first, then graphics
            // (ink only, no chrome) translated from content space into bitmap space. The visual is
            // pinned at (0,0) spanning the whole bitmap so RenderTargetBitmap.Render never culls it
            // (quirk A) — the translation lives in SceneRenderOptions.Offset, not the arranged rect.
            var options = new SceneRenderOptions(
                UiScale: new DpiScale(1, 1),
                DrawChrome: false,
                Offset: new Vector(-bounds.Left, -bounds.Top),
                Background: background,
                ArtworkBackground: default,
                ContentBounds: bounds);

            var visual = new SceneVisual(width, height, _graphics, in options);
            visual.Measure(new Size(width, height));
            visual.Arrange(new Rect(0, 0, width, height));

            var bmp = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bmp.Render(visual);

            return bmp;
        }

        public GraphicBase this[int index] => _graphics[index];

        // misc ICollection
        public bool Contains(GraphicBase graphic) => _graphics.Contains(graphic);
        public void CopyTo(GraphicBase[] array, int arrayIndex) => throw new NotSupportedException();
        public int IndexOf(GraphicBase item) => _graphics.IndexOf(item);
        public int IndexOf(Predicate<GraphicBase> predicate) => _graphics.FindIndex(predicate);
        public IEnumerator<GraphicBase> GetEnumerator() => _graphics.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _graphics.GetEnumerator();

        private Rect GetArtworkBounds()
        {
            // O(n) reads of the per-graphic *cached* bounds (GraphicRenderCache) — only graphics
            // whose geometry actually changed recompute anything. Runs at most once per frame (or
            // per synchronous ContentBounds read), never per property event.
            Rect result = default;
            bool first = true;
            for (int i = 0; i < _graphics.Count; i++)
            {
                var item = _graphics[i];
                if (item is GraphicSelectionRectangle)
                    continue;
                if (item.Hidden)
                    continue; // hidden graphics don't contribute to content/export bounds

                var rect = item.Bounds;
                result = first ? rect : result.Union(rect);
                first = false;
            }

            return result;
        }

        public GraphicBase[] GetGraphicList(bool selectedOnly)
        {
            if (selectedOnly)
            {
                return _graphics
                    .Where(g => !(g is GraphicSelectionRectangle))
                    .Where(g => g.IsSelected)
                    .ToArray();
            }
            else
            {
                return _graphics
                    .Where(g => !(g is GraphicSelectionRectangle))
                    .ToArray();
            }
        }
    }
}
