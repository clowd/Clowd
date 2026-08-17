using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Editing
{
    /// <summary>How far a change to the session's project reaches — the player and the window key
    /// their reactions off this, so the classification is part of the editing contract.</summary>
    public enum ProjectChangeKind
    {
        /// <summary>A mid-gesture state (pointer still down): the preview should track it, but
        /// nothing was pushed to undo and nothing was persisted — the gesture's
        /// <see cref="EditGesture.Commit"/> raises the real change once.</summary>
        Preview,

        /// <summary>A committed edit that leaves the set of decoded streams unchanged (move, trim,
        /// split, volume, transform, rename…): the player absorbs it without rebuilding
        /// decoders.</summary>
        Mapping,

        /// <summary>The stream/track set or the timing membership changed (items or tracks added or
        /// removed, sources relinked, undo/redo): the window re-seeks a paused player so the frame
        /// on screen matches the new model.</summary>
        Structural,
    }

    /// <summary>Raised for every observable change to <see cref="EditorSession.Project"/>.
    /// <see cref="Origin"/> is whatever the mutator's caller passed as its <c>origin</c> argument
    /// (null when it passed nothing) — a consumer that both writes and listens (the inspector)
    /// compares it against itself to skip re-reading its own edits.</summary>
    public sealed class ProjectChangedEventArgs : EventArgs
    {
        public ProjectChangedEventArgs(ProjectChangeKind kind, object origin)
        {
            Kind = kind;
            Origin = origin;
        }

        public ProjectChangeKind Kind { get; }

        public object Origin { get; }
    }

    /// <summary>Raised when a mutation produced a project that fails <see cref="Project.Validate"/>
    /// and was rolled back. Surfaced as an event rather than an exception because the mutators are
    /// driven from pointer/keyboard handlers where a throw would tear the interaction down — the
    /// project is already back in its pre-mutation state when this fires.</summary>
    public sealed class ValidationFailureEventArgs : EventArgs
    {
        public ValidationFailureEventArgs(string operation, IReadOnlyList<string> errors)
        {
            Operation = operation;
            Errors = errors;
        }

        /// <summary>The undo label of the operation that was rolled back.</summary>
        public string Operation { get; }

        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>Where the session writes the project file. <see cref="Write"/> is called on the
    /// session's thread with the complete serialized project; implementations own any queueing or
    /// background I/O (the UI's latest-wins autosave writer implements this).</summary>
    public interface IEditorPersistence
    {
        void Write(byte[] utf8Json);
    }

    /// <summary>
    /// One pointer drag (or any other multi-step interaction) as a single undoable unit, returned
    /// by <see cref="EditorSession.BeginGesture"/>. While the gesture is open, session mutations
    /// raise <see cref="ProjectChangeKind.Preview"/> only — no undo entries, no persistence.
    /// <see cref="Commit"/> pushes exactly one undo entry spanning the whole gesture (none when it
    /// ended where it started); <see cref="Cancel"/> — or disposal without a commit, the Esc path —
    /// restores the state the gesture began from.
    /// </summary>
    public sealed class EditGesture : IDisposable
    {
        private readonly EditorSession _session;
        private bool _finished;

        internal EditGesture(EditorSession session, string label, object origin,
            string beforeJson, Guid[] beforeSelection)
        {
            _session = session;
            Label = label;
            Origin = origin;
            BeforeJson = beforeJson;
            BeforeSelection = beforeSelection;
        }

        public string Label { get; }

        internal object Origin { get; }

        internal string BeforeJson { get; }

        internal Guid[] BeforeSelection { get; }

        /// <summary>The strongest kind any inner mutation carried — what <see cref="Commit"/>
        /// raises, so a gesture containing a delete still announces itself as structural.</summary>
        internal ProjectChangeKind Kind { get; set; } = ProjectChangeKind.Mapping;

        /// <summary>Ends the gesture, pushing one undo entry and persisting — unless nothing
        /// actually changed, in which case a no-op drag costs nothing.</summary>
        public void Commit()
        {
            Finish();
            _session.CommitGesture(this);
        }

        /// <summary>Ends the gesture, restoring the project (and selection) to the state it began
        /// from. Nothing is pushed to undo and nothing is persisted.</summary>
        public void Cancel()
        {
            Finish();
            _session.CancelGesture(this);
        }

        /// <summary>Disposal without a prior <see cref="Commit"/> cancels — <c>using</c> a gesture
        /// makes an exception mid-drag restore the project instead of stranding it.</summary>
        public void Dispose()
        {
            if (!_finished)
                Cancel();
        }

        private void Finish()
        {
            if (_finished)
                throw new InvalidOperationException("The gesture has already ended.");
            _finished = true;
        }
    }

    /// <summary>
    /// The editing session: owns the live <see cref="Project"/> and is the single mutation funnel
    /// for the timeline, inspector, gizmo and toolbar. Every mutator runs the same pipeline —
    /// snapshot, mutate, <see cref="Project.Normalize"/>, <see cref="Project.Validate"/> (a failure
    /// rolls the project back and raises <see cref="ValidationFailed"/> instead of throwing), undo
    /// push, persistence, <see cref="ProjectChanged"/> — so no entry point can leave the model
    /// half-updated or unrecorded.
    ///
    /// Undo is whole-project JSON snapshots (the project is a few KB and round-trips all ids, so a
    /// restore still hits the player's cheap update path); <see cref="Undo"/>/<see cref="Redo"/>
    /// <b>replace</b> the <see cref="Project"/> instance. Selection lives here too, so the
    /// timeline, inspector and gizmo cannot disagree about it.
    ///
    /// Not thread-safe: call everything on the owning (UI) thread. The injected
    /// <c>saveScheduler</c> is how the host debounces persistence — the session hands it a callback
    /// which, whenever the host runs it, writes the <b>latest</b> state through the
    /// <see cref="IEditorPersistence"/>.
    /// </summary>
    public sealed class EditorSession
    {
        /// <summary>Undo depth cap. Snapshots are a few KB each, so 100 entries stay well under
        /// the memory budget while covering any plausible editing run.</summary>
        public const int UndoCapacity = 100;

        /// <summary>Consecutive edits sharing a coalesce key merge into one undo entry when they
        /// arrive within this window of each other (spinner mashing is one undo).</summary>
        public const long CoalesceWindowMs = 1000;

        private readonly IEditorPersistence _persist;
        private readonly Action<Action> _saveScheduler;
        private readonly HashSet<Guid> _initialTrackIds;
        private readonly List<UndoEntry> _undo = new List<UndoEntry>();
        private readonly List<UndoEntry> _redo = new List<UndoEntry>();
        private readonly HashSet<Guid> _selectedIds = new HashSet<Guid>();
        private Guid? _selectionAnchor;
        private EditGesture _gesture;
        private bool _suppressCoalesce;
        private string _pendingSaveJson;

        /// <summary>Test seam for the coalesce window; production time is
        /// <see cref="Environment.TickCount64"/>.</summary>
        internal Func<long> Clock = () => Environment.TickCount64;

        /// <param name="project">The project to edit. Normalized here so the session's very first
        /// undo snapshot is already in canonical form.</param>
        /// <param name="persist">Where committed states are written, or null for an unpersisted
        /// session (tests, throwaway previews).</param>
        /// <param name="saveScheduler">Debounce seam: called with a save callback after every
        /// committed change; run the callback to write the latest state. Null runs saves
        /// immediately.</param>
        public EditorSession(Project project, IEditorPersistence persist, Action<Action> saveScheduler)
        {
            ArgumentNullException.ThrowIfNull(project);

            project.Normalize();
            Project = project;
            _persist = persist;
            _saveScheduler = saveScheduler ?? (save => save());
            _initialTrackIds = project.Tracks.Select(t => t.Id).ToHashSet();
        }

        /// <summary>The live model. The instance is <b>replaced</b> by <see cref="Undo"/>/<see cref="Redo"/>
        /// — hold on to ids, not object references, across changes.</summary>
        public Project Project { get; private set; }

        /// <summary>The timeline's current length in 100ns ticks (see
        /// <see cref="Model.Project.GetDurationTicks"/>).</summary>
        public long DurationTicks => Project.GetDurationTicks();

        public event EventHandler<ProjectChangedEventArgs> ProjectChanged;

        public event EventHandler SelectionChanged;

        /// <summary>Raised whenever <see cref="CanUndo"/>/<see cref="CanRedo"/> may have changed.</summary>
        public event EventHandler HistoryChanged;

        /// <summary>See <see cref="ValidationFailureEventArgs"/> — the failed mutation was already
        /// rolled back when this fires.</summary>
        public event EventHandler<ValidationFailureEventArgs> ValidationFailed;

        /// <summary>Deep clone via a JSON round-trip — what the window hands
        /// <c>CompositionPlayer.UpdateProject</c>, which retains the reference and reads it from
        /// background rebuild threads, so it must never see the live mutable instance.</summary>
        public Project SnapshotForPlayer() => Project.FromJson(Project.ToJson());

        // ------------------------------------------------------------------------------ selection

        /// <summary>The selected item ids, primary (anchor) first. Empty when nothing is
        /// selected.</summary>
        public IReadOnlyList<Guid> SelectedItemIds => SelectionSnapshot();

        /// <summary>The primary selected item, resolved against the live <see cref="Project"/> on
        /// every read (items are replaced by undo/redo, so a cached reference would go stale).
        /// Null when nothing is selected or the selected item no longer exists.</summary>
        public Item PrimarySelectedItem
        {
            get
            {
                var id = _selectionAnchor ?? (_selectedIds.Count > 0 ? _selectedIds.First() : (Guid?)null);
                return id is Guid g ? Project.Items.FirstOrDefault(i => i.Id == g) : null;
            }
        }

        /// <summary>Selects an item (null clears). <paramref name="additive"/> is accepted for the
        /// coming multi-select — the session currently caps selection at one item, so it behaves
        /// as replace.</summary>
        public void Select(Guid? itemId, bool additive = false)
        {
            if (itemId == null)
            {
                ClearSelection();
                return;
            }

            var id = itemId.Value;
            if (_selectedIds.Count == 1 && _selectedIds.Contains(id) && _selectionAnchor == id)
                return;

            _selectedIds.Clear();
            _selectedIds.Add(id);
            _selectionAnchor = id;
            // a selection change ends any coalescing run: even a caller whose coalesce key forgot
            // to name its item cannot merge edits made to two different selections into one entry.
            _suppressCoalesce = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ClearSelection()
        {
            if (_selectedIds.Count == 0)
                return;

            _selectedIds.Clear();
            _selectionAnchor = null;
            _suppressCoalesce = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------------------- gestures

        /// <summary>True while a gesture is open. Hosts gate history commands on this —
        /// <see cref="Undo"/>/<see cref="Redo"/> during a drag would throw.</summary>
        public bool IsGestureActive => _gesture != null;

        /// <summary>Opens a gesture (see <see cref="EditGesture"/>). Gestures do not nest — a
        /// second call before the first gesture ended is a programming error and throws.</summary>
        public EditGesture BeginGesture(string label, object origin = null)
        {
            if (_gesture != null)
                throw new InvalidOperationException($"Gesture '{_gesture.Label}' is still in progress.");

            _gesture = new EditGesture(this, label, origin, Project.ToJson(), SelectionSnapshot());
            return _gesture;
        }

        internal void CommitGesture(EditGesture gesture)
        {
            _gesture = null;

            var after = Project.ToJson();
            if (after == gesture.BeforeJson)
                return;

            PushUndo(new UndoEntry
            {
                Label = gesture.Label,
                BeforeJson = gesture.BeforeJson,
                BeforeSelection = gesture.BeforeSelection,
                TimestampMs = Clock(),
            });
            SchedulePersist(after);
            RaiseProjectChanged(gesture.Kind, gesture.Origin);
        }

        internal void CancelGesture(EditGesture gesture)
        {
            _gesture = null;

            if (Project.ToJson() == gesture.BeforeJson)
                return;

            RestoreInPlace(gesture.BeforeJson);
            RestoreSelection(gesture.BeforeSelection);
            RaiseProjectChanged(gesture.Kind, gesture.Origin);
        }

        // ------------------------------------------------------------------------------ output box

        /// <summary>The largest and smallest output dimension a canvas may take. The lower bound is
        /// two rather than one because the encoder only accepts even sizes (yuv420p).</summary>
        public const int MinOutputDimension = 2;

        public const int MaxOutputDimension = 16384;

        /// <summary>
        /// Resizes the output canvas, in one undo entry. Items are placed as fractions of the
        /// canvas, so this rescales the whole composition rather than cropping it — an aspect change
        /// therefore re-letterboxes the material inside the new frame.
        ///
        /// The size is clamped and rounded to even (<see cref="ClampOutputDimension"/>) before it is
        /// applied, so a hand-typed odd size cannot reach the encoder. False means nothing changed
        /// (the clamped size is the one already set) or validation rolled the change back.
        /// </summary>
        public bool SetOutputSize(int widthPx, int heightPx, object origin = null) =>
            Mutate("Resize Canvas", ProjectChangeKind.Structural, null, origin, p =>
            {
                var w = ClampOutputDimension(widthPx);
                var h = ClampOutputDimension(heightPx);
                if (p.Output.WidthPx == w && p.Output.HeightPx == h)
                    return false;

                p.Output.WidthPx = w;
                p.Output.HeightPx = h;
                return true;
            }, failureValue: false);

        /// <summary>Brings one canvas dimension into range and onto an even number (yuv420p), which
        /// is what <see cref="SetOutputSize"/> stores and what the resolution picker offers.</summary>
        public static int ClampOutputDimension(int value)
        {
            var clamped = Math.Clamp(value, MinOutputDimension, MaxOutputDimension);
            return clamped - (clamped % 2);
        }

        /// <summary>
        /// Sets the output frame rate, in one undo entry. Output is CFR, so this is the grid every
        /// composed frame lands on when the edit is rendered; the preview composes on demand and is
        /// unaffected, which is why this is a mapping rather than a structural change. The rate is a
        /// rational so 29.97 (30000/1001) survives exactly.
        ///
        /// False means the rational was not positive, nothing changed (that rate is already set), or
        /// validation rolled the change back.
        /// </summary>
        public bool SetOutputFrameRate(int fpsNum, int fpsDen, object origin = null)
        {
            if (fpsNum <= 0 || fpsDen <= 0)
                return false;

            return Mutate("Change Frame Rate", ProjectChangeKind.Mapping, null, origin, p =>
            {
                if (p.Output.FpsNum == fpsNum && p.Output.FpsDen == fpsDen)
                    return false;

                p.Output.FpsNum = fpsNum;
                p.Output.FpsDen = fpsDen;
                return true;
            }, failureValue: false);
        }

        /// <summary>
        /// The natural frame rate of the material: the first video stream that declares one, among
        /// the sources the timeline actually plays — the recording that started the session, or the
        /// first video imported into an edit built from scratch. Deliberately <i>first</i> rather
        /// than largest (<see cref="GetNativeSize"/>'s rule): a 30fps recording carrying a 60fps
        /// webcam on track 2 is still a 30fps edit. Null when nothing referenced declares a rate,
        /// which is why the picker's native entry is optional.
        /// </summary>
        public static (int Num, int Den)? GetNativeFrameRate(Project project)
        {
            if (project?.Sources == null)
                return null;

            foreach (var source in project.Sources)
            {
                if (source.Streams == null || !IsSourceReferenced(project, source.Id))
                    continue;

                foreach (var stream in source.Streams)
                {
                    if (stream.Kind != StreamKind.Video ||
                        stream.AvgFrameRateNum <= 0 || stream.AvgFrameRateDen <= 0)
                        continue;

                    return (stream.AvgFrameRateNum, stream.AvgFrameRateDen);
                }
            }

            return null;
        }

        /// <summary>
        /// The natural size of the material: the largest video stream among the sources the timeline
        /// actually plays — for a screen recording, the recording itself, and for an edit whose
        /// second track is a webcam, still the recording. Null when nothing video is referenced (an
        /// edit built only from text, audio, or nothing at all), which is why the picker's native
        /// entry is optional.
        /// </summary>
        public static (int WidthPx, int HeightPx)? GetNativeSize(Project project)
        {
            if (project?.Sources == null)
                return null;

            (int W, int H)? best = null;
            foreach (var source in project.Sources)
            {
                if (source.Streams == null || !IsSourceReferenced(project, source.Id))
                    continue;

                foreach (var stream in source.Streams)
                {
                    if (stream.Kind != StreamKind.Video || stream.Width <= 0 || stream.Height <= 0)
                        continue;

                    if (best == null || (long)stream.Width * stream.Height > (long)best.Value.W * best.Value.H)
                        best = (stream.Width, stream.Height);
                }
            }

            return best;
        }

        // --------------------------------------------------------------------------- timeline ops

        /// <summary>Wraps <see cref="TimelineOps.Move"/> (whole link group, clamped at the
        /// origin). Returns the delta actually applied — 0 when clamped away entirely or when the
        /// result was rolled back (a move onto an occupied span of the same track).</summary>
        public long MoveItem(Guid itemId, long deltaTicks, object origin = null) =>
            Mutate("Move", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.Move(p, itemId, deltaTicks));

        /// <summary>Wraps <see cref="TimelineOps.TrimStart"/> (single item), with the input-overlay
        /// clamp on both sides of the hard sync (see <see cref="ClampOverlayTrim"/> and
        /// <see cref="ClampOverlayItemsToScreen"/>). Returns the delta actually applied.</summary>
        public long TrimItemStart(Guid itemId, long deltaTicks, object origin = null) =>
            Mutate("Trim", TrimChangeKind(itemId), null, origin, p =>
            {
                var applied = TimelineOps.TrimStart(p, itemId,
                    ClampOverlayTrim(p, itemId, deltaTicks, fromStart: true));
                ClampOverlayItemsToScreen(p, itemId);
                return applied;
            });

        /// <summary>Wraps <see cref="TimelineOps.TrimEnd"/> (single item), clamped exactly as
        /// <see cref="TrimItemStart"/>. Returns the delta actually applied.</summary>
        public long TrimItemEnd(Guid itemId, long deltaTicks, object origin = null) =>
            Mutate("Trim", TrimChangeKind(itemId), null, origin, p =>
            {
                var applied = TimelineOps.TrimEnd(p, itemId,
                    ClampOverlayTrim(p, itemId, deltaTicks, fromStart: false));
                ClampOverlayItemsToScreen(p, itemId);
                return applied;
            });

        /// <summary>
        /// The change kind a trim of this item raises: <see cref="ProjectChangeKind.Structural"/>
        /// when the item's recording carries cursor/keyboard rows, because shrinking the screen
        /// takes those items along and may strand — and so drop — one of them, emptying a row.
        /// Deliberately keyed on the group rather than on what the trim turns out to do: the answer
        /// is needed before the mutation runs, and an extra row rebuild once per trim gesture costs
        /// nothing (mid-gesture changes raise Preview and escalate the gesture's kind instead).
        /// </summary>
        private ProjectChangeKind TrimChangeKind(Guid itemId)
        {
            var item = Project.Items.FirstOrDefault(i => i.Id == itemId);
            if (item?.LinkGroupId is not Guid group || item.Content is not MediaContent)
                return ProjectChangeKind.Mapping;

            return Project.Items.Any(i => i.LinkGroupId == group
                                          && i.Content is CursorContent or KeyboardContent)
                ? ProjectChangeKind.Structural
                : ProjectChangeKind.Mapping;
        }

        /// <summary>
        /// The trim delta an input-overlay item may actually take. Cursor/keyboard items annotate a
        /// screen segment and are hard-synced to it: they may be trimmed <i>shorter</i> freely (hide
        /// the cursor for a while) but must never reach past the screen span, where they would have
        /// nothing to annotate — the cursor draws nothing there and the keyboard falls back to
        /// item-relative time (see <c>FrameComposer.FindScreenMediaItem</c>). The dragged edge is
        /// therefore clamped to the span of the screen items sharing the overlay's link group. An
        /// item already hanging past that span may still shrink, it just cannot grow, exactly as
        /// <see cref="TimelineOps.TrimEnd"/> treats an item hanging past the end of its source.
        /// Non-overlay items — and overlays whose screen row is gone — pass through unchanged.
        /// </summary>
        private static long ClampOverlayTrim(Project project, Guid itemId, long deltaTicks, bool fromStart)
        {
            var item = project.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || OverlayScreenSpan(project, item) is not { } span)
                return deltaTicks;

            return fromStart
                ? Math.Max(deltaTicks, Math.Min(0, span.Start - item.TimelineStartTicks))
                : Math.Min(deltaTicks, Math.Max(0, span.End - item.TimelineEndTicks));
        }

        /// <summary>
        /// The other side of the same contract: after a screen item is trimmed, the overlay items of
        /// its link group are pulled in with it so they never outlive the screen span. Trims are
        /// single-item by design (the group's rows keep their own in-points), so nothing else fans
        /// out this way — but an overlay left hanging over material that is gone is exactly the
        /// state the hard sync exists to prevent. An overlay item the trim leaves entirely outside
        /// the span is removed: what it annotated no longer plays. No-op for anything but a linked
        /// media item.
        /// </summary>
        private void ClampOverlayItemsToScreen(Project project, Guid trimmedItemId)
        {
            var trimmed = project.Items.FirstOrDefault(i => i.Id == trimmedItemId);
            if (trimmed?.LinkGroupId is not Guid group || trimmed.Content is not MediaContent)
                return;

            var dropped = false;
            foreach (var overlay in project.Items.Where(i => i.LinkGroupId == group
                         && i.Content is CursorContent or KeyboardContent).ToList())
            {
                if (OverlayScreenSpan(project, overlay) is not { } span)
                    continue;

                var start = Math.Max(overlay.TimelineStartTicks, span.Start);
                var end = Math.Min(overlay.TimelineEndTicks, span.End);
                if (end <= start)
                {
                    project.Items.Remove(overlay);
                    dropped = true;
                    continue;
                }

                overlay.TimelineStartTicks = start;
                overlay.DurationTicks = end - start;
            }

            if (dropped)
                PruneEmptyTracks(project);
        }

        /// <summary>The span an input-overlay item is allowed to occupy: the union of the video-row
        /// screen items sharing its link group — the segment it mirrors, which a
        /// <see cref="TimelineOps.SplitItem"/> on the screen row may have left as two back-to-back
        /// members. Null for a non-overlay item, and for an overlay whose screen partner is gone
        /// (nothing to clamp to; the overlay already draws nothing).</summary>
        private static (long Start, long End)? OverlayScreenSpan(Project project, Item item)
        {
            Guid sourceId;
            if (item.Content is CursorContent cursor)
                sourceId = cursor.SourceId;
            else if (item.Content is KeyboardContent keyboard)
                sourceId = keyboard.SourceId;
            else
                return null;

            if (item.LinkGroupId is not Guid group)
                return null;

            var source = project.Sources.FirstOrDefault(s => s.Id == sourceId);
            if (source == null)
                return null;

            long start = long.MaxValue, end = long.MinValue;
            foreach (var partner in project.Items)
            {
                if (partner.LinkGroupId != group || partner.Content is not MediaContent media
                    || media.SourceId != sourceId
                    || !Composition.FrameComposer.IsScreenStream(source, media.StreamIndex))
                    continue;

                var track = project.Tracks.FirstOrDefault(t => t.Id == partner.TrackId);
                if (track?.Kind != TrackKind.Video)
                    continue;

                start = Math.Min(start, partner.TimelineStartTicks);
                end = Math.Max(end, partner.TimelineEndTicks);
            }

            return end > start ? (start, end) : null;
        }

        /// <summary>Wraps <see cref="TimelineOps.SetSpeed"/> (single media item, re-timed in
        /// place). Returns the speed actually stored.</summary>
        public double SetItemSpeed(Guid itemId, double speed, object origin = null) =>
            Mutate("Speed", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.SetSpeed(p, itemId, speed), failureValue: 1.0);

        /// <summary>Sets a speed effect item's target factor (<see cref="SpeedContent.Factor"/>),
        /// clamped to its valid range — the item's span is untouched, unlike
        /// <see cref="SetItemSpeed"/>'s re-timing. Coalesced per item so spinner mashing is one
        /// undo. Returns the factor actually stored (1.0 for non-speed content).</summary>
        public double SetSpeedFactor(Guid itemId, double factor, object origin = null) =>
            Mutate("Speed Target", ProjectChangeKind.Mapping, $"speed-factor:{itemId}", origin, p =>
            {
                var item = RequireItem(p, itemId);
                if (item.Content is not SpeedContent speed)
                    return 1.0;

                factor = Math.Clamp(factor, 0.1, 10);
                speed.Factor = factor;
                return factor;
            }, failureValue: 1.0);

        /// <summary>Wraps <see cref="TimelineOps.Split"/> (whole link group, all-or-nothing).</summary>
        public bool SplitAt(Guid itemId, long timelineTicks, object origin = null) =>
            Mutate("Split", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.Split(p, itemId, timelineTicks));

        /// <summary>Wraps <see cref="TimelineOps.SplitItem"/>: cuts this clip and nothing else, not
        /// even the rest of its recording. The timeline's right-click split.</summary>
        public bool SplitItemAt(Guid itemId, long timelineTicks, object origin = null) =>
            Mutate("Split", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.SplitItem(p, itemId, timelineTicks));

        /// <summary>The split-everything command: cuts every item covering the playhead — video,
        /// audio, text and image rows alike — taking each link group once. Deliberately ignores the
        /// selection: this is the "cut straight down the timeline" gesture, and a selected clip must
        /// not quietly narrow it to one row (the right-click menu is what cuts a single clip).
        /// Returns true when anything split.</summary>
        public bool SplitAtPlayhead(long playheadTicks, object origin = null) =>
            Mutate("Split", ProjectChangeKind.Mapping, null, origin, p =>
            {
                // snapshot the candidates first: right halves produced by a split start exactly at
                // the playhead, so they cover it and must not themselves be split.
                var candidates = p.Items.Where(i => Covers(i, playheadTicks))
                                        .Select(i => (i.Id, i.LinkGroupId)).ToList();
                var handledGroups = new HashSet<Guid>();
                var any = false;
                foreach (var (id, group) in candidates)
                {
                    if (group is Guid g && !handledGroups.Add(g))
                        continue; // a split from another member already covered this group
                    any |= TimelineOps.Split(p, id, playheadTicks);
                }
                return any;
            });

        /// <summary>Wraps <see cref="TimelineOps.RippleDelete"/> (the clip's span cut out of its
        /// link group, gap closes on all tracks), then prunes any non-initial track that lost its
        /// last item — inside the same mutation, so one undo restores tracks and items
        /// together.</summary>
        public void RippleDeleteItem(Guid itemId, object origin = null) =>
            Mutate("Delete", ProjectChangeKind.Structural, null, origin, p =>
            {
                TimelineOps.RippleDelete(p, itemId);
                PruneEmptyTracks(p);
            });

        /// <summary>Wraps <see cref="TimelineOps.Delete"/> (single item, no ripple), with the same
        /// track prune as <see cref="RippleDeleteItem"/>.</summary>
        public void DeleteItem(Guid itemId, object origin = null) =>
            Mutate("Delete", ProjectChangeKind.Structural, null, origin, p =>
            {
                if (TimelineOps.Delete(p, itemId))
                    PruneEmptyTracks(p);
            });

        /// <summary>Wraps <see cref="TimelineOps.DeleteLinked"/> (the clip's span cut out of its
        /// link group in place — no ripple) with the same track prune, all in one mutation. The
        /// delete for an imported file's rows, whose group means "streams of one file", not
        /// "contiguous recording segments" (see <see cref="IsRippleGroup"/>): closing the gap
        /// under everything else is the recording cut's semantics, not the overlay's.</summary>
        public void DeleteGroup(Guid itemId, object origin = null) =>
            Mutate("Delete", ProjectChangeKind.Structural, null, origin, p =>
            {
                TimelineOps.DeleteLinked(p, itemId);
                PruneEmptyTracks(p);
            });

        /// <summary>
        /// True when the item's link group is a recording-segment group — one with a member on a
        /// track the session opened with — as opposed to the per-file group an import gets. The
        /// discriminator the UI keys the move gate off: a recording group is pinned in place
        /// while an import moves as one. False for unlinked items.
        /// </summary>
        public bool IsRippleGroup(Guid itemId)
        {
            var item = Project.Items.FirstOrDefault(i => i.Id == itemId);
            if (item?.LinkGroupId is not Guid group)
                return false;

            return Project.Items.Any(i => i.LinkGroupId == group && _initialTrackIds.Contains(i.TrackId));
        }

        /// <summary>Wraps <see cref="TimelineOps.UnlinkTrack"/> — the row's sync toggle turned
        /// off. Link groups only affect editing, never playback, so this is a mapping change.
        /// Refused for cursor/keyboard rows: their items are hard-synced to the recording
        /// (validation requires the link group), so the toggle never comes off.</summary>
        public void UnlinkTrack(Guid trackId, object origin = null)
        {
            if (IsInputOverlayTrack(Project, trackId))
                return;

            Mutate("Unlink Track", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.UnlinkTrack(p, trackId));
        }

        /// <summary>Wraps <see cref="TimelineOps.TryRelinkTrack"/>. False means the row has
        /// drifted and the toggle stays off — the project is untouched.</summary>
        public bool TryRelinkTrack(Guid trackId, object origin = null) =>
            Mutate("Relink Track", ProjectChangeKind.Mapping, null, origin,
                p => TimelineOps.TryRelinkTrack(p, trackId));

        // ---------------------------------------------------------------- tracks and properties

        /// <summary>Video rows' enable toggle. Structural: hiding a row changes which streams the
        /// player decodes.</summary>
        public void SetTrackHidden(Guid trackId, bool value, object origin = null) =>
            Mutate(value ? "Hide Track" : "Show Track", ProjectChangeKind.Structural, null, origin,
                p => RequireTrack(p, trackId).Hidden = value);

        /// <summary>Audio rows' enable toggle. Structural for the same reason as
        /// <see cref="SetTrackHidden"/>.</summary>
        public void SetTrackMuted(Guid trackId, bool value, object origin = null) =>
            Mutate(value ? "Mute Track" : "Unmute Track", ProjectChangeKind.Structural, null, origin,
                p => RequireTrack(p, trackId).Muted = value);

        public void SetTrackLocked(Guid trackId, bool value, object origin = null) =>
            Mutate(value ? "Lock Track" : "Unlock Track", ProjectChangeKind.Structural, null, origin,
                p => RequireTrack(p, trackId).Locked = value);

        /// <summary>
        /// Duplicates a row: a new track of the same kind and enable state directly above the
        /// original, carrying a copy of every item on it. The copies are unlinked — a duplicate is
        /// its own material, not a new member of the recording's group (a linked copy would make
        /// every group move/split apply twice to the same content). The copy is never locked: a
        /// duplicate exists to be edited. Returns false for an unknown id, or for the speed row —
        /// playback speed is a single global timeline, and a second speed row cannot validate.
        /// </summary>
        public bool DuplicateTrack(Guid trackId, object origin = null) =>
            Mutate("Duplicate Track", ProjectChangeKind.Structural, null, origin, p =>
            {
                var track = p.Tracks.FirstOrDefault(t => t.Id == trackId);
                if (track == null || IsSpeedTrack(p, track) || IsInputOverlayTrack(p, trackId))
                    return false;

                // slot the copy in directly above the original: everything at or past that order
                // steps down one, exactly as InsertVideoTrackOnTop renumbers.
                var order = track.Order + 1;
                foreach (var t in p.Tracks)
                {
                    if (t.Order >= order)
                        t.Order++;
                }

                var copy = new Track
                {
                    Id = Guid.NewGuid(),
                    Kind = track.Kind,
                    Name = String.IsNullOrEmpty(track.Name) ? track.Name : track.Name + " copy",
                    Order = order,
                    Hidden = track.Hidden,
                    Muted = track.Muted,
                };
                p.Tracks.Add(copy);

                foreach (var item in p.Items.Where(i => i.TrackId == trackId).ToList())
                {
                    p.Items.Add(new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = copy.Id,
                        TimelineStartTicks = item.TimelineStartTicks,
                        DurationTicks = item.DurationTicks,
                        Content = item.Content?.Clone(),
                        Transform = item.Transform?.Clone() ?? new Transform(),
                        Surround = item.Surround?.Clone(),
                        Entry = item.Entry?.Clone(),
                        Exit = item.Exit?.Clone(),
                        Volume = item.Volume,
                    });
                }

                return true;
            }, failureValue: false);

        /// <summary>Deletes a row and every item on it — no ripple, the span just empties. Link
        /// group members on <i>other</i> rows are untouched: this is the row's delete, not the
        /// group's (that is <see cref="RippleDeleteItem"/>/<see cref="DeleteGroup"/>). Returns
        /// false for an unknown id.</summary>
        public bool DeleteTrack(Guid trackId, object origin = null) =>
            Mutate("Delete Track", ProjectChangeKind.Structural, null, origin, p =>
            {
                if (p.Tracks.RemoveAll(t => t.Id == trackId) == 0)
                    return false;

                p.Items.RemoveAll(i => i.TrackId == trackId);
                return true;
            }, failureValue: false);

        /// <summary>
        /// Moves a video row one place through the composite stack: <paramref name="towardsFront"/>
        /// raises it over the row it passes, otherwise it drops behind. Returns false — changing
        /// nothing — for an audio row (audio does not stack), a row already at the end it is being
        /// pushed towards, or an unknown id.
        ///
        /// <para>Because <see cref="Track.Order"/> is neither required to be unique nor contiguous,
        /// this renumbers every row rather than swapping two values: a swap between rows that
        /// happen to share an <c>Order</c> would be a no-op, and the <c>(Order, Id)</c> tie-break
        /// would silently decide the stacking instead of the user. Order is presentation-only, so
        /// renumbering costs nothing.</para>
        /// </summary>
        public bool MoveTrackLayer(Guid trackId, bool towardsFront, object origin = null) =>
            Mutate(towardsFront ? "Move Row Up" : "Move Row Down", ProjectChangeKind.Structural, null, origin,
                // ascending Order paints later, so "towards the front" is the next index up.
                p => Reorder(p, trackId, index => towardsFront ? index + 1 : index - 1, audioMoves: false),
                failureValue: false);

        /// <summary>
        /// Moves a row to an absolute position among the rows of its own kind — the drop of the
        /// track headers' drag-reorder, which cannot express itself as a run of one-step moves
        /// without pushing one undo entry per step. <paramref name="index"/> counts in the model's
        /// canonical ascending <c>(Order, Id)</c> order, so for video that is back-to-front (the
        /// <i>reverse</i> of the top-to-bottom rows the timeline draws — see
        /// <c>TimelineRowLayout.Build</c>) and 0 is the backmost layer.
        ///
        /// <para>Unlike <see cref="MoveTrackLayer"/> this accepts audio rows: their order is not a
        /// stacking order (audio does not composite) but it is the order the timeline lists them
        /// in, which is worth arranging. The pinned speed row is excluded from the index space
        /// and never moves. Returns false — changing nothing — for an unknown id, an index
        /// outside its group, or a move that lands where the row already is.</para>
        /// </summary>
        public bool MoveTrackToIndex(Guid trackId, int index, object origin = null) =>
            Mutate("Reorder Row", ProjectChangeKind.Structural, null, origin,
                p => Reorder(p, trackId, _ => index, audioMoves: true),
                failureValue: false);

        /// <summary>
        /// The shared body of both moves: lifts the row out of its kind's list, puts it back at
        /// <paramref name="targetOf"/>(current index) and renumbers.
        ///
        /// <para>Because <see cref="Track.Order"/> is neither required to be unique nor contiguous,
        /// this renumbers every row rather than swapping two values: a swap between rows that
        /// happen to share an <c>Order</c> would be a no-op, and the <c>(Order, Id)</c> tie-break
        /// would silently decide the stacking instead of the user. Order is presentation-only, so
        /// renumbering costs nothing.</para>
        /// </summary>
        private static bool Reorder(Project project, Guid trackId, Func<int, int> targetOf, bool audioMoves)
        {
            // cursor/keyboard rows are pinned directly above their recording's screen row — they
            // never reorder themselves (other video rows may still step around them).
            if (IsInputOverlayTrack(project, trackId))
                return false;

            // the speed row is pinned: it never moves, nothing moves past it, and it sits outside
            // the layer-index space entirely (its Order is renumbered above everything below).
            var speed = project.Tracks.Where(t => IsSpeedTrack(project, t))
                                      .OrderBy(t => t.Order).ThenBy(t => t.Id).ToList();
            var video = project.Tracks.Where(t => t.Kind != TrackKind.Audio && !speed.Contains(t))
                                      .OrderBy(t => t.Order).ThenBy(t => t.Id).ToList();
            var audio = project.Tracks.Where(t => t.Kind == TrackKind.Audio)
                                      .OrderBy(t => t.Order).ThenBy(t => t.Id).ToList();

            var group = video;
            var index = video.FindIndex(t => t.Id == trackId);
            if (index < 0)
            {
                if (!audioMoves)
                    return false;

                group = audio;
                index = audio.FindIndex(t => t.Id == trackId);
                if (index < 0)
                    return false;
            }

            var target = targetOf(index);
            if (target < 0 || target >= group.Count || target == index)
                return false;

            var moved = group[index];
            group.RemoveAt(index);
            group.Insert(target, moved);

            // the pin is two-sided: the overlay rows never move themselves (above), and no move
            // may land the screen row above its own cursor/keyboard rows — they composite over
            // it by contract, and buried beneath the opaque screen frame the cursor would simply
            // vanish (the default overlay stands down while a cursor item is active).
            if (group == video && OverlayPinViolated(project, video))
                return false;

            var order = 0;
            foreach (var track in video)
                track.Order = order++;
            // audio keeps its relative order and stays above every video Order, which is what
            // puts those rows at the bottom of the timeline.
            foreach (var track in audio)
                track.Order = order++;
            foreach (var track in speed)
                track.Order = order++;

            return true;
        }

        /// <summary>Whether <see cref="MoveTrackLayer"/> would do anything — the enablement the
        /// timeline's context menu needs, without a speculative mutation. Always false for the
        /// pinned speed row, and for a step that would raise a screen row above its own
        /// cursor/keyboard rows (the same refusal <see cref="Reorder"/> applies).</summary>
        public bool CanMoveTrackLayer(Guid trackId, bool towardsFront)
        {
            if (IsInputOverlayTrack(Project, trackId))
                return false;

            var video = Project.Tracks.Where(t => t.Kind != TrackKind.Audio && !IsSpeedTrack(Project, t))
                                      .OrderBy(t => t.Order).ThenBy(t => t.Id).ToList();

            var index = video.FindIndex(t => t.Id == trackId);
            if (index < 0)
                return false;

            var target = towardsFront ? index + 1 : index - 1;
            if (target < 0 || target >= video.Count)
                return false;

            var moved = video[index];
            video.RemoveAt(index);
            video.Insert(target, moved);
            return !OverlayPinViolated(Project, video);
        }

        /// <summary>
        /// Whether the tentative video arrangement (list index = eventual paint order) buries a
        /// cursor/keyboard row beneath its own screen row. Only the screen-vs-overlay relation is
        /// pinned: other rows — the webcam, imports, zoom effect rows — may still step between or
        /// around the pair.
        /// </summary>
        private static bool OverlayPinViolated(Project project, List<Track> video)
        {
            for (var i = 0; i < video.Count; i++)
            {
                if (!IsInputOverlayTrack(project, video[i].Id))
                    continue;

                var screen = FindOverlayScreenTrack(project, video[i]);
                if (screen != null && video.IndexOf(screen) > i)
                    return true;
            }
            return false;
        }

        /// <summary>The screen row an overlay row is pinned above: the video row of the
        /// screen-stream media item sharing a link group with one of the overlay's items — the
        /// hard-sync partner <see cref="AddInputOverlayTrack"/> wired. Null when the screen row
        /// is gone (the overlay then draws nothing and pins to nothing).</summary>
        private static Track FindOverlayScreenTrack(Project project, Track overlay)
        {
            foreach (var item in project.Items)
            {
                if (item.TrackId != overlay.Id || item.LinkGroupId == null)
                    continue;

                Guid sourceId;
                if (item.Content is CursorContent cursor)
                    sourceId = cursor.SourceId;
                else if (item.Content is KeyboardContent keyboard)
                    sourceId = keyboard.SourceId;
                else
                    continue;

                var source = project.Sources.FirstOrDefault(s => s.Id == sourceId);
                if (source == null)
                    continue;

                foreach (var partner in project.Items)
                {
                    if (partner.LinkGroupId != item.LinkGroupId
                        || partner.Content is not MediaContent media || media.SourceId != sourceId
                        || !Composition.FrameComposer.IsScreenStream(source, media.StreamIndex))
                        continue;

                    var track = project.Tracks.FirstOrDefault(t => t.Id == partner.TrackId);
                    if (track?.Kind == TrackKind.Video)
                        return track;
                }
            }
            return null;
        }

        /// <summary>Renames a row. Coalesced per track so typing in the header textbox is one undo
        /// entry, not one per keystroke.</summary>
        public void RenameTrack(Guid trackId, string name, object origin = null) =>
            Mutate("Rename Track", ProjectChangeKind.Mapping, $"rename-track:{trackId}", origin,
                p => RequireTrack(p, trackId).Name = name);

        /// <summary>
        /// The inspector/gizmo write path: applies <paramref name="edit"/> to the item and runs the
        /// full pipeline around it. Consecutive calls sharing a non-null
        /// <paramref name="coalesceKey"/> within <see cref="CoalesceWindowMs"/> merge into one undo
        /// entry (spinner mashing). Pass <paramref name="structural"/> when the edit changes what
        /// the player must decode (it rarely does — transforms, volume and transitions are all
        /// mapping changes).
        /// </summary>
        public void EditItem(Guid itemId, Action<Item> edit, string coalesceKey = null,
            bool structural = false, object origin = null)
        {
            ArgumentNullException.ThrowIfNull(edit);

            Mutate("Edit", structural ? ProjectChangeKind.Structural : ProjectChangeKind.Mapping,
                coalesceKey, origin, p => edit(RequireItem(p, itemId)));
        }

        /// <summary>The multi-item form of <see cref="EditItem"/>: every id is edited inside one
        /// mutation, so a write that fans out over a row (the webcam placement, a multi-select
        /// inspector edit) costs one pipeline run, one undo entry and one
        /// <see cref="ProjectChanged"/> — not one per item. Ids not in the project throw, exactly
        /// as they do for a single edit; an empty list is a no-op.</summary>
        public void EditItems(IReadOnlyList<Guid> itemIds, Action<Item> edit, string coalesceKey = null,
            bool structural = false, object origin = null)
        {
            ArgumentNullException.ThrowIfNull(itemIds);
            ArgumentNullException.ThrowIfNull(edit);
            if (itemIds.Count == 0)
                return;

            Mutate("Edit", structural ? ProjectChangeKind.Structural : ProjectChangeKind.Mapping,
                coalesceKey, origin, p =>
                {
                    foreach (var id in itemIds)
                        edit(RequireItem(p, id));
                });
        }

        // -------------------------------------------------------------------------- add / import

        /// <summary>Adds a text card with the editor defaults — "Title", white, centred, sized
        /// against the canvas (8% of its height) — on the topmost video row with that span free
        /// (a fresh row when none is). Returns the live item, or null when the add was rolled
        /// back.</summary>
        public Item AddText(long startTicks, long durationTicks, object origin = null)
        {
            Item created = null;
            var committed = Mutate("Add Text", ProjectChangeKind.Structural, null, origin, p =>
            {
                created = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = FindOrCreateFreeVideoTrack(p, startTicks, durationTicks).Id,
                    TimelineStartTicks = startTicks,
                    DurationTicks = durationTicks,
                    Content = new TextContent
                    {
                        Text = "Title",
                        Size = p.Output.HeightPx * 0.08,
                        Color = "#FFFFFFFF",
                        Align = TextAlign.Center,
                    },
                };
                p.Items.Add(created);
            });
            return committed ? created : null;
        }

        /// <summary>Adds an image item at half canvas width, centred, on the topmost free video
        /// row (a fresh row when none is). Returns the live item, or null when rolled back.</summary>
        public Item AddImage(string path, long startTicks, long durationTicks, object origin = null)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("The image path is empty.", nameof(path));

            Item created = null;
            var committed = Mutate("Add Image", ProjectChangeKind.Structural, null, origin, p =>
            {
                created = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = FindOrCreateFreeVideoTrack(p, startTicks, durationTicks).Id,
                    TimelineStartTicks = startTicks,
                    DurationTicks = durationTicks,
                    Content = new ImageContent { Path = path },
                    Transform = new Transform { Scale = 0.5 },
                };
                p.Items.Add(created);
            });
            return committed ? created : null;
        }

        /// <summary>
        /// Adds a speed effect item on <b>the</b> speed row — found by content, created above
        /// everything when the project has none. The duration is clamped into the gap before the
        /// next speed item; the add is refused — returning null — when an existing speed item
        /// already covers <paramref name="startTicks"/> or the gap is shorter than
        /// <see cref="TimelineOps.MinSegmentTicks"/>. Returns the live item.
        /// </summary>
        public Item AddSpeedEffect(long startTicks, long durationTicks, object origin = null)
        {
            Item created = null;
            var committed = Mutate("Add Speed", ProjectChangeKind.Structural, null, origin, p =>
            {
                // effect items don't extend the project, so an item past the content end would be
                // unreachable on the timeline — clamp into the content span, refuse when there is
                // no room.
                var contentEnd = p.GetDurationTicks();
                if (contentEnd < TimelineOps.MinSegmentTicks)
                    return;
                var start = Math.Clamp(startTicks, 0, contentEnd - TimelineOps.MinSegmentTicks);
                var duration = Math.Min(durationTicks, contentEnd - start);

                var track = FindSpeedTrack(p);
                if (track != null)
                {
                    var next = long.MaxValue;
                    foreach (var other in p.Items)
                    {
                        if (other.TrackId != track.Id)
                            continue;
                        if (other.TimelineStartTicks <= start && start < other.TimelineEndTicks)
                            return;
                        if (other.TimelineStartTicks > start)
                            next = Math.Min(next, other.TimelineStartTicks);
                    }

                    if (next != long.MaxValue)
                    {
                        var gap = next - start;
                        if (gap < TimelineOps.MinSegmentTicks)
                            return;
                        duration = Math.Min(duration, gap);
                    }
                }

                track ??= InsertSpeedTrack(p);
                created = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    TimelineStartTicks = start,
                    DurationTicks = Math.Max(duration, TimelineOps.MinSegmentTicks),
                    Content = new SpeedContent(),
                };
                p.Items.Add(created);
            });
            return committed ? created : null;
        }

        /// <summary>Adds a zoom effect item on a <b>new</b> zoom row composited above the whole
        /// video block — a zoom applies to the rows beneath it, and a new one starts by covering
        /// all of them (drag the row down to narrow its reach). Returns the live item, or null
        /// when rolled back.</summary>
        public Item AddZoomEffect(long startTicks, long durationTicks, object origin = null)
        {
            Item created = null;
            var committed = Mutate("Add Zoom", ProjectChangeKind.Structural, null, origin, p =>
            {
                // clamp into the content span, exactly as AddSpeedEffect does: effect items don't
                // extend the project, so anything past the content end would be unreachable.
                var contentEnd = p.GetDurationTicks();
                if (contentEnd < TimelineOps.MinSegmentTicks)
                    return;
                var start = Math.Clamp(startTicks, 0, contentEnd - TimelineOps.MinSegmentTicks);

                var track = InsertEffectTrackOnTop(p, "Zoom");
                created = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    TimelineStartTicks = start,
                    DurationTicks = Math.Min(durationTicks, contentEnd - start),
                    Content = new ZoomContent(),
                };
                p.Items.Add(created);
            });
            return committed ? created : null;
        }

        /// <summary>Whether the project already carries the (single) speed row — the add-speed
        /// command's CanExecute.</summary>
        public bool HasSpeedTrack => FindSpeedTrack(Project) != null;

        // ------------------------------------------------------------------- input overlay rows

        /// <summary>
        /// Adds the cursor overlay row: a <see cref="TrackKind.Video"/> track named "Cursor"
        /// directly above the recording's screen row (above the keyboard row when one exists),
        /// carrying one <see cref="CursorContent"/> item per screen keep-segment — mirroring each
        /// segment's span and link group, which is what hard-syncs the row to the recording's
        /// trims and cuts. Returns the first live item, or null when refused: no source with
        /// input-capture data, the row already exists, or the screen row has no segments.
        /// </summary>
        public Item AddCursorTrack(object origin = null)
        {
            Item created = null;
            var committed = Mutate("Add Cursor", ProjectChangeKind.Structural, null, origin,
                p => { created = AddInputOverlayTrack(p, cursor: true); });
            return committed ? created : null;
        }

        /// <summary>The keyboard counterpart of <see cref="AddCursorTrack"/>: a row named "Keys"
        /// directly above the screen row but below an existing cursor row, one
        /// <see cref="KeyboardContent"/> item per screen keep-segment.</summary>
        public Item AddKeyboardTrack(object origin = null)
        {
            Item created = null;
            var committed = Mutate("Add Keys", ProjectChangeKind.Structural, null, origin,
                p => { created = AddInputOverlayTrack(p, cursor: false); });
            return committed ? created : null;
        }

        /// <summary>How long a keystroke row takes to arrive or leave, as
        /// <see cref="AddKeyboardTrack"/> writes it — the same 300ms the inspector's animation
        /// spinners default to.</summary>
        private const long KeyboardRowTransitionTicks = 300 * TimeSpan.TicksPerMillisecond;

        /// <summary>Whether the project carries a cursor overlay row — by content, like
        /// <see cref="HasSpeedTrack"/>, so the flag flips off the moment the last item goes.</summary>
        public bool HasCursorTrack => FindInputOverlayTrack(Project, cursor: true) != null;

        /// <summary>Whether the project carries a keyboard overlay row; see
        /// <see cref="HasCursorTrack"/>.</summary>
        public bool HasKeyboardTrack => FindInputOverlayTrack(Project, cursor: false) != null;

        /// <summary>True when any source carries input-capture data whose file exists — what
        /// gates the add-cursor/add-keyboard commands. The existence probe is cached per path:
        /// the flag is polled from CanExecute refreshes, and a sidecar neither appears nor
        /// disappears mid-session.</summary>
        public bool HasInputCapture
        {
            get
            {
                foreach (var source in Project.Sources)
                {
                    if (!String.IsNullOrEmpty(source.InputCapturePath) &&
                        InputCaptureFileExists(source.InputCapturePath))
                        return true;
                }
                return false;
            }
        }

        private readonly Dictionary<string, bool> _inputCaptureExists =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private bool InputCaptureFileExists(string path)
        {
            if (!_inputCaptureExists.TryGetValue(path, out var exists))
                _inputCaptureExists[path] = exists = File.Exists(path);
            return exists;
        }

        /// <summary>The shared body of both overlay factories. Returns the first created item, or
        /// null when the add is refused (see <see cref="AddCursorTrack"/>).</summary>
        private static Item AddInputOverlayTrack(Project project, bool cursor)
        {
            var source = project.Sources.FirstOrDefault(s => !String.IsNullOrEmpty(s.InputCapturePath));
            if (source == null || FindInputOverlayTrack(project, cursor) != null)
                return null;

            // the screen row: the backmost video row playing the capture's *screen stream*. The
            // webcam row plays the same source (a different stream), and the user may have ordered
            // it behind the screen row — matching by source alone would pin to it there, and an
            // overlay row inserted behind the screen frame composes invisibly.
            var screen = project.Tracks
                .Where(t => t.Kind == TrackKind.Video && project.Items.Any(i =>
                    i.TrackId == t.Id && i.Content is MediaContent m && m.SourceId == source.Id
                    && Composition.FrameComposer.IsScreenStream(source, m.StreamIndex)))
                .OrderBy(t => t.Order).ThenBy(t => t.Id)
                .FirstOrDefault();
            if (screen == null)
                return null;

            var segments = project.Items
                .Where(i => i.TrackId == screen.Id && i.Content is MediaContent)
                .OrderBy(i => i.TimelineStartTicks).ToList();
            if (segments.Count == 0)
                return null;

            // cursor sits above the keyboard row when both exist; the keyboard slots directly
            // above the screen row, which leaves it below an existing cursor row.
            var below = screen.Order;
            if (cursor && FindInputOverlayTrack(project, cursor: false) is { } keys)
                below = Math.Max(below, keys.Order);
            var order = below + 1;
            foreach (var t in project.Tracks)
            {
                if (t.Order >= order)
                    t.Order++;
            }

            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Video,
                Name = cursor ? "Cursor" : "Keys",
                Order = order,
            };
            project.Tracks.Add(track);

            Item first = null;
            foreach (var segment in segments)
            {
                // hard-sync: the overlay item joins the segment's link group. A screen segment
                // that was unlinked gets a fresh group shared with its overlay — the overlay
                // must follow the screen either way.
                segment.LinkGroupId ??= Guid.NewGuid();

                var item = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    TimelineStartTicks = segment.TimelineStartTicks,
                    DurationTicks = segment.DurationTicks,
                    Content = cursor
                        ? new CursorContent { SourceId = source.Id }
                        : new KeyboardContent { SourceId = source.Id },
                    // keyboard blocks anchor bottom-centre at half canvas width; the cursor's
                    // position is data-driven and ignores the transform entirely.
                    Transform = cursor
                        ? new Transform()
                        : new Transform { X = 0.5, Y = 0.85, Scale = 0.5 },
                    // a themed glyph needs a shadow to read over light and dark alike, so a cursor
                    // row starts with one (the native style ignores it — see
                    // FrameComposer.DrawCursorItem). A keystroke block draws its own pill and does not.
                    Surround = cursor
                        ? Surround.Create(SurroundKind.Shadow, cursor: true)
                        : null,
                    // on a keyboard row entry/exit animate each keystroke ROW rather than the
                    // block (see FrameComposer.DrawKeyboard): rows slide up into place as they
                    // push the older ones along, and fade away once they have been read.
                    Entry = cursor ? null : new Transition
                    {
                        Kind = TransitionKind.SlideUp,
                        DurationTicks = KeyboardRowTransitionTicks,
                        Easing = TransitionEasing.CubicInOut,
                    },
                    Exit = cursor ? null : new Transition
                    {
                        Kind = TransitionKind.Fade,
                        DurationTicks = KeyboardRowTransitionTicks,
                        Easing = TransitionEasing.CubicInOut,
                    },
                    LinkGroupId = segment.LinkGroupId,
                };
                project.Items.Add(item);
                first ??= item;
            }
            return first;
        }

        /// <summary>The video row carrying cursor (or keyboard) overlay items, or null — by
        /// content, exactly as <see cref="IsSpeedTrack"/> classifies the speed row, and empty
        /// rows never survive a commit (see <see cref="PruneEmptyTracks"/>).</summary>
        private static Track FindInputOverlayTrack(Project project, bool cursor) =>
            project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video && project.Items.Any(i =>
                i.TrackId == t.Id && (cursor ? i.Content is CursorContent : i.Content is KeyboardContent)));

        /// <summary>Whether the row's items are cursor/keyboard overlays — the rows whose sync,
        /// reorder and duplicate are refused (their placement and link groups are structural, not
        /// user-arrangeable).</summary>
        private static bool IsInputOverlayTrack(Project project, Guid trackId) =>
            project.Items.Any(i => i.TrackId == trackId && i.Content is CursorContent or KeyboardContent);

        /// <summary>
        /// Imports an external media file as an overlay: one new <see cref="Source"/> with the
        /// probed streams (mapped exactly as a recording's are), one new track per stream — video
        /// rows stacked above the existing video rows, audio rows at the bottom — and one item per
        /// stream starting at <paramref name="startTicks"/>. The items share a fresh link group
        /// when there is more than one, so the file's rows move as one; video items default to
        /// half canvas width, centred. One undo entry restores all of it. Returns the live items
        /// (empty when the probe had no usable streams or the import was rolled back).
        /// </summary>
        public IReadOnlyList<Item> ImportMedia(string path, MediaProbeResult probe, long startTicks,
            object origin = null)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException("The media path is empty.", nameof(path));
            ArgumentNullException.ThrowIfNull(probe);

            var streams = MapStreams(probe);
            if (streams.Count == 0)
                return Array.Empty<Item>();

            var created = new List<Item>();
            var committed = Mutate("Import Media", ProjectChangeKind.Structural, null, origin, p =>
            {
                var source = new Source { Id = Guid.NewGuid(), Path = path, Streams = streams };
                p.Sources.Add(source);

                var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (String.IsNullOrWhiteSpace(baseName))
                    baseName = "Import";

                // only multi-stream imports are linked: a group of one has nothing to keep in
                // sync. The UI moves and deletes an import group as a unit (it is a per-file
                // group, not a recording-segment group — see IsRippleGroup).
                var linkGroup = streams.Count > 1 ? Guid.NewGuid() : (Guid?)null;

                var videoStreams = streams.Where(s => s.Kind == StreamKind.Video).ToList();
                var audioStreams = streams.Where(s => s.Kind == StreamKind.Audio).ToList();

                for (var i = 0; i < videoStreams.Count; i++)
                {
                    // successive video streams stack upward, the whole file above what was there.
                    var track = InsertVideoTrackOnTop(p,
                        videoStreams.Count == 1 ? baseName : $"{baseName} {i + 1}");
                    created.Add(NewImportItem(track, source, videoStreams[i], probe, startTicks,
                        linkGroup, new Transform { Scale = 0.5 }));
                }

                for (var i = 0; i < audioStreams.Count; i++)
                {
                    // above every existing row except the pinned speed row, which stays on top:
                    // shift it (and only it) up, as the insert helpers renumber.
                    var belowSpeed = p.Tracks.Where(t => !IsSpeedTrack(p, t))
                                             .Select(t => t.Order).ToList();
                    var order = belowSpeed.Count > 0 ? belowSpeed.Max() + 1 : 0;
                    foreach (var existing in p.Tracks)
                    {
                        if (existing.Order >= order)
                            existing.Order++;
                    }
                    var track = new Track
                    {
                        Id = Guid.NewGuid(),
                        Kind = TrackKind.Audio,
                        Name = audioStreams.Count == 1 ? baseName : $"{baseName} {i + 1}",
                        Order = order,
                    };
                    p.Tracks.Add(track);
                    created.Add(NewImportItem(track, source, audioStreams[i], probe, startTicks,
                        linkGroup, null));
                }

                p.Items.AddRange(created);
            });
            return committed ? created : Array.Empty<Item>();
        }

        /// <summary>
        /// Points a source at a new file, replacing its stream descriptions with the reprobed
        /// ones. Tolerant of a file that no longer matches: a stream the new file is missing keeps
        /// its old description (items referencing it stay valid; playback of it fails soft), and
        /// extra streams are added when their index is free. Returns a note per mismatch — empty
        /// means the new file matched cleanly. A null <paramref name="reprobe"/> updates the path
        /// only.
        /// </summary>
        public IReadOnlyList<string> RelinkSource(Guid sourceId, string newPath,
            MediaProbeResult reprobe, object origin = null)
        {
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentException("The new source path is empty.", nameof(newPath));

            var notes = new List<string>();
            Mutate("Relink Source", ProjectChangeKind.Structural, null, origin, p =>
            {
                var source = p.Sources.FirstOrDefault(s => s.Id == sourceId)
                    ?? throw new ArgumentException($"Source {sourceId} is not in the project.", nameof(sourceId));

                source.Path = newPath;
                if (reprobe == null)
                    return;

                var fresh = MapStreams(reprobe);
                var updated = new List<SourceStream>();
                foreach (var old in source.Streams)
                {
                    var match = fresh.FirstOrDefault(n => n.Index == old.Index && n.Kind == old.Kind);
                    if (match != null)
                    {
                        updated.Add(match);
                        fresh.Remove(match);
                    }
                    else
                    {
                        updated.Add(old);
                        notes.Add($"The new file has no {old.Kind} stream at index {old.Index}; keeping the original description.");
                    }
                }

                foreach (var extra in fresh)
                {
                    if (updated.All(s => s.Index != extra.Index))
                        updated.Add(extra);
                    notes.Add($"The new file has an extra {extra.Kind} stream at index {extra.Index}.");
                }

                source.Streams = updated;
            });
            return notes;
        }

        /// <summary>
        /// Drops a source and everything that plays it: every item referencing it, and any track
        /// those items left empty — including a row the session opened with, which
        /// <see cref="PruneEmptyTracks"/> deliberately protects but which has nothing left to hold
        /// once its file is gone. The missing-media dialog's "Remove"; one undo entry restores
        /// source, items and rows together. False means no such source (or nothing changed).
        /// </summary>
        public bool RemoveSource(Guid sourceId, object origin = null) =>
            Mutate("Remove Media", ProjectChangeKind.Structural, null, origin, p =>
            {
                var source = p.Sources.FirstOrDefault(s => s.Id == sourceId);
                if (source == null)
                    return false;

                var touched = p.Items.Where(i => PlaysSource(i, sourceId))
                                     .Select(i => i.TrackId).ToHashSet();
                p.Items.RemoveAll(i => PlaysSource(i, sourceId));
                p.Sources.Remove(source);
                p.Tracks.RemoveAll(t => touched.Contains(t.Id) && p.Items.All(i => i.TrackId != t.Id));
                return true;
            }, failureValue: false);

        /// <summary>
        /// Disables (hides video rows / mutes audio rows) or re-enables every row that plays
        /// <paramref name="sourceId"/>, in one mutation — the missing-media dialog's "Skip", and
        /// the symmetric restore a later Locate performs so the rows a skip hid come back with the
        /// file. One undo entry (and one player rebuild) however many streams the source has; a
        /// no-op when the rows are already in the requested state.
        /// </summary>
        public void SetSourceRowsEnabled(Guid sourceId, bool enabled, object origin = null) =>
            Mutate(enabled ? "Show Media" : "Skip Media", ProjectChangeKind.Structural, null, origin, p =>
            {
                var trackIds = p.Items.Where(i => PlaysSource(i, sourceId))
                                      .Select(i => i.TrackId).ToHashSet();
                foreach (var track in p.Tracks)
                {
                    if (!trackIds.Contains(track.Id))
                        continue;

                    if (track.Kind == TrackKind.Audio)
                        track.Muted = !enabled;
                    else
                        track.Hidden = !enabled;
                }
            });

        /// <summary>The tracks carrying items that play <paramref name="sourceId"/> — the rows the
        /// missing-media dialog's "Skip" hides or mutes so the project still opens.</summary>
        public IReadOnlyList<Track> GetTracksForSource(Guid sourceId)
        {
            var trackIds = Project.Items.Where(i => PlaysSource(i, sourceId))
                                        .Select(i => i.TrackId).ToHashSet();
            return Project.Tracks.Where(t => trackIds.Contains(t.Id)).ToList();
        }

        /// <summary>The <b>referenced</b> sources whose file no longer exists on disk — what the
        /// window's missing-media dialog is built from, and what the render refuses to run without.
        /// A source no item plays is dead weight the composition never opens, so its file being
        /// gone blocks nothing. Live references into <see cref="Project"/>.</summary>
        public IReadOnlyList<Source> GetMissingSources() =>
            Project.Sources.Where(s => IsSourceReferenced(Project, s.Id) && !SourceFileExists(s)).ToList();

        /// <summary>True when at least one item plays a stream of this source (see
        /// <see cref="GetMissingSources"/>). Static because the render path checks a detached
        /// project snapshot, with no session in reach.</summary>
        public static bool IsSourceReferenced(Project project, Guid sourceId) =>
            project?.Items != null && project.Items.Any(i => PlaysSource(i, sourceId));

        /// <summary>A source with no path at all counts as missing — it can only have come from a
        /// hand-edited or half-written file, and nothing can open it.</summary>
        public static bool SourceFileExists(Source source) =>
            !String.IsNullOrEmpty(source?.Path) && File.Exists(source.Path);

        /// <summary>Whether the item consumes this source's data — media streams, and the input
        /// overlays that read its capture sidecar, so removing a source takes its cursor/keyboard
        /// rows with it rather than stranding items that fail validation.</summary>
        private static bool PlaysSource(Item item, Guid sourceId) => item.Content switch
        {
            MediaContent media => media.SourceId == sourceId,
            CursorContent cursor => cursor.SourceId == sourceId,
            KeyboardContent keyboard => keyboard.SourceId == sourceId,
            _ => false,
        };

        // ------------------------------------------------------------------------------ undo/redo

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>Restores the state before the newest undo entry, capturing the current state
        /// as its redo. The <see cref="Project"/> instance is replaced; selection is restored
        /// filtered to ids that still exist. Raises <see cref="ProjectChanged"/> as
        /// <see cref="ProjectChangeKind.Structural"/> — a restore can change anything.</summary>
        public void Undo()
        {
            if (_gesture != null)
                throw new InvalidOperationException("Cannot undo while a gesture is in progress.");
            if (_undo.Count == 0)
                return;

            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(new UndoEntry
            {
                Label = entry.Label,
                BeforeJson = Project.ToJson(),
                BeforeSelection = SelectionSnapshot(),
                TimestampMs = Clock(),
            });

            RestoreHistoryState(entry);
        }

        /// <summary>Symmetric counterpart of <see cref="Undo"/>.</summary>
        public void Redo()
        {
            if (_gesture != null)
                throw new InvalidOperationException("Cannot redo while a gesture is in progress.");
            if (_redo.Count == 0)
                return;

            var entry = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            // pushed directly, not through PushUndo: this push must not clear the rest of the
            // redo stack, and it must never coalesce.
            _undo.Add(new UndoEntry
            {
                Label = entry.Label,
                BeforeJson = Project.ToJson(),
                BeforeSelection = SelectionSnapshot(),
                TimestampMs = Clock(),
            });

            RestoreHistoryState(entry);
        }

        // ---------------------------------------------------------------------------- persistence

        /// <summary>Writes any pending state through the persistence sink immediately — the
        /// window's close path, so a debounced save cannot be lost.</summary>
        public void FlushSave() => WritePendingSave();

        // ------------------------------------------------------------------------------- pipeline

        /// <summary>The one mutation pipeline (see the class remarks). Returns false when the
        /// mutation was rolled back by validation, true otherwise (including a no-op).</summary>
        private bool Mutate(string label, ProjectChangeKind kind, string coalesceKey, object origin,
            Action<Project> edit) =>
            Mutate<object>(label, kind, coalesceKey, origin, p =>
            {
                edit(p);
                return null;
            }, RollbackSentinel) != RollbackSentinel;

        private static readonly object RollbackSentinel = new object();

        /// <summary>Value-returning form of the pipeline. A validation rollback returns
        /// <paramref name="failureValue"/>; otherwise the edit's own result is returned, whether
        /// or not it changed anything.</summary>
        private T Mutate<T>(string label, ProjectChangeKind kind, string coalesceKey, object origin,
            Func<Project, T> edit, T failureValue = default)
        {
            var before = Project.ToJson();
            var beforeSelection = SelectionSnapshot();

            T result;
            try
            {
                result = edit(Project);
                Project.Normalize();
            }
            catch
            {
                // an exception out of the edit callback is a programming error, but it must not
                // strand a half-mutated model behind it.
                RestoreInPlace(before);
                throw;
            }

            var errors = Project.Validate();
            if (errors.Count > 0)
            {
                RestoreInPlace(before);
                ValidationFailed?.Invoke(this, new ValidationFailureEventArgs(label, errors));
                return failureValue;
            }

            var after = Project.ToJson();
            if (after == before)
                return result;

            FilterSelectionToLiveItems();

            if (_gesture != null)
            {
                if (kind > _gesture.Kind)
                    _gesture.Kind = kind;
                RaiseProjectChanged(ProjectChangeKind.Preview, origin ?? _gesture.Origin);
                return result;
            }

            PushUndo(new UndoEntry
            {
                Label = label,
                BeforeJson = before,
                BeforeSelection = beforeSelection,
                CoalesceKey = coalesceKey,
                TimestampMs = Clock(),
            });
            SchedulePersist(after);
            RaiseProjectChanged(kind, origin);
            return result;
        }

        private void PushUndo(UndoEntry entry)
        {
            _redo.Clear();

            if (!_suppressCoalesce && entry.CoalesceKey != null && _undo.Count > 0)
            {
                var top = _undo[^1];
                if (top.CoalesceKey == entry.CoalesceKey &&
                    entry.TimestampMs - top.TimestampMs < CoalesceWindowMs)
                {
                    // the run's oldest before-state already spans this edit; sliding the timestamp
                    // keeps a steady stream of spinner clicks in one entry.
                    top.TimestampMs = entry.TimestampMs;
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            _undo.Add(entry);
            if (_undo.Count > UndoCapacity)
                _undo.RemoveAt(0);
            _suppressCoalesce = false;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RestoreHistoryState(UndoEntry entry)
        {
            Project = Project.FromJson(entry.BeforeJson);
            RestoreSelection(entry.BeforeSelection);
            _suppressCoalesce = true;
            SchedulePersist(entry.BeforeJson);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            RaiseProjectChanged(ProjectChangeKind.Structural, null);
        }

        /// <summary>Restores serialized state into the live instance without replacing it — the
        /// rollback/cancel path, where consumers may hold the reference mid-interaction. Only
        /// undo/redo replace the instance.</summary>
        private void RestoreInPlace(string json)
        {
            var restored = Project.FromJson(json);
            Project.Version = restored.Version;
            Project.Output = restored.Output;
            Project.Sources = restored.Sources;
            Project.Tracks = restored.Tracks;
            Project.Items = restored.Items;
        }

        private void SchedulePersist(string json)
        {
            if (_persist == null)
                return;

            _pendingSaveJson = json;
            _saveScheduler(WritePendingSave);
        }

        /// <summary>Latest-wins: however many scheduled callbacks the host coalesced or reordered,
        /// whichever runs first writes the newest state and the rest find nothing to do.</summary>
        private void WritePendingSave()
        {
            var json = _pendingSaveJson;
            if (json == null)
                return;

            _pendingSaveJson = null;
            _persist.Write(Encoding.UTF8.GetBytes(json));
        }

        private void RaiseProjectChanged(ProjectChangeKind kind, object origin) =>
            ProjectChanged?.Invoke(this, new ProjectChangedEventArgs(kind, origin));

        // -------------------------------------------------------------------- selection internals

        /// <summary>The selection as ids, anchor first — both the public view and what undo
        /// entries store.</summary>
        private Guid[] SelectionSnapshot()
        {
            if (_selectedIds.Count == 0)
                return Array.Empty<Guid>();

            var ids = new List<Guid>(_selectedIds.Count);
            if (_selectionAnchor is Guid anchor && _selectedIds.Contains(anchor))
                ids.Add(anchor);
            foreach (var id in _selectedIds)
            {
                if (id != _selectionAnchor)
                    ids.Add(id);
            }
            return ids.ToArray();
        }

        /// <summary>Drops selected ids that no longer resolve to an item — run after every
        /// committed mutation so a delete cannot leave a ghost selection.</summary>
        private void FilterSelectionToLiveItems()
        {
            if (_selectedIds.Count == 0)
                return;

            var removed = _selectedIds.RemoveWhere(id => Project.Items.All(i => i.Id != id));
            if (removed == 0)
                return;

            if (_selectionAnchor is Guid anchor && !_selectedIds.Contains(anchor))
                _selectionAnchor = _selectedIds.Count > 0 ? _selectedIds.First() : (Guid?)null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Replaces the selection with the given ids (anchor first), filtered to items
        /// that exist in the current project.</summary>
        private void RestoreSelection(Guid[] ids)
        {
            var live = ids.Where(id => Project.Items.Any(i => i.Id == id)).ToList();
            var anchor = live.Count > 0 ? live[0] : (Guid?)null;

            var changed = live.Count != _selectedIds.Count ||
                          !live.All(_selectedIds.Contains) ||
                          anchor != _selectionAnchor;
            if (!changed)
                return;

            _selectedIds.Clear();
            foreach (var id in live)
                _selectedIds.Add(id);
            _selectionAnchor = anchor;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ----------------------------------------------------------------------- model helpers

        /// <summary>
        /// Where a newly added text card or image goes: the <b>frontmost</b> video row when it has
        /// <c>[startTicks, startTicks + durationTicks)</c> free, otherwise a fresh "Overlay" row
        /// above everything. A new item is always composed in front of what is already there —
        /// dropping it onto a lower free row instead would put it behind content the user can see,
        /// which reads as an add that did nothing.
        ///
        /// <para>Hidden and locked rows are never candidates, for the same reason: an item added to
        /// a hidden row is composed by nothing (the webcam row is Hidden on every fresh edit that
        /// never enabled the overlay). Cursor/keyboard rows are never candidates either — they are
        /// video rows, but their content is structural (pinned, refuses reorder/duplicate, deleted
        /// with the row), and a text card must not inherit any of that. Reuse is still the common
        /// case — a second card at a different time lands on the same overlay row rather than
        /// stacking up empty ones.</para>
        /// </summary>
        private static Track FindOrCreateFreeVideoTrack(Project project, long startTicks, long durationTicks)
        {
            var end = startTicks + durationTicks;
            var frontmost = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden && !t.Locked
                                                      && !IsInputOverlayTrack(project, t.Id))
                                          .OrderByDescending(t => t.Order)
                                          .ThenByDescending(t => t.Id)
                                          .FirstOrDefault();

            if (frontmost != null && !project.Items.Any(i => i.TrackId == frontmost.Id &&
                    i.TimelineStartTicks < end && startTicks < i.TimelineEndTicks))
                return frontmost;

            return InsertVideoTrackOnTop(project, "Overlay");
        }

        /// <summary>Adds a video track composited above every existing video row, shifting the
        /// orders of whatever sits at or above the slot (the audio rows) down a row.</summary>
        private static Track InsertVideoTrackOnTop(Project project, string name)
        {
            var videoOrders = project.Tracks.Where(t => t.Kind == TrackKind.Video)
                                            .Select(t => t.Order).ToList();
            var order = videoOrders.Count > 0 ? videoOrders.Max() + 1 : 0;
            foreach (var track in project.Tracks)
            {
                if (track.Order >= order)
                    track.Order++;
            }

            var created = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = name, Order = order };
            project.Tracks.Add(created);
            return created;
        }

        /// <summary>Adds a zoom row composited above the whole video block — video and zoom rows
        /// alike — shifting whatever sits at or above the slot (the audio rows and the speed row)
        /// up one, exactly as <see cref="InsertVideoTrackOnTop"/> renumbers.</summary>
        private static Track InsertEffectTrackOnTop(Project project, string name)
        {
            var blockOrders = project.Tracks.Where(t => t.Kind != TrackKind.Audio && !IsSpeedTrack(project, t))
                                            .Select(t => t.Order).ToList();
            var order = blockOrders.Count > 0 ? blockOrders.Max() + 1 : 0;
            foreach (var track in project.Tracks)
            {
                if (track.Order >= order)
                    track.Order++;
            }

            var created = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = name, Order = order };
            project.Tracks.Add(created);
            return created;
        }

        /// <summary>Adds <b>the</b> speed row, ordered above every existing row: it is excluded
        /// from z-order and paint entirely, and keeping it on top means the video and audio
        /// blocks never step over it.</summary>
        private static Track InsertSpeedTrack(Project project)
        {
            var order = project.Tracks.Count > 0 ? project.Tracks.Max(t => t.Order) + 1 : 0;
            var created = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed", Order = order };
            project.Tracks.Add(created);
            return created;
        }

        /// <summary>The row carrying the project's speed items, or null. Resolved by content, not
        /// name — <see cref="Project.Validate"/> keeps it unique.</summary>
        private static Track FindSpeedTrack(Project project) =>
            project.Tracks.FirstOrDefault(t => IsSpeedTrack(project, t));

        /// <summary>Whether this is the pinned speed row: an effect row is a speed row or a zoom
        /// row by what its items are, and empty effect rows never survive a commit (see
        /// <see cref="PruneEmptyTracks"/>).</summary>
        private static bool IsSpeedTrack(Project project, Track track) =>
            track.Kind == TrackKind.Effect &&
            project.Items.Any(i => i.TrackId == track.Id && i.Content is SpeedContent);

        /// <summary>Removes tracks that were created during this session (not part of the project
        /// the session opened with) and no longer carry any item. Effect rows are pruned
        /// regardless of when they were created: the timeline classifies an effect row by its
        /// items' content, so an empty one is unclassifiable and must never survive.</summary>
        private void PruneEmptyTracks(Project project) =>
            project.Tracks.RemoveAll(t => (t.Kind == TrackKind.Effect || !_initialTrackIds.Contains(t.Id)) &&
                                          project.Items.All(i => i.TrackId != t.Id));

        /// <summary>Probe → <see cref="SourceStream"/> list, video then audio — the identical
        /// mapping <see cref="RecordingProject.Build"/> applies to a recording's own streams.</summary>
        private static List<SourceStream> MapStreams(MediaProbeResult probe)
        {
            var streams = new List<SourceStream>();
            foreach (var v in probe.VideoStreams ?? Array.Empty<VideoStreamProbe>())
                streams.Add(new SourceStream
                {
                    Index = v.StreamIndex,
                    Kind = StreamKind.Video,
                    Width = v.Width,
                    Height = v.Height,
                    AvgFrameRateNum = v.AvgFrameRateNum,
                    AvgFrameRateDen = v.AvgFrameRateDen,
                    IsVariableFrameRate = v.IsVariableFrameRate,
                    StartTimeTicks = v.StartTimeTicks,
                    DurationTicks = v.DurationTicks,
                });
            foreach (var a in probe.AudioStreams ?? Array.Empty<AudioStreamProbe>())
                streams.Add(new SourceStream
                {
                    Index = a.StreamIndex,
                    Kind = StreamKind.Audio,
                    DurationTicks = a.DurationTicks,
                });
            return streams;
        }

        private static Item NewImportItem(Track track, Source source, SourceStream stream,
            MediaProbeResult probe, long startTicks, Guid? linkGroup, Transform transform)
        {
            // the stream's own duration, the container's when the stream carries none, and a
            // visible trimmable default when neither is known.
            var duration = stream.DurationTicks > 0 ? stream.DurationTicks
                : probe.DurationTicks > 0 ? probe.DurationTicks
                : TimeSpan.FromSeconds(5).Ticks;

            return new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = duration,
                Content = new MediaContent
                {
                    SourceId = source.Id,
                    StreamIndex = stream.Index,
                    SourceInTicks = 0,
                },
                Transform = transform ?? new Transform(),
                LinkGroupId = linkGroup,
            };
        }

        private static bool Covers(Item item, long timelineTicks) =>
            timelineTicks >= item.TimelineStartTicks && timelineTicks < item.TimelineEndTicks;

        private static Track RequireTrack(Project project, Guid trackId)
        {
            var track = project.Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track == null)
                throw new ArgumentException($"Track {trackId} is not in the project.", nameof(trackId));

            return track;
        }

        private static Item RequireItem(Project project, Guid itemId)
        {
            var item = project.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null)
                throw new ArgumentException($"Item {itemId} is not in the project.", nameof(itemId));

            return item;
        }

        /// <summary>One undo (or redo) step: the complete serialized state — and selection — from
        /// before the labelled operation ran.</summary>
        private sealed class UndoEntry
        {
            public string Label { get; init; }

            public string BeforeJson { get; init; }

            public Guid[] BeforeSelection { get; init; }

            public string CoalesceKey { get; init; }

            /// <summary><see cref="Environment.TickCount64"/> at push time — the coalesce window's
            /// reference point, slid forward as edits merge in.</summary>
            public long TimestampMs { get; set; }
        }
    }
}
