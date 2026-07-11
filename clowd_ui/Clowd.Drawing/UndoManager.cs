using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.History;

namespace Clowd.Drawing
{
    /// <summary>The kind of history mutation that raised <see cref="UndoManager.StateChanged"/> —
    /// the autosave throttle routes on this (immediate serialize for discrete actions, trailing
    /// debounce for merge-in-place rewrites, final-design §B.6).</summary>
    internal enum HistoryChangeKind
    {
        Append,
        Merge,
        Undo,
        Redo,
    }

    /// <summary>
    /// The undo/redo engine. Public surface and merge semantics are the pre-rebuild contract
    /// (tools-history §2.5, pinned by UndoManagerTests); the internals are the delta engine of
    /// final-design §B: commits diff the live document against an incrementally-maintained
    /// <see cref="CommittedState"/> shadow using the dirt reported by the frozen
    /// <c>GraphicCollection.ConsumeDirty()</c> seam — O(changed), no JSON — and undo/redo apply
    /// field deltas in place to the SAME live instances in the SAME <see cref="GraphicCollection"/>
    /// (no deserialize, no collection swap, no visual rebuild). Deleted graphics retain their live
    /// instance in the step, so undoing a delete is a list insert — memory-heavy transients are
    /// trimmed at delete-commit via <c>TrimTransientCaches</c> and re-derived on undo (the shared
    /// decode LRU and the retained shadow sprite keep the re-insert cheap). History is capped at
    /// 200 delta steps, dropping the oldest.
    ///
    /// <see cref="StateChanged"/> payloads: append/undo/redo (discrete actions) carry a freshly
    /// serialized document exactly as before; merge-in-place raises carry a null State — nothing
    /// O(document) remains on the scrub path, and the autosave throttle (final-design §B.6)
    /// serializes on its trailing edge instead. <see cref="LastChangeKind"/> tells the canvas
    /// which case it is.
    /// </summary>
    internal class UndoManager
    {
        class GraphicState
        {
            public Color BackgroundColor { get; set; } = Colors.Transparent;
            public GraphicBase[] Graphics { get; set; } = new GraphicBase[0];
        }

        public bool CanUndo => _node?.Previous != null;

        public bool CanRedo => _node?.Next != null;

        public event EventHandler<StateChangedEventArgs> StateChanged;

        /// <summary>What the most recent <see cref="StateChanged"/> raise was (autosave routing).</summary>
        internal HistoryChangeKind LastChangeKind { get; private set; }

        /// <summary>
        /// Release escape hatch (final-design risk #5): when set, the next commit treats every
        /// live graphic as dirty and forces the structural/background compares, so a mutation that
        /// somehow bypassed the PropertyChanged funnel still lands in history.
        /// </summary>
        internal bool FullScanNextCommit { get; set; }

        /// <summary>
        /// Test/diagnostic hook: raised once per <see cref="AddCommandStep"/> with the built
        /// change set (empty for a no-op commit). The parity tests compare this against the
        /// <see cref="GetChangedNodes"/> JSON oracle over independently captured snapshots.
        /// </summary>
        internal static event Action<UndoManager, SortedSet<string>> DiagnosticCommitBuilt;

        private const int MaxSteps = 200;

        private readonly DrawingCanvas _drawingCanvas;
        private HistoryStep _node;
        private CommittedState _committed;
        private bool _canMergeNext = false;

#if DEBUG
        // the previous commit's serialized document, kept ONLY to feed the per-commit parity
        // assert (built ChangeSet vs the GetChangedNodes oracle) — final-design §B.2
        private JsonObject _debugShadowJson;
#endif

        public UndoManager(DrawingCanvas drawingCanvas)
        {
            _drawingCanvas = drawingCanvas;
            ClearHistory();
        }

        /// <summary>
        /// Resets history to a single root node. With <paramref name="initialState"/> this is the
        /// session-restore path (RestoreState → ClearHistory(data)): the state is deserialized,
        /// normalized and loaded IN PLACE into the same GraphicCollection (final-design §B.4);
        /// without it, the canvas is cleared. Never raises StateChanged (contract #23).
        /// </summary>
        public void ClearHistory(JsonObject initialState = null)
        {
            _canMergeNext = false;

            var collection = _drawingCanvas.GraphicsList;
            if (initialState != null)
            {
                var state = initialState.Deserialize<GraphicState>(GraphicsSerializer.Options);
                foreach (var s in state.Graphics)
                    s.Normalize();

                collection?.Clear();
                collection?.AddRange(state.Graphics);
                _drawingCanvas.ArtworkBackground = state.BackgroundColor;
            }
            else if (collection != null && collection.Count > 0)
            {
                // parity with the old ClearHistory(), which swapped in an empty collection
                collection.Clear();
            }

            _committed = CommittedState.Capture(_drawingCanvas);
            _node = new HistoryStep(); // root: Changes == null, so nothing can merge into it

            // loading is not user dirt
            collection?.ConsumeDirty();
            _drawingCanvas.ConsumeBackgroundDirty();

#if DEBUG
            _debugShadowJson = SerializeDocument(_drawingCanvas);
#endif
        }

