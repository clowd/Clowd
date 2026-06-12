using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
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
        // Drag bookkeeping stays screen-space (PixelPoint) via canvas.PointToScreen / PointToClient.
        private PixelPoint _lastPointScr = new PixelPoint(0, 0);

        bool _wasEdit;

        public ToolPointer() : base(() => HelperFunctions.DefaultCursor)
        { }

        public GraphicBase MakeHitTest(DrawingCanvas drawingCanvas, Point point, out int handleNumber)
        {
            var dpi = drawingCanvas.CanvasUiElementScale;
            var controls = drawingCanvas.GraphicsList.Select(gv => new
            {
                gv,
                gv.IsSelected,
                HitTest = gv.MakeHitTest(point, dpi)
            }).Reverse().ToArray();

            // Test if we start dragging a handle (e.g. resize, rotate, etc.; only if control is selected and cursor is on the handle)
            var grabHandle = controls.FirstOrDefault(g => g.IsSelected && g.HitTest > 0);
            if (grabHandle != null)
            {
                handleNumber = grabHandle.HitTest;
                return grabHandle.gv;
            }

            // Test if we start dragging an object
            var grabObject = controls.FirstOrDefault(g => g.HitTest == 0);
            if (grabObject != null)
            {
                handleNumber = 0;
                return grabObject.gv;
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
            _lastPointScr = drawingCanvas.PointToScreen(s.Position);

            int handleNumber;
            var graphic = MakeHitTest(drawingCanvas, wpfPt, out handleNumber);

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
            var graphic = MakeHitTest(drawingCanvas, point, out handleNumber);

            if (handleNumber < 0) // hit no objects
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
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                return;
            }

            var wpfPt = s.Position;
            var lastWpfPt = drawingCanvas.PointToClient(_lastPointScr);

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

            _lastPointScr = drawingCanvas.PointToScreen(wpfPt);

            switch (_selectMode)
            {
                case SelectionMode.Move:
                    foreach (var o in drawingCanvas.GraphicsList.SelectedItems)
                        o.Move(dx, dy);
                    break;

                case SelectionMode.HandleDrag:
                    if (_handleGrabbedObject != null)
                    {
                        // if should maintain aspect ratio of a rectangle
                        var shiftPressed = (s.Modifiers & KeyModifiers.Shift) != 0;
                        var lineGraphic = _handleGrabbedObject as GraphicLine;
                        var rotatableGraphic = _handleGrabbedObject as GraphicRectangle;
                        var rotatableDestRect = GetTransformedRect(
                            rotatableGraphic?.UnrotatedBounds,
                            _handleGrabbed,
                            rotatableGraphic?.UnapplyRotation(wpfPt) ?? default(Point));
                        if (shiftPressed && rotatableGraphic != null && _handleRatio != 0 && rotatableDestRect != null)
                        {
                            var sourceRatio = _handleRatio;
                            rotatableDestRect = ScaleRectToAspect(rotatableDestRect.Value, sourceRatio);
                            rotatableDestRect = TranslateDestAroundHandle(rotatableGraphic.UnrotatedBounds, rotatableDestRect.Value, _handleGrabbed);
                            rotatableGraphic.Left = rotatableDestRect.Value.Left;
                            rotatableGraphic.Bottom = rotatableDestRect.Value.Bottom;
                            rotatableGraphic.Right = rotatableDestRect.Value.Right;
                            rotatableGraphic.Top = rotatableDestRect.Value.Top;
                        }
                        else if (shiftPressed && lineGraphic != null)
                        {
                            var anchor = _handleGrabbed == 1 ? lineGraphic.LineEnd : lineGraphic.LineStart;
                            wpfPt = HelperFunctions.SnapPointToCommonAngle(anchor, wpfPt, false);
                            _handleGrabbedObject.MoveHandleTo(wpfPt, _handleGrabbed);
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

                foreach (var g in drawingCanvas.GraphicsList)
                {
                    if (rect.Contains(g.Bounds))
                    {
                        g.IsSelected = true;
                    }
                }
            }

            drawingCanvas.ReleaseMouseCapture();

            Point point = s.Position;
            SetHitTest(drawingCanvas, point);

            _selectMode = SelectionMode.None;
            if (_wasEdit)
                drawingCanvas.AddCommandToHistory(false);
        }

        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
        }

        // decision #3: WPF Rect.Empty sentinels become Rect? null in these helpers.
        private Rect? GetTransformedRect(Rect? source, int handleNumber, Point point)
        {
            if (source == null)
                return null;

            switch (handleNumber)
            {
                case 1:
                    return HelperFunctions.CreateRectSafe(point.X, point.Y, source.Value.Right, source.Value.Bottom);
                case 3:
                    return HelperFunctions.CreateRectSafe(source.Value.Left, point.Y, point.X, source.Value.Bottom);
                case 5:
                    return HelperFunctions.CreateRectSafe(source.Value.Left, source.Value.Top, point.X, point.Y);
                case 7:
                    return HelperFunctions.CreateRectSafe(point.X, source.Value.Top, source.Value.Right, point.Y);
                default:
                    return null;
            }
        }

        private Rect? TranslateDestAroundHandle(Rect source, Rect dest, int handleNumber)
        {
            switch (handleNumber)
            {
                case 5:
                    var topLeft = source.TopLeft;
                    return new Rect(new Point(topLeft.X + dest.Width, topLeft.Y + dest.Height), topLeft);
                case 7:
                    var topRight = source.TopRight;
                    return new Rect(new Point(topRight.X - dest.Width, topRight.Y + dest.Height), topRight);
                case 1:
                    var botRight = source.BottomRight;
                    return new Rect(new Point(botRight.X - dest.Width, botRight.Y - dest.Height), botRight);
                case 3:
                    var botLeft = source.BottomLeft;
                    return new Rect(new Point(botLeft.X + dest.Width, botLeft.Y - dest.Height), botLeft);
                default:
                    return null;
            }
        }

        private Rect ScaleRectToAspect(Rect dest, double sourceAspect, bool keepWidth = true, bool keepHeight = true)
        {
            // Avalonia Rect is immutable; compute width/height locally (position is ignored by callers).
            double destWidth, destHeight;

            double destAspect = dest.Width / dest.Height;

            if (sourceAspect > destAspect)
            {
                // wider than high keep the width and scale the height
                destWidth = dest.Width;
                destHeight = dest.Width / sourceAspect;

                if (keepHeight)
                {
                    double resizePerc = dest.Height / destHeight;
                    destWidth = dest.Width * resizePerc;
                    destHeight = dest.Height;
                }
            }
            else
            {
                // higher than wide – keep the height and scale the width
                destHeight = dest.Height;
                destWidth = dest.Height * sourceAspect;

                if (keepWidth)
                {
                    double resizePerc = dest.Width / destWidth;
                    destWidth = dest.Width;
                    destHeight = dest.Height * resizePerc;
                }
            }

            return new Rect(0, 0, destWidth, destHeight);
        }
    }
}
