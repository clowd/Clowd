using System;
using Avalonia.Threading;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.History
{
    /// <summary>
    /// The persistence boundary between the history engine and <see cref="DrawingCanvas.StateUpdated"/>
    /// (final-design §B.6): discrete actions (step append / undo / redo) raise StateUpdated
    /// immediately with the freshly-serialized <c>{BackgroundColor, Graphics[]}</c> payload the
    /// engine already built — identical to the old behavior for every discrete action — while
    /// merge-in-place rewrites (the slider/drag scrub path, which raise StateChanged with a null
    /// State) only arm a 150 ms trailing-edge debounce timer. When the timer fires — or when
    /// <see cref="Flush"/> is called at teardown — the live document is serialized ONCE and
    /// delivered. A 5-second scrub therefore raises a handful of StateUpdated events instead of
    /// one per pointer event: nothing O(document) remains on the pointer-move path.
    ///
    /// Deferral rule: <see cref="Flush"/> never serializes while scratch state is live — an active
    /// tool drag, or a text/image graphic in edit mode — because the live document then contains
    /// uncommitted (possibly about-to-be-aborted) work the old system never persisted. The flush
    /// re-arms and retries after the drag/edit ends; the drag-end/edit-end discrete commit
    /// supersedes it anyway. Consequence: a close mid-drag may drop only the un-flushed merge tail
    /// — it never writes discarded scratch, which is strictly safer than persisting it.
    /// </summary>
    internal sealed class AutosaveThrottle
    {
        private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(150);

        private readonly DrawingCanvas _canvas;
        private DispatcherTimer _timer; // created lazily on the first merge raise
        private bool _pending;

        public AutosaveThrottle(DrawingCanvas canvas)
        {
            _canvas = canvas;
        }

        /// <summary>
        /// Routes one <see cref="UndoManager.StateChanged"/> raise. <paramref name="e"/> carries
        /// the serialized document for discrete kinds and a null State for merges (the engine
        /// deliberately does not serialize on the scrub path).
        /// </summary>
        public void OnHistoryChanged(HistoryChangeKind kind, StateChangedEventArgs e)
        {
            if (kind == HistoryChangeKind.Merge)
            {
                // trailing edge: every merge re-arms the timer, so it fires 150 ms after the
                // LAST rewrite of a scrub (graphics.json lands shortly after the user lets go)
                _pending = true;
                _timer ??= new DispatcherTimer(DebounceInterval, DispatcherPriority.Background, (_, _) => Flush());
                _timer.Stop();
                _timer.Start();
                return;
            }

            // append / undo / redo: the payload in the args is the complete current document, so
            // it supersedes any armed merge tail (latest-wins) and is delivered synchronously,
            // with the matching undo-chain snapshot attached (history.json moves in lockstep).
            // The history payload is only built when someone consumes the event.
            Cancel();
            _canvas.RaiseStateUpdated(new StateChangedEventArgs(e.State, _canvas.HasStateUpdatedSubscribers ? _canvas.SerializeHistory() : null));
        }

        /// <summary>
        /// Fires any armed trailing edge synchronously: serializes the live document once and
        /// raises StateUpdated. Called by the debounce timer, by
        /// <see cref="DrawingCanvas.FlushPendingState"/> (EditorWindow teardown) and on
        /// DetachedFromVisualTree — the latest committed state is on disk, airtight at close.
        /// No-op when nothing is pending.
        /// </summary>
        public void Flush()
        {
            if (!_pending)
                return;

            // scratch-deferral (see class doc): never serialize the live document while a tool
            // drag or a text/image edit is in progress — keep the tail armed and retry after the
            // scratch state resolves (the drag-end/edit-end discrete commit supersedes it anyway).
            bool scratch = _canvas.IsToolDragActive;
            if (!scratch)
            {
                foreach (var g in _canvas.GraphicsList)
                {
                    if (g is GraphicText { Editing: true } || g is GraphicImage { Editing: true })
                    {
                        scratch = true;
                        break;
                    }
                }
            }

            if (scratch)
            {
                _timer ??= new DispatcherTimer(DebounceInterval, DispatcherPriority.Background, (_, _) => Flush());
                _timer.Stop();
                _timer.Start();
                return; // _pending stays true
            }

            Cancel();
            _canvas.RaiseStateUpdated(new StateChangedEventArgs(UndoManager.SerializeDocument(_canvas),
                                                                _canvas.HasStateUpdatedSubscribers ? _canvas.SerializeHistory() : null));

            // trailing edge of a property-bar scrub: while _pending was armed the validator capped
            // shadow bakes at interactive resolution (IsInteractiveScrubActive) — one more pass
            // re-bakes them at full res, mirroring the ToolPointer drag-end RequestValidation.
            _canvas.GraphicsList?.RequestValidation();
        }

        /// <summary>
        /// Drops any armed trailing edge without raising. Used when the pending state has been
        /// superseded (a discrete raise carries a newer document) or invalidated wholesale
        /// (RestoreState replaces the document — contract #23: no raise on restore-load).
        /// </summary>
        public void Cancel()
        {
            _pending = false;
            _timer?.Stop();
        }

        /// <summary>
        /// True while a merge tail is armed — i.e. the user is mid-scrub on a property-bar slider.
        /// The frame validator treats this like an active tool drag and caps shadow bakes at
        /// interactive resolution; <see cref="Flush"/> re-bakes full-res on the trailing edge.
        /// </summary>
        internal bool IsScrubActive => _pending;
    }
}