        /// <summary>
        /// Commits the changes made since the last commit as one history step, merging into the
        /// current step when allowed (see the merge contract in tools-history §2.5). Cost is
        /// O(changed graphics × fields); a no-op commit adds nothing and keeps the redo branch,
        /// but still rewrites the merge chain (a non-mergable no-op breaks the chain), exactly
        /// like the old implementation.
        /// </summary>
        public void AddCommandStep(bool mergable)
        {
            var collection = _drawingCanvas.GraphicsList;

            if (_node == null || _committed == null)
            {
                // bootstrap (parity with the old `_node?.Value == null` branch — normally
                // unreachable because the constructor's ClearHistory seeds both)
                _committed = CommittedState.Capture(_drawingCanvas);
                _node = new HistoryStep();
                collection?.ConsumeDirty();
                _drawingCanvas.ConsumeBackgroundDirty();
#if DEBUG
                _debugShadowJson = SerializeDocument(_drawingCanvas);
#endif
                return;
            }

            // 'mergable=false' prevents this event from being merged with the current step
            // but also the next event from being merged with this one. Rewritten BEFORE the
            // no-op return below — a no-op non-mergable commit still breaks the merge chain.
            var canMergeWithCurrent = _canMergeNext;
            _canMergeNext = mergable;

            var (dirtyGraphics, structuralDirty) = collection.ConsumeDirty();
            var backgroundDirty = _drawingCanvas.ConsumeBackgroundDirty();

            if (FullScanNextCommit)
            {
                FullScanNextCommit = false;
                structuralDirty = true;
                backgroundDirty = true;
                foreach (var g in collection)
                    dirtyGraphics.Add(g);
            }

            var built = ChangeSetBuilder.Build(_drawingCanvas, _committed, dirtyGraphics, structuralDirty, backgroundDirty);

            DiagnosticCommitBuilt?.Invoke(this, built.Changes);
#if DEBUG
            AssertCommitParity(built.Changes);
#endif

            // do nothing if nothing was changed (transient-only dirt compares clean above).
            if (built.Changes.Count == 0)
            {
                // A value-identical same-id instance swap (public GraphicCollection seam) emits a
                // Delta with no change paths: no undo step is warranted, but the committed shadow
                // must still swap its Instance refs or it would keep the dead instance and a later
                // restore would silently resurrect it. Deltas without change paths can only be
                // instance swaps, and Order/Background are never set on an empty-changes build.
                if (built.Deltas.Count > 0)
                    ApplyToShadow(built);
                return;
            }

            ApplyToShadow(built);

            // merge the previous/next changes into a single step
            // if only the same properties were changed
            if (mergable && canMergeWithCurrent && _node.Changes?.SequenceEqual(built.Changes) == true)
            {
                FoldInto(_node, built);
                _node.Next = null;
                LastChangeKind = HistoryChangeKind.Merge;
                RaiseStateChangedEvent(null); // the autosave throttle serializes on its trailing edge
                return;
            }

            var step = new HistoryStep
            {
                Previous = _node,
                Changes = built.Changes,
                Graphics = built.Deltas.ToArray(),
                Background = built.Background,
                Order = built.Order,
            };
            _node.Next = step;
            _node = step;
            TrimToCap();

            LastChangeKind = HistoryChangeKind.Append;
            RaiseStateChangedEvent(SerializeDocument(_drawingCanvas));
        }

        /// <summary>
        /// Computes the set of changed property paths between two state snapshots, at per-property
        /// granularity (one path per changed leaf, recursing into objects/arrays). Array items that
        /// are objects carrying an "id" property (the graphics) are keyed by that id, so per-graphic
        /// edits diff against the same graphic; a pure reorder (same ids, different order — i.e. a
        /// z-order change) is reported as a single "(order)" path on the array. Other array items
        /// are keyed positionally ("item.N"). The undo merge logic compares these sets to decide
        /// whether consecutive edits collapse into one step.
        ///
        /// This is no longer on the commit hot path (the delta engine builds the same set from
        /// field records), but it remains the machine-checked grammar oracle: a DEBUG assert
        /// verifies every commit's built change set against it, and HistoryParityTests fuzz the
        /// two against each other.
        /// </summary>
        internal static SortedSet<string> GetChangedNodes(JsonObject prev, JsonObject next)
        {
            // this runs on every command step (including each merged step of a drag), so it is
            // written iteratively over materialized child arrays with plain string paths — no
            // LINQ/iterator allocations per node.
            var changes = new SortedSet<string>(StringComparer.Ordinal);
            DiffChildren("root", prev, next, changes);
            return changes;
        }

        private static bool HasChildren(JsonNode node) => node is JsonObject || node is JsonArray;

