using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Prisc.Drawing.Graphics;
using Prisc.Drawing.Rendering;

namespace Prisc.Drawing.Tools
{
    internal class ToolPointer : ToolBase
    {
        public SelectionMode Selection => _selectMode;

        public enum SelectionMode
        {
            None,
            Move, // object(s) are moved
            HandleDrag, // object is edited via dragging a handle (e.g. resize, rotate)
            GroupSelection
        }

        private SelectionMode _selectMode = SelectionMode.None;

        // Object which is currently resized:
        private GraphicBase _handleGrabbedObject;
        private int _handleGrabbed;
        private double _handleRatio;

        // Keep state about last and current point (used to edit objects via dragging, e.g. move and resize).
        // Drag bookkeeping is root-window space in DOUBLE precision. The previous screen-space scheme
        // (PointToScreen/PointToClient) rounds to whole physical pixels on every event; at canvas zoom
        // or DPI scale != 1 each pointer move injects a sub-pixel error into the delta, and over a drag
        // the errors accumulate into visible drift between the pointer and the dragged object. Root
        // space keeps the original property that mattered: the delta stays correct if the canvas
        // transform changes mid-drag (e.g. wheel zoom while dragging).
        private Point _lastPointRoot;

        // A press on a locked body starts a marquee (it cannot move), and selects that graphic only
        // if the press is released without dragging past ClickSlop root-space pixels.
        private const double ClickSlop = 4;
        private GraphicBase _lockedPress;
        private Point _pressRoot;

        bool _wasEdit;

        public ToolPointer() : base(() => HelperFunctions.DefaultCursor)
        { }

        private static Point CanvasToRoot(DrawingCanvas canvas, Point canvasPt) =>
            canvas.TranslatePoint(canvasPt, (Visual)TopLevel.GetTopLevel(canvas) ?? canvas) ?? canvasPt;

        private static Point RootToCanvas(DrawingCanvas canvas, Point rootPt) =>
            ((Visual)TopLevel.GetTopLevel(canvas) ?? canvas).TranslatePoint(rootPt, canvas) ?? rootPt;

        /// <summary>The top-most graphic under <paramref name="point"/>, or a selected graphic's
        /// handle there. Locked graphics are skipped unless <paramref name="includeLocked"/>, in
        /// which case only their body can hit (<see cref="GraphicBase.ShowsHandles"/> is false).</summary>
        public GraphicBase MakeHitTest(DrawingCanvas drawingCanvas, Point point, out int handleNumber, bool includeLocked = false)
        {
            // runs on every hover pointer-move, so it is a plain top-most-first loop (no LINQ).
            // a selected graphic's handle wins over any object body, even a body above it in
            // z-order — so body hits are remembered but the handle scan covers the whole list.
            var dpi = drawingCanvas.CanvasUiElementScale;
            var list = drawingCanvas.GraphicsList;

            GraphicBase hitBody = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var g = list[i];

                // hidden graphics are invisible to hit-testing; locked graphics only for the callers
                // that select (the pointer tool, the right-click menu), never for the eraser.
                if (g.Hidden || (g.Locked && !includeLocked))
                    continue;

                var hit = g.MakeHitTest(point, dpi);

                // Test if we start dragging a handle (e.g. resize, rotate, etc.; only if control is selected and cursor is on the handle)
                if (g.IsSelected && hit > 0)
                {
                    handleNumber = hit;
                    return g;
                }

                // Test if we start dragging an object
                if (hit == 0 && hitBody == null)
                {
                    hitBody = g;
                }
            }

            if (hitBody != null)
            {
                handleNumber = 0;
                return hitBody;
            }

            handleNumber = -1;
            return null;
        }

