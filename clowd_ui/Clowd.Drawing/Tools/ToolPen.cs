using System;
using Avalonia;
using Avalonia.Input;
using Clowd.Config;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The pen: click to place a corner anchor, press-and-drag to pull mirrored handles out of the
    /// anchor being placed (Alt: a cusp with one handle, Shift: 45° snap), click anchor 0 to close
    /// (the rubber band snaps onto it within <see cref="GraphicPath.CloseSnapRadius"/>), Enter /
    /// Escape / double-click / a tool switch to finish. Closing, Enter, Escape and double-click all
    /// hand the finished path to the pointer, like the one-shot drawing tools; a pen re-selected
    /// afterwards stays active: the next click starts a new path — or, while exactly one path is
    /// selected, grabs one of its anchors or handles and drags it in place. Pressing and releasing
    /// an end anchor of an open path without moving re-enters the extending mode at that end (the
    /// pen only: the pointer just drags anchors). Hovering the stroke of any path shows a ghost
    /// anchor (<see cref="GraphicPath.GhostPoint"/>); a press there inserts a real one without
    /// changing the shape and drags it.
    ///
    /// Every handler keys off <see cref="_mode"/>, never canvas.IsMouseCaptured, so tests drive it
    /// with synthetic pointer states (Pointer == null, for which capture is a no-op). A path being
    /// extended is a real graphic in the list and is selected; if the Layers panel removes or
    /// deselects it behind our back the next event finishes it silently.
    ///
    /// History: one step per path created or extended (committed on finish — property-bar edits
    /// made meanwhile fold into it), one per handle or anchor drag, one per anchor insert (with any
    /// drag that follows it), delete or type toggle. An untouched continuation leaves no step.
    /// </summary>
    internal class ToolPen : ToolBase
    {
        private enum Mode
        {
            Idle,           // nothing in progress; a press starts a path or grabs a handle
            Extending,      // between clicks: the next press adds an anchor (or closes)
            PlacingAnchor,  // pressed on a new anchor: a drag shapes its handles
            DraggingHandle, // pressed on an existing anchor/handle of the selected path
        }

        private Mode _mode;
        private GraphicPath _path;

        // DraggingHandle
        private int _grab;
        private bool _continueCandidate;
        private bool _edited;

        // PlacingAnchor: the anchor the drag shapes, and whether it is the closing drag onto anchor 0
        private int _placing;
        private bool _placingIncoming;

        private Point _downPt;
        private bool _dragged;

        // what the extension started from, so finishing an untouched continuation leaves no step:
        // the anchors as stored (after any reversal) and the closed flag, plus a flag for edits
        // that leave the anchors alone (the property bar)
        private PathAnchor[] _anchorsBefore = Array.Empty<PathAnchor>();
        private bool _closedBefore;
        private bool _touched;
        private bool _reversed;

        // the path showing the ghost anchor of an idle hover, if any
        private GraphicPath _ghostPath;

        // the path the last release closed on anchor 0, so the double-click's second press (which
        // the canvas routes to Activate) does not also toggle that anchor
        private GraphicPath _justClosed;

        public ToolPen() : base(() => CursorResources.Pen)
        { }

        /// <summary>True while the pen is between clicks on <paramref name="path"/>.</summary>
        internal bool IsExtending(GraphicPath path) => _mode == Mode.Extending && ReferenceEquals(_path, path);

        /// <summary>True (once) when the last release closed <paramref name="path"/> on its first
        /// anchor; any later press or key clears it.</summary>
        internal bool ConsumeJustClosed(GraphicPath path)
        {
            if (!ReferenceEquals(_justClosed, path))
                return false;
            _justClosed = null;
            return true;
        }

        /// <summary>A change to the path being extended that the pen did not make itself (a
        /// property-bar edit): it belongs to the path's step, and makes an untouched continuation
        /// touched.</summary>
        internal void MarkEdited() => _touched = true;

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            Validate(canvas);
            _justClosed = null;
            SetGhost(null, default);

            // right button is the canvas's (context menu); the second press of a double-click goes
            // to Activate instead, and a triple-click's third press must not re-grab the endpoint
            // the double-click just finished on
            if (!s.LeftPressed || clickCount > 1)
                return;

            var pt = s.Position;
            var dpi = canvas.CanvasUiElementScale;
            _dragged = false;

            switch (_mode)
            {
            case Mode.Extending:
                if (_path.IsNearFirstAnchor(pt, dpi))
                {
                    // closing: the drag (if any) shapes anchor 0's In handle, Out mirrored
                    _path.Closed = true;
                    _placing = 0;
                    _placingIncoming = true;
                    _downPt = pt;
                    _mode = Mode.PlacingAnchor;
                    canvas.CaptureMouse(s.Pointer);
                    return;
                }

                if ((s.Modifiers & KeyModifiers.Shift) != 0)
                    pt = HelperFunctions.SnapPointToCommonAngle(_path.LastAnchor.P, pt, false);

                // a press on the anchor just placed (double-click residue, a stutter) adds nothing
                if (GraphicLine.Distance(pt, _path.LastAnchor.P) < GraphicPath.ClickThreshold * dpi.DpiScaleX)
                    return;

                _path.AppendAnchor(PathAnchor.Corner(pt));
                _placing = _path.AnchorCount - 1;
                _placingIncoming = false;
                _downPt = pt;
                _mode = Mode.PlacingAnchor;
                canvas.CaptureMouse(s.Pointer);
                return;

            case Mode.Idle:
                var selected = SelectedPath(canvas);
                int handle;
                if (selected != null && (handle = selected.MakeHitTest(pt, dpi)) > 0)
                {
                    _path = selected;
                    _grab = selected.ResolveGrab(handle, s.Modifiers);
                    selected.SetActiveAnchor(_grab);
                    _continueCandidate = selected.IsEndpointHandle(handle) && (s.Modifiers & KeyModifiers.Alt) == 0;
                    _edited = false;
                    _downPt = pt;
                    _mode = Mode.DraggingHandle;
                    canvas.CaptureMouse(s.Pointer);
                    return;
                }

                // on the stroke of a path: insert an anchor there (selecting that path) and drag it
                if (FindInsertion(canvas, pt, dpi) is { } ins)
                {
                    if (selected != ins.Path)
                    {
                        canvas.UnselectAll();
                        ins.Path.IsSelected = true;
                    }

                    _path = ins.Path;
                    _grab = _path.AnchorHandle(_path.InsertAnchor(ins.Segment, ins.T));
                    _continueCandidate = false;
                    _edited = true;
                    _downPt = pt;
                    _mode = Mode.DraggingHandle;
                    canvas.CaptureMouse(s.Pointer);
                    return;
                }

                // anywhere else (empty canvas, a body, another kind of graphic) starts a new path.
                // Its style is the pen's saved settings, read directly: the canvas's style
                // properties may still be bound to the path that was selected until now (see
                // DrawingCanvas.SyncObjectState), and a new path is not a copy of the last one.
                canvas.UnselectAll();
                var settings = canvas.ResolveToolSettings(ToolType.Pen);
                _path = new GraphicPath(settings.ObjectColor, settings.LineWidth, pt)
                {
                    DashStyle = settings.DashStyle,
                    IsSelected = true,
                };
                canvas.GraphicsList.Add(_path);
                _anchorsBefore = Array.Empty<PathAnchor>();
                _closedBefore = false;
                _touched = true;
                _reversed = false;
                _placing = 0;
                _placingIncoming = false;
                _downPt = pt;
                _mode = Mode.PlacingAnchor;
                canvas.CaptureMouse(s.Pointer);
                return;
            }
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            Validate(canvas);

            var pt = s.Position;
            var dpi = canvas.CanvasUiElementScale;

            switch (_mode)
            {
            case Mode.PlacingAnchor:
                if (!_dragged && GraphicLine.Distance(pt, _downPt) >= GraphicPath.ClickThreshold * dpi.DpiScaleX)
                    _dragged = true;
                if (_dragged)
                    _path.SetHandlesFromDrag(_placing, pt, s.Modifiers, _placingIncoming); // idempotent, so synthetic Shift replays are fine
                break;

            case Mode.DraggingHandle:
                if (!_dragged && GraphicLine.Distance(pt, _downPt) >= GraphicPath.ClickThreshold * dpi.DpiScaleX)
                    _dragged = true;
                if (_dragged)
                {
                    _path.MoveHandleTo(pt, _grab, s.Modifiers);
                    _edited = true;
                    canvas.Cursor = _path.GetHandleCursor(_grab);
                }
                break;

            case Mode.Extending:
                // near anchor 0 the band snaps onto it (the click there closes); the raw cursor
                // decides, so a Shift snap cannot pull it in or out of range
                if (_path.IsNearFirstAnchor(pt, dpi))
                    pt = _path.Anchors[0].P;
                else if ((s.Modifiers & KeyModifiers.Shift) != 0)
                    pt = HelperFunctions.SnapPointToCommonAngle(_path.LastAnchor.P, pt, false);
                _path.PreviewPoint = pt;
                canvas.Cursor = CursorResources.Pen;
                break;

            case Mode.Idle:
                var selected = SelectedPath(canvas);
                var handle = selected?.MakeHitTest(pt, dpi) ?? -1;
                if (handle > 0)
                {
                    SetGhost(null, default);
                    canvas.Cursor = selected.IsEndpointHandle(handle) ? CursorResources.Pen : selected.GetHandleCursor(handle);
                }
                else
                {
                    var ins = FindInsertion(canvas, pt, dpi);
                    SetGhost(ins?.Path, ins?.Point ?? default);
                    canvas.Cursor = CursorResources.Pen;
                }

                break;
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, PointerState s)
        {
            Validate(canvas);

            // also absorbs the fake up a right-click sends and the release after a double-click
            if (_mode != Mode.PlacingAnchor && _mode != Mode.DraggingHandle)
                return;

            canvas.ReleaseMouseCapture();

            if (_mode == Mode.PlacingAnchor)
            {
                _path.Normalize();
                if (_path.Closed)
                {
                    // a closed path is done: hand it to the pointer, still selected, like Enter
                    _justClosed = _path;
                    Finish(canvas);
                    canvas.Tool = ToolType.Pointer;
                }
                else
                {
                    _mode = Mode.Extending;
                    _path.PreviewPoint = s.Position;
                }

                return;
            }

            // DraggingHandle: a motionless press on an end anchor continues the path from there
            if (_continueCandidate && !_dragged)
            {
                var path = _path;
                _mode = Mode.Idle;
                _path = null;
                BeginExtending(canvas, path, atStart: _grab == 1);
                return;
            }

            _path.Normalize();
            _path.OnGestureCompleted();
            if (_edited)
                canvas.AddCommandToHistory(false);
            _edited = false;
            _mode = Mode.Idle;
            _path = null;
        }

        public override bool OnKeyDown(DrawingCanvas canvas, Key key)
        {
            Validate(canvas);
            _justClosed = null;

            // Enter finishes the path and hands it to the pointer (still selected, so it can be
            // edited straight away), like every other drawing tool reverting once it is done.
            // Escape needs nothing here: the editor's CancelCurrentOperation finishes it through
            // AbortOperation and reverts too.
            if (key == Key.Enter && _mode != Mode.DraggingHandle && _mode != Mode.PlacingAnchor)
            {
                Finish(canvas);
                canvas.Tool = ToolType.Pointer;
                return true;
            }

            if (_mode != Mode.Extending)
                return false;

            switch (key)
            {

            case Key.Back:
            case Key.Delete:
                // step back one anchor; backing out of the first one drops the path
                _path.RemoveLastAnchor();
                if (_path.AnchorCount == 0)
                {
                    var path = _path;
                    _mode = Mode.Idle;
                    _path = null;
                    canvas.GraphicsList.Remove(path);
                    canvas.AddCommandToHistory(false); // a no-op for a path that was never committed
                }

                return true;
            }

            return false;
        }

        public override void CommitPending(DrawingCanvas canvas)
        {
            SetGhost(null, default);
            Finish(canvas);
        }

        // Escape FINISHES a pen path (every reference editor does); only a path with fewer than two
        // anchors is discarded. Capture loss mid-drag therefore keeps what was dragged so far.
        public override void AbortOperation(DrawingCanvas canvas)
        {
            SetGhost(null, default);
            Finish(canvas);
        }

        public override void OnMouseLeave(DrawingCanvas canvas) => SetGhost(null, default);

        /// <summary>
        /// Ends whatever is in progress and commits it: a drag becomes a step if it edited anything;
        /// an extension becomes a step if anything about the path changed, drops the path when fewer
        /// than two anchors remain, and otherwise (an untouched continuation) leaves the path exactly
        /// as it was — reversed back if it was being continued from its start. The path stays
        /// selected. Returns false only for that untouched case, so a double-click on an end anchor
        /// can fall through to the anchor-type toggle.
        /// </summary>
        internal bool Finish(DrawingCanvas canvas)
        {
            if (_mode == Mode.Idle)
                return false;

            var path = _path;
            var mode = _mode;
            var edited = _edited;

            // reset before touching the canvas so anything re-entrant (SyncObjectState, a Validate
            // from a nested event) sees an idle tool
            _mode = Mode.Idle;
            _path = null;
            _edited = false;
            _continueCandidate = false;
            path.PreviewPoint = null;

            // a finished path is an object again: Delete now deletes it, not its last anchor
            path.ClearActiveAnchor();

            if (mode == Mode.DraggingHandle)
            {
                path.Normalize();
                path.OnGestureCompleted();
                if (edited)
                    canvas.AddCommandToHistory(false);
                return edited;
            }

            if (!canvas.GraphicsList.Contains(path))
                return true; // removed behind our back; that removal was its own step

            if (path.AnchorCount < 2)
            {
                canvas.GraphicsList.Remove(path);
                canvas.AddCommandToHistory(false); // a no-op for a path that was never committed
                return true;
            }

            // "untouched" is a bitwise comparison with what the extension started from: a removed
            // anchor replaced by a new one leaves the count alone but is still an edit
            var touched = _touched || path.Closed != _closedBefore
                          || !path.Anchors.AsSpan().SequenceEqual(_anchorsBefore);
            if (!touched)
            {
                if (_reversed)
                    path.ReverseDirection(); // back to how it was stored: no phantom step
                return false;
            }

            path.Normalize();
            canvas.AddCommandToHistory(false);
            return true;
        }

        /// <summary>Enters the extending mode on an existing open path at one of its ends; the
        /// start is handled by walking the path backwards until it is finished.</summary>
        internal void BeginExtending(DrawingCanvas canvas, GraphicPath path, bool atStart)
        {
            if (atStart)
                path.ReverseDirection();

            _reversed = atStart;
            _anchorsBefore = (PathAnchor[])path.Anchors.Clone();
            _closedBefore = path.Closed;
            _touched = false;
            _path = path;
            _edited = false;
            _mode = Mode.Extending;
            path.PreviewPoint = null;
            canvas.Cursor = CursorResources.Pen;
        }

        // the path being worked on may have been deleted or deselected by the Layers panel: a
        // deletion was its own step, so just forget it; a deselection finishes it
        private void Validate(DrawingCanvas canvas)
        {
            if (_path == null)
                return;

            if (!canvas.GraphicsList.Contains(_path))
            {
                _mode = Mode.Idle;
                _path = null;
                _edited = false;
                _continueCandidate = false;
            }
            else if (!_path.IsSelected)
            {
                Finish(canvas);
            }
        }

        private readonly record struct Insertion(GraphicPath Path, int Segment, double T, Point Point);

        // the topmost visible, unlocked path whose stroke passes near the cursor, and where on it
        // an anchor would go
        private static Insertion? FindInsertion(DrawingCanvas canvas, Point pt, DpiScale dpi)
        {
            var list = canvas.GraphicsList;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is GraphicPath path && !path.Hidden && !path.Locked
                    && path.TryFindInsertion(pt, dpi, out var segment, out var t, out var onStroke))
                    return new Insertion(path, segment, t, onStroke);
            }

            return null;
        }

        private void SetGhost(GraphicPath path, Point point)
        {
            if (_ghostPath != null && _ghostPath != path)
                _ghostPath.GhostPoint = null;
            _ghostPath = path;
            if (path != null)
                path.GhostPoint = point;
        }

        // the one path whose anchors and handles the idle pen edits
        private static GraphicPath SelectedPath(DrawingCanvas canvas)
        {
            var selected = canvas.GraphicsList.SelectedItems;
            return selected.Length == 1 && selected[0] is GraphicPath path && !path.Hidden && !path.Locked ? path : null;
        }
    }
}