        private static string GetItemName(JsonNode node, int index)
        {
            if (node is JsonObject obj &&
                obj.TryGetPropertyValue("id", out var id) &&
                id is JsonValue value &&
                value.TryGetValue<string>(out var name))
            {
                return name;
            }

            return "item." + index;
        }

        private static KeyValuePair<string, JsonNode>[] GetNamedChildren(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var children = new KeyValuePair<string, JsonNode>[obj.Count];
                int i = 0;
                foreach (var kvp in obj)
                    children[i++] = kvp;
                return children;
            }

            if (node is JsonArray arr)
            {
                var children = new KeyValuePair<string, JsonNode>[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    children[i] = new KeyValuePair<string, JsonNode>(GetItemName(arr[i], i), arr[i]);
                return children;
            }

            return Array.Empty<KeyValuePair<string, JsonNode>>();
        }

        private static void DiffChildren(string path, JsonNode prevEl, JsonNode nextEl, SortedSet<string> changes)
        {
            var prevChildren = GetNamedChildren(prevEl);
            var nextChildren = GetNamedChildren(nextEl);

            // id-keyed matching makes a pure reorder invisible to the per-item diff, but list
            // order is the z-order of the graphics — report it explicitly. (Adds/removes change
            // membership and already produce their own paths.)
            if (prevEl is JsonArray && nextEl is JsonArray && prevChildren.Length == nextChildren.Length)
            {
                bool sameOrder = true;
                for (int i = 0; i < prevChildren.Length; i++)
                {
                    if (!string.Equals(prevChildren[i].Key, nextChildren[i].Key, StringComparison.Ordinal))
                    {
                        sameOrder = false;
                        break;
                    }
                }

                if (!sameOrder)
                {
                    var prevNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var c in prevChildren)
                        prevNames.Add(c.Key);

                    bool sameMembers = true;
                    foreach (var c in nextChildren)
                    {
                        if (!prevNames.Contains(c.Key))
                        {
                            sameMembers = false;
                            break;
                        }
                    }

                    if (sameMembers)
                        changes.Add(path + "/(order)");
                }
            }

            // add all of prev properties to dictionary
            var dict = new Dictionary<string, JsonNode>(prevChildren.Length, StringComparer.Ordinal);
            foreach (var e in prevChildren)
                dict.Add(e.Key, e.Value);

            // iterate next properties, find matches in dictionary
            foreach (var e in nextChildren)
            {
                var elPath = path + "/" + e.Key;

                if (!dict.TryGetValue(e.Key, out var ePrev))
                {
                    // prev does not contain this property
                    changes.Add(elPath);
                    continue;
                }

                dict.Remove(e.Key);

                if (HasChildren(ePrev) != HasChildren(e.Value))
                {
                    // the structure of this element has changed
                    changes.Add(elPath);
                }
                else if (HasChildren(ePrev))
                {
                    // they both have children to check
                    DiffChildren(elPath, ePrev, e.Value, changes);
                }
                else if (!JsonNode.DeepEquals(ePrev, e.Value))
                {
                    // they both have an absolute value and it's changed
                    changes.Add(elPath);
                }
            }

            // anything not removed from the dictionary was not in 'next'
            foreach (var e in dict)
                changes.Add(path + "/" + e.Key);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            RevertUncommitted();

            var step = _node;
            ApplyStep(step, undo: true);
            _node = step.Previous;

#if DEBUG
            _debugShadowJson = SerializeDocument(_drawingCanvas);
#endif
            LastChangeKind = HistoryChangeKind.Undo;
            RaiseStateChangedEvent(SerializeDocument(_drawingCanvas));
        }

        public void Redo()
        {
            if (!CanRedo)
                return;

            RevertUncommitted();

            var step = _node.Next;
            ApplyStep(step, undo: false);
            _node = step;

#if DEBUG
            _debugShadowJson = SerializeDocument(_drawingCanvas);
#endif
            LastChangeKind = HistoryChangeKind.Redo;
            RaiseStateChangedEvent(SerializeDocument(_drawingCanvas));
        }

        /// <summary>
        /// Serializes the canvas document as the persisted <c>{BackgroundColor, Graphics[]}</c>
        /// shape (byte-compatible with graphics.json and the old StateUpdated payloads). Used for
        /// the discrete-action StateChanged payloads, by the autosave throttle's trailing edge,
        /// and by the parity tests.
        /// </summary>
        internal static JsonObject SerializeDocument(DrawingCanvas canvas)
        {
            var state = new GraphicState
            {
                BackgroundColor = canvas.ArtworkBackground,
                Graphics = canvas.GraphicsList?.GetGraphicList(false) ?? new GraphicBase[0],
            };
            return (JsonObject)JsonSerializer.SerializeToNode(state, GraphicsSerializer.Options);
        }

        private void RaiseStateChangedEvent(JsonObject state)
        {
            StateChanged?.Invoke(this, new StateChangedEventArgs(state));
        }