        /// <summary>
        /// Handle mouse down.
        /// Start moving, resizing or group selection.
        /// </summary>
        public override void OnMouseDown(DrawingCanvas drawingCanvas, PointerState s, int clickCount)
        {
            var wpfPt = s.Position;
            _lastPointRoot = CanvasToRoot(drawingCanvas, s.Position);

            int handleNumber;
            var graphic = MakeHitTest(drawingCanvas, wpfPt, out handleNumber, includeLocked: true);
            drawingCanvas.SetHoveredGraphic(null); // the hover cue is for the mouse-up state only

            // a locked graphic cannot be dragged, so its body acts as background (a press there
            // starts a marquee, as it did when locked graphics were transparent) and only a click
            // selects it, on release
            _lockedPress = graphic is { Locked: true } ? graphic : null;
            _pressRoot = _lastPointRoot;
            if (_lockedPress != null)
                graphic = null;

            // Capture mouse until MouseUp event is received
            drawingCanvas.CaptureMouse(s.Pointer);

            // Unselect all other objects if:
            if (
                // ... dragging a handle, OR
                handleNumber > 0 ||
                // ... dragging an unselected object or creating a selection rectangle,
                ((graphic == null || !graphic.IsSelected) &&
                 // ... and the user didn’t press Ctrl or Shift. (exact-equality modifier check, as in WPF)
                 s.Modifiers != KeyModifiers.Control && s.Modifiers != KeyModifiers.Shift))
            {
                drawingCanvas.UnselectAllExcept(graphic);
            }

            // If we create a selection rectangle, this shouldn’t be considered an edit for the undo history.
            // Similarly, if we mouse down on an object or handle but don’t end up dragging it anywhere, it’s also not an edit,
            // so we don’t set _wasEdit to true until the mouse-move event with the button pressed.
            _wasEdit = false;

            if (graphic != null)
            {
                if (handleNumber > 0)
                {
                    // an arrowless step badge has nothing for its badge handle to pivot around, so
                    // either handle pulls an arrow out; latched here so the drag keeps doing that
                    // once the arrow exists
                    if (graphic is GraphicCount { HasArrow: false } && handleNumber == GraphicCount.BadgeHandle)
                        handleNumber = GraphicCount.ArrowHandle;

                    // Alt on a path anchor pulls fresh handles out of it (the drag then drives its
                    // Out handle). Continuing a path from an end anchor is the pen's alone.
                    if (graphic is GraphicPath path)
                    {
                        handleNumber = path.ResolveGrab(handleNumber, s.Modifiers);
                        path.SetActiveAnchor(handleNumber);
                    }

                    _selectMode = SelectionMode.HandleDrag;
                    _handleGrabbedObject = graphic;
                    _handleGrabbed = handleNumber;

                    // initial aspect ratio
                    var rotatableGraphic = graphic as GraphicRectangle;
                    if (rotatableGraphic != null)
                        _handleRatio = rotatableGraphic.UnrotatedBounds.Width / rotatableGraphic.UnrotatedBounds.Height;
                    else
                        _handleRatio = 0;
                }
                else
                {
                    // a body press is no longer about any one anchor
                    if (graphic is GraphicPath path)
                        path.ClearActiveAnchor();

                    _selectMode = SelectionMode.Move;
                    drawingCanvas.Cursor = CursorResources.Move;
                }

                graphic.IsSelected = true;
            }
            else
            {
                // Click on background — start a selection rectangle for group selection.
                var rect = HelperFunctions.CreateRectSafe(wpfPt.X, wpfPt.Y, wpfPt.X + 1, wpfPt.Y + 1);
                var gsr = new GraphicSelectionRectangle(rect);
                drawingCanvas.GraphicsList.Add(gsr);

                _selectMode = SelectionMode.GroupSelection;
            }
        }

        private void SetHitTest(DrawingCanvas drawingCanvas, Point point)
        {
            int handleNumber;
            var graphic = MakeHitTest(drawingCanvas, point, out handleNumber, includeLocked: true);
            drawingCanvas.SetHoveredGraphic(graphic);

            if (handleNumber < 0 || graphic.Locked) // hit no objects, or one that cannot move
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
            else if (handleNumber == 0) // hit body of object
                drawingCanvas.Cursor = CursorResources.Move;
            else // hit resize handle
                drawingCanvas.Cursor = graphic.GetHandleCursor(handleNumber);
        }

