using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

        // Keep state about last and current point (used to edit objects via dragging, e.g. move and resize)
        private Point _lastPoint = new Point(0, 0);

        bool _wasEdit;

        public ToolPointer() : base(HelperFunctions.DefaultCursor)
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
        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            _lastPoint = e.GetPosition(drawingCanvas);
            int handleNumber;
            var graphic = MakeHitTest(drawingCanvas, _lastPoint, out handleNumber);

            // Capture mouse until MouseUp event is received
            drawingCanvas.CaptureMouse();

            // Unselect all other objects if:
            if (
                // ... dragging a handle, OR
                handleNumber > 0 ||
                // ... dragging an unselected object or creating a selection rectangle,
                ((graphic == null || !graphic.IsSelected) &&
                 // ... and the user didn’t press Ctrl or Shift.
                 Keyboard.Modifiers != ModifierKeys.Control && Keyboard.Modifiers != ModifierKeys.Shift))
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
                    drawingCanvas.Cursor = Cursors.SizeAll;
                }

                graphic.IsSelected = true;
            }
            else
            {
                // Click on background — start a selection rectangle for group selection.
                var rect = HelperFunctions.CreateRectSafe(_lastPoint.X, _lastPoint.Y, _lastPoint.X + 1, _lastPoint.Y + 1);
                var gsr = new GraphicSelectionRectangle(rect);
                drawingCanvas.GraphicsList.Add(gsr);

                _selectMode = SelectionMode.GroupSelection;
            }
        }

        /// <summary>
        /// Handle mouse move.
        /// Set cursor, move/resize, make group selection.
        /// </summary>
        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            // Exclude all cases except left button on/off.
            if (e.MiddleButton == MouseButtonState.Pressed ||
                e.RightButton == MouseButtonState.Pressed)
            {
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                return;
            }

            Point point = e.GetPosition(drawingCanvas);

            // Set cursor when left button is not pressed
            if (e.LeftButton == MouseButtonState.Released)
            {
                int handleNumber;
                var graphic = MakeHitTest(drawingCanvas, point, out handleNumber);
                drawingCanvas.Cursor =
                    handleNumber < 0 ? HelperFunctions.DefaultCursor :
                    handleNumber == 0 ? Cursors.Hand :
                    graphic.GetHandleCursor(handleNumber);

                return;
            }

            if (!drawingCanvas.IsMouseCaptured)
                return;

            _wasEdit = true;

            // Find difference between previous and current position
            double dx = point.X - _lastPoint.X;
            double dy = point.Y - _lastPoint.Y;

            _lastPoint = point;

            switch (_selectMode)
            {
                case SelectionMode.Move:
                    foreach (var o in drawingCanvas.SelectedItems)
                        o.Move(dx, dy);
                    break;

                case SelectionMode.HandleDrag:
                    if (_handleGrabbedObject != null)
                    {
                        // if should maintain aspect ratio of a rectangle
                        var shiftPressed = Keyboard.IsKeyDown(Key.RightShift) || Keyboard.IsKeyDown(Key.LeftShift);
                        var lineGraphic = _handleGrabbedObject as GraphicLine;
                        var rotatableGraphic = _handleGrabbedObject as GraphicRectangle;
                        var rotatableDestRect = GetTransformedRect(
                            rotatableGraphic?.UnrotatedBounds ?? Rect.Empty, 
                            _handleGrabbed,
                            rotatableGraphic?.UnapplyRotation(point) ?? default(Point));
                        if (shiftPressed && rotatableGraphic != null && _handleRatio != 0 && !rotatableDestRect.IsEmpty)
                        {
                            var sourceRatio = _handleRatio;
                            rotatableDestRect = ScaleRectToAspect(rotatableDestRect, sourceRatio);
                            rotatableDestRect = TranslateDestAroundHandle(rotatableGraphic.UnrotatedBounds, rotatableDestRect, _handleGrabbed);
                            rotatableGraphic.Left = rotatableDestRect.Left;
                            rotatableGraphic.Bottom = rotatableDestRect.Bottom;
                            rotatableGraphic.Right = rotatableDestRect.Right;
                            rotatableGraphic.Top = rotatableDestRect.Top;
                        }
                        else if (shiftPressed && lineGraphic != null)
                        {
                            var anchor = _handleGrabbed == 1 ? lineGraphic.LineEnd : lineGraphic.LineStart;
                            point = HelperFunctions.SnapPointToCommonAngle(anchor, point, false);
                            _handleGrabbedObject.MoveHandleTo(point, _handleGrabbed);
                        }
                        else
                        {
                            _handleGrabbedObject.MoveHandleTo(point, _handleGrabbed);
                        }
                        drawingCanvas.Cursor = _handleGrabbedObject.GetHandleCursor(_handleGrabbed);
                    }

                    break;

                case SelectionMode.GroupSelection:
                    // Resize selection rectangle
                    drawingCanvas[drawingCanvas.Count - 1].MoveHandleTo(point, 5);
                    break;
            }
        }

        /// <summary>
        /// Handle mouse up.
        /// Return to normal state.
        /// </summary>
        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
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
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
            _selectMode = SelectionMode.None;
            if (_wasEdit)
                drawingCanvas.AddCommandToHistory();
        }

        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
        }

        private Rect GetTransformedRect(Rect source, int handleNumber, Point point)
        {
            if (source.IsEmpty)
                return Rect.Empty;

            switch (handleNumber)
            {
                case 1:
                    return HelperFunctions.CreateRectSafe(point.X, point.Y, source.Right, source.Bottom);
                case 3:
                    return HelperFunctions.CreateRectSafe(source.Left, point.Y, point.X, source.Bottom);
                case 5:
                    return HelperFunctions.CreateRectSafe(source.Left, source.Top, point.X, point.Y);
                case 7:
                    return HelperFunctions.CreateRectSafe(point.X, source.Top, source.Right, point.Y);
                default:
                    return Rect.Empty;
            }
        }

        private Rect TranslateDestAroundHandle(Rect source, Rect dest, int handleNumber)
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
                    return Rect.Empty;
            }
        }

        private Rect ScaleRectToAspect(Rect dest, double sourceAspect, bool keepWidth = true, bool keepHeight = true)
        {
            Rect destRect = new Rect();

            double destAspect = dest.Width / dest.Height;

            if (sourceAspect > destAspect)
            {
                // wider than high keep the width and scale the height
                destRect.Width = dest.Width;
                destRect.Height = dest.Width / sourceAspect;

                if (keepHeight)
                {
                    double resizePerc = dest.Height / destRect.Height;
                    destRect.Width = dest.Width * resizePerc;
                    destRect.Height = dest.Height;
                }
            }
            else
            {
                // higher than wide – keep the height and scale the width
                destRect.Height = dest.Height;
                destRect.Width = dest.Height * sourceAspect;

                if (keepWidth)
                {
                    double resizePerc = dest.Width / destRect.Width;
                    destRect.Width = dest.Width;
                    destRect.Height = dest.Height * resizePerc;
                }
            }

            return destRect;
        }
    }
}