        // ====================================================================
        // history.json (persistent undo — MIGRATION.md §8.8)
        // ====================================================================

        /// <summary>Serializes the full undo chain (root→tail, cursor included) for history.json.
        /// The autosave throttle attaches this to every StateUpdated raise that carries a
        /// document, so graphics.json and history.json always move in lockstep.</summary>
        internal JsonObject SerializeHistory() => HistorySerializer.Serialize(_node, _committed);

        /// <summary>
        /// Replaces the (freshly reset) history with a chain rehydrated from
        /// <paramref name="history"/>, positioned at its saved cursor. The file is trusted only
        /// after replaying baseline→cursor reproduces <paramref name="expectedDocument"/> exactly
        /// (an empty <see cref="GetChangedNodes"/> diff — graphics.json is the authority); any
        /// parse, shape or replay failure returns false and leaves the current empty history
        /// untouched. Never raises StateChanged (contract #23 extends to history loading).
        ///
        /// The committed shadow is NOT rebuilt here: it already mirrors the live document seeded
        /// by ClearHistory, which is the engine invariant that matters. The loaded records can
        /// differ from it by Normalize ulps; the first apply absorbs that exactly like an
        /// in-session apply would. The load boundary is non-mergable (<c>_canMergeNext</c> stays
        /// false), so the first post-load commit always appends — a fresh merge chain.
        /// </summary>
        internal bool TryRehydrateHistory(JsonObject history, JsonObject expectedDocument)
        {
            if (history == null || expectedDocument == null)
                return false;

            try
            {
                var parsed = HistorySerializer.Deserialize(history, MaxSteps);
                var replayed = HistorySerializer.ReplayToCursor(parsed);

                // fast path: the raw cursor records equal graphics.json byte-for-byte (every
                // close that followed a plain commit). Otherwise the document may have drifted
                // off the records only through Normalize's non-idempotent derived fields (undo
                // re-Normalizes; a text's CenterOfRotation is chronically one Normalize behind)
                // — those are still the same document, so compare both sides after the SAME
                // treatment: the normalize-materialized replay against the live document (which
                // RestoreState just built by normalizing graphics.json). Anything that still
                // differs is a real inconsistency (stale/tampered file, e.g. a crash between
                // the graphics/history writes).
                if (GetChangedNodes(HistorySerializer.SerializeReplayed(replayed, normalize: false), expectedDocument).Count != 0 &&
                    GetChangedNodes(HistorySerializer.SerializeReplayed(replayed, normalize: true),
                                    SerializeDocument(_drawingCanvas)).Count != 0)
                {
                    return false;
                }

                var node = new HistoryStep(); // fresh root: Changes == null, nothing merges into it
                var cursor = node;
                for (int i = 0; i < parsed.Steps.Count; i++)
                {
                    var step = parsed.Steps[i];
                    step.Previous = node;
                    node.Next = step;
                    node = step;
                    if (i + 1 == parsed.Cursor)
                        cursor = step;
                }

                _node = cursor;
                _canMergeNext = false;
                return true;
            }
            catch
            {
                return false; // corrupt/truncated file → today's exact behavior: empty history
            }
        }

        // ====================================================================
        // Commit internals
        // ====================================================================

        /// <summary>Moves the committed shadow to the just-built change set's After side.</summary>
        private void ApplyToShadow(ChangeSet built)
        {
            foreach (var delta in built.Deltas)
            {
                if (delta.After == null)
                    _committed.ById.Remove(delta.Id);
                else
                    _committed.ById[delta.Id] = delta.After;
            }

            if (built.Order.HasValue)
                _committed.Order = built.Order.Value.After;
            if (built.Background.HasValue)
                _committed.Background = built.Background.Value.After;
        }

        /// <summary>
        /// Merge-in-place (final-design §B.2 step 5): the current step absorbs the new captures —
        /// its Before side (the state one undo reverts to) is kept, its After side is rewritten.
        /// Because equal change sets imply matching delta ids, this is a per-id After swap; an
        /// add folded with its own remove cancels out entirely.
        /// </summary>
        private static void FoldInto(HistoryStep node, ChangeSet built)
        {
            var deltas = new List<GraphicDelta>(node.Graphics);
            foreach (var nd in built.Deltas)
            {
                GraphicDelta existing = null;
                for (int i = 0; i < deltas.Count && existing == null; i++)
                    if (string.Equals(deltas[i].Id, nd.Id, StringComparison.Ordinal))
                        existing = deltas[i];

                if (existing == null)
                {
                    deltas.Add(nd);
                }
                else
                {
                    existing.After = nd.After;
                    if (nd.Index >= 0)
                        existing.Index = nd.Index;
                }
            }

            deltas.RemoveAll(d => d.Before == null && d.After == null); // add folded with remove
            node.Graphics = deltas.ToArray();

            if (built.Order.HasValue)
            {
                var before = node.Order?.Before ?? built.Order.Value.Before;
                var after = built.Order.Value.After;
                node.Order = before.AsSpan().SequenceEqual(after) ? default((string[], string[])?) : (before, after);
            }

            if (built.Background.HasValue)
            {
                var before = node.Background?.Before ?? built.Background.Value.Before;
                var after = built.Background.Value.After;
                node.Background = before == after ? default((Color, Color)?) : (before, after);
            }

            node.CachedJson = null; // the fold rewrote the step — re-serialize on the next emission
        }