        /// <summary>
        /// Handle mouse move.
        /// Set cursor, move/resize, make group selection.
        /// </summary>
        public override void OnMouseMove(DrawingCanvas drawingCanvas, PointerState s)
        {
            // Exclude all cases except left button on/off.
            if (s.MiddlePressed || s.RightPressed)
            {
                drawingCanvas.SetHoveredGraphic(null);
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                return;
            }

            var wpfPt = s.Position;
            var lastWpfPt = RootToCanvas(drawingCanvas, _lastPointRoot);

            // Set cursor when left button is not pressed
            if (!s.LeftPressed)
            {
                SetHitTest(drawingCanvas, wpfPt);
                return;
            }

            if (!drawingCanvas.IsMouseCaptured)
                return;

            _wasEdit = true;

            // Find difference between previous and current position
            double dx = wpfPt.X - lastWpfPt.X;
            double dy = wpfPt.Y - lastWpfPt.Y;

            _lastPointRoot = CanvasToRoot(drawingCanvas, wpfPt);

            switch (_selectMode)
            {
                case SelectionMode.Move:
                    foreach (var o in drawingCanvas.GraphicsList.SelectedItems)
                        if (!o.Locked)
                            o.Move(dx, dy);
                    break;

                case SelectionMode.HandleDrag:
                    if (_handleGrabbedObject != null)
                    {
                        // if should maintain aspect ratio of a rectangle
                        var shiftPressed = (s.Modifiers & KeyModifiers.Shift) != 0;
                        var lineGraphic = _handleGrabbedObject as GraphicLine;
                        // text and step badges number their handles differently (rotation / arrow
                        // tip), so the rectangle's corner-resize aspect lock must not apply to them
                        var rotatableGraphic = _handleGrabbedObject is GraphicText ? null : _handleGrabbedObject as GraphicRectangle;
                        if (shiftPressed && rotatableGraphic != null && IsCornerHandle(_handleGrabbed)
                            && double.IsFinite(_handleRatio) && _handleRatio > 0)
                        {
                            // only the grabbed corner moves, so the aspect-locked size goes through the
                            // graphic's own MoveHandleTo — which keeps an image's crop clamp and the
                            // usual raise. The anchor is read from the raw edges that handle never
                            // writes, so it stays put even after the corner is dragged across it.
                            var anchor = GetOppositeCorner(rotatableGraphic, _handleGrabbed);
                            var corner = GetAspectLockedCorner(anchor, rotatableGraphic.UnapplyRotation(wpfPt), _handleRatio, _handleGrabbed);
                            rotatableGraphic.MoveHandleTo(rotatableGraphic.ApplyRotation(corner), _handleGrabbed);
                        }
                        // angle snapping is an ENDPOINT gesture (handles 1/2): it re-aims the free end
                        // around the anchored one. The arrow's curve handle (3) moves no endpoint, so
                        // it is excluded — snapping it would fling the shaft off the pointer.
                        else if (shiftPressed && lineGraphic != null && _handleGrabbed <= 2)
                        {
                            var anchor = _handleGrabbed == 1 ? lineGraphic.LineEnd : lineGraphic.LineStart;
                            wpfPt = HelperFunctions.SnapPointToCommonAngle(anchor, wpfPt, false);
                            _handleGrabbedObject.MoveHandleTo(wpfPt, _handleGrabbed);
                        }
                        // a step badge behaves like a line: the arrow tip pivots around the badge
                        // center, and the badge handle pivots around the anchored tip
                        else if (shiftPressed && _handleGrabbedObject is GraphicCount count
                                 && (_handleGrabbed == GraphicCount.ArrowHandle || count.HasArrow))
                        {
                            var anchor = _handleGrabbed == GraphicCount.ArrowHandle ? count.Center : count.ArrowTip;
                            wpfPt = HelperFunctions.SnapPointToCommonAngle(anchor, wpfPt, false);
                            _handleGrabbedObject.MoveHandleTo(wpfPt, _handleGrabbed);
                        }
                        // the line's curve handle reads Shift itself: it snaps the bend symmetric and
                        // into 10% steps of the chord length
                        else if (lineGraphic != null)
                        {
                            lineGraphic.MoveHandleTo(wpfPt, _handleGrabbed, s.Modifiers);
                        }
                        // a path handle reads the modifiers itself: Shift snaps the handle to 15° steps,
                        // Alt breaks a smooth anchor's symmetry
                        else if (_handleGrabbedObject is GraphicPath path)
                        {
                            path.MoveHandleTo(wpfPt, _handleGrabbed, s.Modifiers);
                        }
                        else
                        {
                            _handleGrabbedObject.MoveHandleTo(wpfPt, _handleGrabbed);
                        }
                        drawingCanvas.Cursor = _handleGrabbedObject.GetHandleCursor(_handleGrabbed);
                    }

                    break;

                case SelectionMode.GroupSelection:
                    // Resize selection rectangle
                    drawingCanvas[drawingCanvas.Count - 1].MoveHandleTo(wpfPt, 5);
                    break;
            }
        }