        /// <summary>
        /// History cap (final-design §B.5): each delta step carries before AND after, so steps are
        /// independently invertible — dropping the oldest merely shortens how far undo reaches.
        /// The dropped transition's retained instances get their transient caches trimmed unless
        /// they are back in the live document.
        /// </summary>
        private void TrimToCap()
        {
            int depth = 0;
            var root = _node;
            while (root.Previous != null)
            {
                root = root.Previous;
                depth++;
            }

            var collection = _drawingCanvas.GraphicsList;
            while (depth > MaxSteps)
            {
                var oldest = root.Next; // the transition root→root.Next is the oldest step
                foreach (var delta in oldest.Graphics)
                {
                    TrimIfNotLive(collection, delta.Before);
                    TrimIfNotLive(collection, delta.After);
                }

                oldest.Graphics = Array.Empty<GraphicDelta>();
                oldest.Order = null;
                oldest.Background = null;
                oldest.Changes = null; // it is the new root; nothing may merge into it from below
                oldest.Previous = null;
                oldest.CachedJson = null;
                oldest.CachedBaselineJson = null; // the new root's baseline is a different state
                root = oldest;
                depth--;
            }
        }

        private static void TrimIfNotLive(GraphicCollection collection, FieldRecord record)
        {
            var instance = record?.Instance;
            if (instance == null)
                return;
            if (collection.TryGetById(instance.Id, out var live) && ReferenceEquals(live, instance))
                return;
            instance.TrimTransientCaches();
        }

#if DEBUG
        /// <summary>
        /// The grammar oracle (final-design §B.2): every commit's built change set must set-equal
        /// what the retained JSON diff reports over full snapshots. Runs in Debug/test builds only
        /// — the whole test suite runs in Debug, so any drift fails loudly.
        /// </summary>
        private void AssertCommitParity(SortedSet<string> built)
        {
            var nextJson = SerializeDocument(_drawingCanvas);
            var oracle = GetChangedNodes(_debugShadowJson, nextJson);
            if (!oracle.SetEquals(built))
            {
                throw new InvalidOperationException(
                    "History parity violation — the delta engine's change set disagrees with the GetChangedNodes oracle.\n" +
                    $"  oracle: [{string.Join(", ", oracle)}]\n" +
                    $"  built:  [{string.Join(", ", built)}]");
            }

            _debugShadowJson = nextJson;
        }
#endif

        // ====================================================================
        // Restore internals — deltas applied in place (final-design §B.4)
        // ====================================================================

        /// <summary>
        /// Discards edits made since the last commit before an undo/redo lands, replicating the
        /// old snapshot-restore semantics (a full restore overwrote uncommitted scratch too).
        /// Reverts field drift back to the committed records, drops uncommitted adds, re-inserts
        /// uncommitted deletes and restores the committed order. Usually a no-op.
        /// </summary>
        private void RevertUncommitted()
        {
            var collection = _drawingCanvas.GraphicsList;
            var (dirtyGraphics, structuralDirty) = collection.ConsumeDirty();
            var backgroundDirty = _drawingCanvas.ConsumeBackgroundDirty();

            foreach (var g in dirtyGraphics)
            {
                if (g is GraphicSelectionRectangle)
                    continue;
                if (!collection.TryGetById(g.Id, out var live) || !ReferenceEquals(live, g))
                {
                    structuralDirty = true; // removed since the commit — membership reconcile below
                    continue;
                }

                if (!_committed.ById.TryGetValue(g.Id, out var rec) ||
                    !ReferenceEquals(rec.Instance, g) ||
                    !ReferenceEquals(rec.Map, GraphicFieldMap.For(g.GetType())))
                {
                    structuralDirty = true; // uncommitted add / instance replacement
                    continue;
                }

                var changed = WriteFields(g, rec);
                if (changed != null)
                {
                    g.OnFieldsRestored(changed);
                    g.Normalize();
                    RaiseBare(g);
                    // Normalize can drift by ulps — keep the shadow mirroring the live document
                    // (drift also stales the cached history baseline, whose fold starts there)
                    var applied = Recapture(rec, g);
                    if (!ReferenceEquals(applied.Values, rec.Values))
                    {
                        _committed.ById[g.Id] = applied;
                        InvalidateBaselineCache();
                    }
                }
            }

            if (structuralDirty)
                ReconcileMembership(collection, _committed.Order, id => _committed.ById[id]);

            if (backgroundDirty)
                _drawingCanvas.ArtworkBackground = _committed.Background;

            // the reverting writes above raised their own PropertyChanged dirt — not user dirt
            collection.ConsumeDirty();
            _drawingCanvas.ConsumeBackgroundDirty();
        }

        private void ApplyStep(HistoryStep step, bool undo)
        {
            var collection = _drawingCanvas.GraphicsList;

            // 1. undo/redo clears selection and ends text-editing/crop mode (contract #22),
            //    via direct field writes so property-setter side effects (GraphicImage
            //    IsSelected=false → EndCrop → re-entrant commit) cannot fire mid-restore.
            foreach (var g in collection)
                ResetTransientUiState(g, raise: true);

            // 2a. removals (the target side does not contain these graphics)
            foreach (var delta in step.Graphics)
            {
                var target = undo ? delta.Before : delta.After;
                if (target != null)
                    continue;
                if (collection.TryGetById(delta.Id, out var live))
                    collection.RemoveAt(collection.IndexOf(live));
            }

            // 2b. inserts and in-place field application, ascending index so multi-inserts land
            //     close to home (the order permutation below is authoritative)
            foreach (var delta in step.Graphics.OrderBy(d => d.Index))
            {
                var target = undo ? delta.Before : delta.After;
                if (target == null)
                    continue;

                var applied = ApplyRecord(collection, delta, target);

                // Normalize drifted off the step's record → the committed shadow moved without a
                // compensating step record, so the cached baseline fold is stale (see
                // InvalidateBaselineCache). ApplyRecord shares target.Values when there is none.
                if (!ReferenceEquals(applied.Values, target.Values))
                    InvalidateBaselineCache();

                // keep the step record's instance ref current for the opposite direction (a
                // reconstructed instance replaces a lost retained one) — its VALUES stay the
                // committed ones so every re-application is deterministic
                if (!ReferenceEquals(target.Instance, applied.Instance))
                {
                    var stepRecord = new FieldRecord(target.Map, target.Values, applied.Instance);
                    if (undo) delta.Before = stepRecord;
                    else delta.After = stepRecord;
                }

                // the shadow mirrors the LIVE post-Normalize state (ApplyRecord re-captured if
                // Normalize drifted), so the next commit's baseline is exactly the live document
                _committed.ById[delta.Id] = applied;
            }

            foreach (var delta in step.Graphics)
            {
                var target = undo ? delta.Before : delta.After;
                if (target == null)
                    _committed.ById.Remove(delta.Id);
            }

            // 2c. z-order permutation
            if (step.Order.HasValue)
            {
                var order = undo ? step.Order.Value.Before : step.Order.Value.After;
                ApplyOrder(collection, order);
                _committed.Order = order;
            }

            // 2d. background
            if (step.Background.HasValue)
            {
                var bg = undo ? step.Background.Value.Before : step.Background.Value.After;
                _drawingCanvas.ArtworkBackground = bg;
                _committed.Background = bg;
            }

            // 3. the apply raised its own PropertyChanged dirt — not user dirt
            collection.ConsumeDirty();
            _drawingCanvas.ConsumeBackgroundDirty();
        }

        /// <summary>
        /// Brings the graphic identified by <paramref name="delta"/> to the state in
        /// <paramref name="target"/>: in-place compiled field writes when the same instance is
        /// live (property setters bypassed — the exact semantics field-based deserialize
        /// guarantees, C7), otherwise re-insert of the retained instance (caches intact) or, as a
        /// last resort, reconstruction via the field map. Restored graphics are Normalize()d
        /// (parity with the old SetState; untouched graphics are not — deviation (c)).
        ///
        /// Returns the record the committed shadow should hold for this graphic. Normalize is not
        /// bitwise-idempotent (CenterOfRotation re-derives through floating-point round trips), so
        /// when it ran, the live values are re-captured — the shadow always mirrors the live
        /// document exactly, and the ulp drift is absorbed into the baseline instead of surfacing
        /// as phantom change paths on the next commit.
        /// </summary>
        private static FieldRecord ApplyRecord(GraphicCollection collection, GraphicDelta delta, FieldRecord target)
        {
            bool present = collection.TryGetById(delta.Id, out var live);
            bool inPlace = present &&
                           (target.Instance == null || ReferenceEquals(live, target.Instance)) &&
                           ReferenceEquals(GraphicFieldMap.For(live.GetType()), target.Map);

            if (inPlace)
            {
                var changed = WriteFields(live, target);
                if (changed == null)
                    return ReferenceEquals(target.Instance, live) ? target : new FieldRecord(target.Map, target.Values, live);

                live.OnFieldsRestored(changed);
                live.Normalize();
                RaiseBare(live); // feeds the frame validator / repaint
                return Recapture(target, live);
            }

            if (present)
            {
                // same id, different instance or type — reconcile by replacement
                collection.RemoveAt(collection.IndexOf(live));
            }

            var inst = target.Instance ?? (GraphicBase)target.Map.CreateObject();

            // a retained instance may still carry the selection/editing state it was deleted with
            ResetTransientUiState(inst, raise: false);

            // apply the stored record before inserting (guards against post-removal drift);
            // undo of a delete is a list insert of the retained instance — its memory-heavy
            // transients were trimmed at delete-commit (TrimTransientCaches) and re-derive lazily
            // (shared decode LRU + retained shadow sprite keep this cheap)
            var drift = WriteFields(inst, target);
            FieldRecord result;
            if (drift != null)
            {
                inst.OnFieldsRestored(drift);
                inst.Normalize();
                result = Recapture(target, inst);
            }
            else
            {
                result = ReferenceEquals(target.Instance, inst) ? target : new FieldRecord(target.Map, target.Values, inst);
            }

            int index = delta.Index < 0 ? int.MaxValue : delta.Index;
            index = Math.Min(index, FilteredCount(collection));
            collection.Insert(index, inst);
            Debug.Assert(string.Equals(inst.Id, delta.Id, StringComparison.Ordinal),
                         "re-inserted graphic lost its id (duplicate-id rewrite during restore)");

            return result;
        }