        /// <summary>
        /// Handle mouse up.
        /// Return to normal state.
        /// </summary>
        public override void OnMouseUp(DrawingCanvas drawingCanvas, PointerState s)
        {
            if (!drawingCanvas.IsMouseCaptured)
            {
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                _selectMode = SelectionMode.None;
                return;
            }

            if (_handleGrabbedObject != null)
            {
                // after resizing/rotating
                _handleGrabbedObject.Normalize();
                _handleGrabbedObject = null;
            }

            if (_selectMode == SelectionMode.GroupSelection)
            {
                GraphicSelectionRectangle r = (GraphicSelectionRectangle)(drawingCanvas[drawingCanvas.Count - 1]);
                r.Normalize();
                Rect rect = r.Bounds;

                drawingCanvas.GraphicsList.Remove(drawingCanvas[drawingCanvas.Count - 1]);

                var root = CanvasToRoot(drawingCanvas, s.Position);
                var clicked = _lockedPress != null
                              && Math.Abs(root.X - _pressRoot.X) <= ClickSlop && Math.Abs(root.Y - _pressRoot.Y) <= ClickSlop;
                if (clicked)
                {
                    _lockedPress.IsSelected = true;
                }
                else
                {
                    foreach (var g in GraphicsInMarquee(drawingCanvas.GraphicsList, rect))
                        g.IsSelected = true;
                }

                _lockedPress = null;
            }

            drawingCanvas.ReleaseMouseCapture();

            Point point = s.Position;
            SetHitTest(drawingCanvas, point);

            if (_wasEdit && _selectMode == SelectionMode.Move)
            {
                // Body-move deltas are root-space doubles, so TranslateCachedBounds leaves each
                // graphic's cached Bounds at a fractional offset of the pre-drag ROUNDED bounds —
                // and no later invalidation ever re-rounds it. Drop the Bounds caches so the next
                // read recomputes (and re-rounds) at the final position, exactly like the old
                // per-read getter; the export offset for a screenshot doc stays integral.
                foreach (var g in drawingCanvas.GraphicsList.SelectedItems)
                {
                    if (g.Locked)
                        continue; // never moved
                    g.RenderCache.Clear(InvalidationAspects.Bounds);
                    g.OnGestureCompleted();
                }
                drawingCanvas.GraphicsList.RequestValidation();
            }

            _selectMode = SelectionMode.None;
            if (_wasEdit)
                drawingCanvas.AddCommandToHistory(false);
        }

        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
        }

        /// <summary>
        /// The marquee rule, shared with the eraser: the graphics <paramref name="rect"/> FULLY
        /// contains, in list order. Hidden and locked graphics are excluded, as is a marquee
        /// still in the list.
        /// </summary>
        internal static List<GraphicBase> GraphicsInMarquee(GraphicCollection list, Rect rect)
        {
            var result = new List<GraphicBase>();
            foreach (var g in list)
            {
                if (g is GraphicSelectionRectangle)
                    continue;

                // hidden and locked graphics are excluded from marquee selection
                if (g.Hidden || g.Locked)
                    continue;

                if (rect.Contains(g.Bounds))
                    result.Add(g);
            }

            return result;
        }

        private static bool IsCornerHandle(int handleNumber) =>
            handleNumber is 1 or 3 or 5 or 7;

        /// <summary>The (unrotated) corner opposite a rectangle's corner handle, read from the raw
        /// edges that handle's MoveHandleTo never writes — so it stays the gesture's fixed anchor
        /// even once the dragged corner crosses it and the edges are mid-drag inverted.</summary>
        internal static Point GetOppositeCorner(GraphicRectangle graphic, int handleNumber)
        {
            return handleNumber switch
            {
                1 => new Point(graphic.Right, graphic.Bottom),
                3 => new Point(graphic.Left, graphic.Bottom),
                5 => new Point(graphic.Left, graphic.Top),
                7 => new Point(graphic.Right, graphic.Top),
                _ => throw new ArgumentOutOfRangeException(nameof(handleNumber)),
            };
        }

        /// <summary>Where an aspect-locked corner handle lands (unrotated): the <paramref name="aspect"/>
        /// box anchored at <paramref name="anchor"/> that just covers <paramref name="pointer"/>, on
        /// whichever side of the anchor the pointer is — dragging across the anchor flips the box
        /// through it, exactly as a free resize does. Only the corner is returned, never a
        /// two-point Rect: unlike WPF's, Avalonia's Rect(Point, Point) does not normalize.</summary>
        internal static Point GetAspectLockedCorner(Point anchor, Point pointer, double aspect, int handleNumber)
        {
            var dx = pointer.X - anchor.X;
            var dy = pointer.Y - anchor.Y;
            var width = Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect);
            var height = width / aspect;

            // exactly on the anchor's axis, fall back to the handle's own side
            var signX = dx != 0 ? Math.Sign(dx) : handleNumber is 3 or 5 ? 1 : -1;
            var signY = dy != 0 ? Math.Sign(dy) : handleNumber is 5 or 7 ? 1 : -1;
            return new Point(anchor.X + signX * width, anchor.Y + signY * height);
        }
    }
}