        /// <summary>
        /// Reconciles collection membership to <paramref name="targetOrder"/> (uncommitted-edit
        /// revert path): live graphics not in the target are dropped, missing ones re-inserted
        /// from their records, impostor same-id instances replaced; ends with the permutation.
        /// The marquee (GraphicSelectionRectangle) is left alone.
        /// </summary>
        private void ReconcileMembership(GraphicCollection collection, string[] targetOrder, Func<string, FieldRecord> recordOf)
        {
            var targetSet = new HashSet<string>(targetOrder, StringComparer.Ordinal);
            for (int i = collection.Count - 1; i >= 0; i--)
            {
                var g = collection[i];
                if (g is GraphicSelectionRectangle)
                    continue;
                if (!targetSet.Contains(g.Id))
                    collection.RemoveAt(i); // uncommitted add — discarded, like the old snapshot restore
            }

            for (int i = 0; i < targetOrder.Length; i++)
            {
                var rec = recordOf(targetOrder[i]);
                bool present = collection.TryGetById(targetOrder[i], out var live);
                if (present && ReferenceEquals(live, rec.Instance))
                    continue;

                var delta = new GraphicDelta(targetOrder[i]) { Index = i };
                var applied = ApplyRecord(collection, delta, rec);
                if (!ReferenceEquals(applied.Values, rec.Values))
                    InvalidateBaselineCache(); // Normalize drift moved the shadow off the records
                _committed.ById[targetOrder[i]] = applied;
            }

            ApplyOrder(collection, targetOrder);
        }

        /// <summary>
        /// Permutes the live list in place to the target marquee-excluded id order via public
        /// RemoveAt/Insert (all non-marquee graphics sit in one prefix block — the marquee, when
        /// present, is last by contract C4 and stays last).
        /// </summary>
        private static void ApplyOrder(GraphicCollection collection, string[] targetIds)
        {
            for (int i = 0; i < targetIds.Length && i < collection.Count; i++)
            {
                if (string.Equals(collection[i].Id, targetIds[i], StringComparison.Ordinal))
                    continue;
                if (!collection.TryGetById(targetIds[i], out var g))
                    continue; // defensive — membership was reconciled before ordering

                int j = collection.IndexOf(g);
                if (j < 0 || j == i)
                    continue;
                collection.RemoveAt(j);
                collection.Insert(i, g);
            }
        }

        /// <summary>
        /// Post-Normalize capture of <paramref name="live"/> for the committed shadow. When
        /// Normalize was bitwise-idempotent (the common case) the returned record SHARES
        /// <paramref name="target"/>.Values — callers detect real drift by Values identity and
        /// invalidate the cached history baseline, whose fold sources from the shadow.
        /// </summary>
        private static FieldRecord Recapture(FieldRecord target, GraphicBase live)
        {
            var recaptured = target.Map.Capture(live);
            if (RecordsMatch(target.Map, recaptured, target.Values))
                return ReferenceEquals(target.Instance, live) ? target : new FieldRecord(target.Map, target.Values, live);
            return new FieldRecord(target.Map, recaptured, live);
        }

        private static bool RecordsMatch(GraphicFieldMap map, object[] a, object[] b)
        {
            for (int i = 0; i < map.Slots.Length; i++)
            {
                if (!map.Slots[i].Codec.AreEqual(a[i], b[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Drops the root's cached baseline (HistorySerializer emission cache). Needed only when
        /// the committed shadow moves WITHOUT a compensating step record — the Normalize-ulp
        /// re-captures on the restore paths — because the baseline fold starts from the shadow.
        /// Every other shadow move is paired with step data the fold cancels against.
        /// </summary>
        private void InvalidateBaselineCache()
        {
            var root = _node;
            while (root.Previous != null)
                root = root.Previous;
            root.CachedBaselineJson = null;
        }

        /// <summary>Writes every differing slot of <paramref name="target"/> into
        /// <paramref name="graphic"/> (deep-copying mutable values); returns the changed JSON
        /// names for <see cref="GraphicBase.OnFieldsRestored"/>, or null when nothing differed.</summary>
        private static List<string> WriteFields(GraphicBase graphic, FieldRecord target)
        {
            List<string> changed = null;
            var slots = target.Map.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                var current = slots[i].Get(graphic);
                if (slots[i].Codec.AreEqual(current, target.Values[i]))
                    continue;
                slots[i].Set(graphic, slots[i].Codec.Capture(target.Values[i]));
                (changed ??= new List<string>()).Add(slots[i].JsonName);
            }

            return changed;
        }

        private static int FilteredCount(GraphicCollection collection)
        {
            int count = 0;
            foreach (var g in collection)
                if (!(g is GraphicSelectionRectangle))
                    count++;
            return count;
        }

        // ====================================================================
        // Transient UI state reset (restore step 1) — direct field writes through compiled
        // accessors, so GraphicImage.IsSelected=false → EndCrop → commit cannot re-enter the
        // engine mid-restore; the follow-up raises keep the collection's SelectedItems and the
        // repaint pipeline in sync (IsSelected maps to InvalidationAspects.None, so no cache or
        // shadow-sprite damage).
        // ====================================================================

        private static void ResetTransientUiState(GraphicBase g, bool raise)
        {
            if (g is GraphicImage img && img.Editing)
            {
                SetImageEditingAnchor(img, default);
                SetImageEditingCanvas(img, null);
                if (raise)
                    RaiseArgs(img, _editingChangedArgs);
            }

            if (g is GraphicText txt && txt.Editing)
            {
                SetTextEditing(txt, false);
                if (raise)
                    RaiseArgs(txt, _editingChangedArgs);
            }

            if (g.IsSelected)
            {
                SetIsSelected(g, false);
                if (raise)
                    RaiseArgs(g, _isSelectedChangedArgs);
            }
        }

        private static void RaiseBare(GraphicBase g) => RaiseArgs(g, _bareChangedArgs);

        private static void RaiseArgs(GraphicBase g, PropertyChangedEventArgs args) => _raisePropertyChanged(g, args);

        private static readonly PropertyChangedEventArgs _bareChangedArgs = new PropertyChangedEventArgs(null);
        private static readonly PropertyChangedEventArgs _isSelectedChangedArgs = new PropertyChangedEventArgs(nameof(GraphicBase.IsSelected));
        private static readonly PropertyChangedEventArgs _editingChangedArgs = new PropertyChangedEventArgs("Editing");

        private static readonly Action<GraphicBase, bool> SetIsSelected =
            CompileFieldSetter<GraphicBase, bool>(typeof(GraphicBase), "_isSelected");

        private static readonly Action<GraphicText, bool> SetTextEditing =
            CompileFieldSetter<GraphicText, bool>(typeof(GraphicText), "_editing");

        private static readonly Action<GraphicImage, Rect> SetImageEditingAnchor =
            CompileFieldSetter<GraphicImage, Rect>(typeof(GraphicImage), "_editingAnchor");

        private static readonly Action<GraphicImage, DrawingCanvas> SetImageEditingCanvas =
            CompileFieldSetter<GraphicImage, DrawingCanvas>(typeof(GraphicImage), "_editingCanvas");

        private static readonly Action<GraphicBase, PropertyChangedEventArgs> _raisePropertyChanged = CompileRaise();

        private static Action<TTarget, TValue> CompileFieldSetter<TTarget, TValue>(Type declaringType, string fieldName)
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException($"{declaringType.Name}.{fieldName} not found (history restore accessor).");
            var target = Expression.Parameter(typeof(TTarget), "target");
            var value = Expression.Parameter(typeof(TValue), "value");
            var body = Expression.Assign(Expression.Field(target, field), value);
            return Expression.Lambda<Action<TTarget, TValue>>(body, target, value).Compile();
        }

        private static Action<GraphicBase, PropertyChangedEventArgs> CompileRaise()
        {
            // GraphicBase.OnPropertyChanged is protected; Expression.Call emits a virtual call so
            // the aspect-clearing override (and the PropertyChanged event) run exactly as if the
            // graphic raised itself
            var method = typeof(GraphicBase).GetMethod("OnPropertyChanged",
                                                       BindingFlags.Instance | BindingFlags.NonPublic,
                                                       null, new[] { typeof(PropertyChangedEventArgs) }, null)
                         ?? throw new InvalidOperationException("GraphicBase.OnPropertyChanged(PropertyChangedEventArgs) not found.");
            var target = Expression.Parameter(typeof(GraphicBase), "target");
            var args = Expression.Parameter(typeof(PropertyChangedEventArgs), "args");
            return Expression.Lambda<Action<GraphicBase, PropertyChangedEventArgs>>(
                Expression.Call(target, method, args), target, args).Compile();
        }
    }
}
