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
            Move,        // object(s) are moved
            HandleDrag,  // object is edited via dragging a handle (e.g. resize, rotate)
            GroupSelection
        }

        private SelectionMode _selectMode = SelectionMode.None;

        // Object which is currently resized:
        private GraphicBase? _handleGrabbedObject;
        private int _handleGrabbed;
        private double _handleRatio;

        // Last position in canvas-local coordinates so we can compute deltas.
        // (The WPF original used screen coordinates so it stayed correct
        // through panning; we'll revisit in Phase 5 alongside viewport zoom.)
        private Point _lastPoint;

        private bool _wasEdit;

        public ToolPointer() : base(() => HelperFunctions.DefaultCursor)
        { }

        public GraphicBase? MakeHitTest(DrawingCanvas drawingCanvas, Point point, out int handleNumber)
        {
            var dpi = drawingCanvas.CanvasUiElementScale;
            var controls = drawingCanvas.GraphicsList.Select(gv => new
            {
                gv,
                gv.IsSelected,
                HitTest = gv.MakeHitTest(point, dpi)
            }).Reverse().ToArray();

            // Test if we start dragging a handle (only if control is selected and cursor is on the handle)
            var grabHandle = controls.FirstOrDefault(g => g.IsSelected && g.HitTest > 0);
            if (grabHandle != null)
            {
                handleNumber = grabHandle.HitTest;
                return grabHandle.gv;
            }

            // Test if we start dragging an object body
            var grabObject = controls.FirstOrDefault(g => g.HitTest == 0);
            if (grabObject != null)
            {
                handleNumber = 0;
                return grabObject.gv;
            }

            handleNumber = -1;
            return null;
        }

        public override void OnPointerPressed(DrawingCanvas drawingCanvas, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(drawingCanvas).Properties;
            if (!props.IsLeftButtonPressed)
                return;

            var pt = drawingCanvas.ToContentPoint(e.GetPosition(drawingCanvas));
            _lastPoint = pt;

            int handleNumber;
            var graphic = MakeHitTest(drawingCanvas, pt, out handleNumber);

            drawingCanvas.CaptureMouse(e.Pointer);

            // Unselect all other objects if dragging a handle, or starting a
            // selection rect / dragging an unselected object without Ctrl/Shift.
            var modifiers = e.KeyModifiers;
            if (handleNumber > 0 ||
                ((graphic == null || !graphic.IsSelected) &&
                 (modifiers & KeyModifiers.Control) == 0 &&
                 (modifiers & KeyModifiers.Shift) == 0))
            {
                drawingCanvas.UnselectAllExcept(graphic);
            }

            _wasEdit = false;

            if (graphic != null)
            {
                if (handleNumber > 0)
                {
                    _selectMode = SelectionMode.HandleDrag;
                    _handleGrabbedObject = graphic;
                    _handleGrabbed = handleNumber;

                    // initial aspect ratio (used when Shift held while resizing)
                    if (graphic is GraphicRectangle rotatableGraphic)
                        _handleRatio = rotatableGraphic.UnrotatedBounds.Height > 0
                            ? rotatableGraphic.UnrotatedBounds.Width / rotatableGraphic.UnrotatedBounds.Height
                            : 0;
                    else
                        _handleRatio = 0;
                }
                else
                {
                    _selectMode = SelectionMode.Move;
                    drawingCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
                }

                graphic.IsSelected = true;
            }
            else
            {
                // Click on empty background — start a rubber-band group selection.
                var rect = HelperFunctions.CreateRectSafe(pt.X, pt.Y, pt.X + 1, pt.Y + 1);
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
                drawingCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
            else if (graphic != null) // hit resize handle
                drawingCanvas.Cursor = graphic.GetHandleCursor(handleNumber);
        }

        public override void OnPointerMoved(DrawingCanvas drawingCanvas, PointerEventArgs e)
        {
            var currentProps = e.GetCurrentPoint(drawingCanvas).Properties;

            // Exclude all cases except left button on/off.
            if (currentProps.IsMiddleButtonPressed || currentProps.IsRightButtonPressed)
            {
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                return;
            }

            var pt = drawingCanvas.ToContentPoint(e.GetPosition(drawingCanvas));

            if (!currentProps.IsLeftButtonPressed)
            {
                SetHitTest(drawingCanvas, pt);
                return;
            }

            if (!drawingCanvas.IsMouseCaptured)
                return;

            _wasEdit = true;

            double dx = pt.X - _lastPoint.X;
            double dy = pt.Y - _lastPoint.Y;
            _lastPoint = pt;

            switch (_selectMode)
            {
                case SelectionMode.Move:
                    foreach (var o in drawingCanvas.GraphicsList.SelectedItems)
                        o.Move(dx, dy);
                    break;

                case SelectionMode.HandleDrag:
                    if (_handleGrabbedObject != null)
                    {
                        var shiftPressed = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                        var lineGraphic = _handleGrabbedObject as GraphicLine;
                        var rotatableGraphic = _handleGrabbedObject as GraphicRectangle;

                        if (shiftPressed && rotatableGraphic != null && _handleRatio != 0)
                        {
                            // aspect-ratio-locked resize
                            var unrotatedPt = rotatableGraphic.UnapplyRotation(pt);
                            var destRect = GetTransformedRect(rotatableGraphic.UnrotatedBounds, _handleGrabbed, unrotatedPt);
                            if (destRect.Width > 0 && destRect.Height > 0)
                            {
                                destRect = ScaleRectToAspect(destRect, _handleRatio);
                                destRect = TranslateDestAroundHandle(rotatableGraphic.UnrotatedBounds, destRect, _handleGrabbed);
                                rotatableGraphic.Left = destRect.Left;
                                rotatableGraphic.Top = destRect.Top;
                                rotatableGraphic.Right = destRect.Right;
                                rotatableGraphic.Bottom = destRect.Bottom;
                            }
                        }
                        else if (shiftPressed && lineGraphic != null)
                        {
                            var anchor = _handleGrabbed == 1 ? lineGraphic.LineEnd : lineGraphic.LineStart;
                            var snapped = HelperFunctions.SnapPointToCommonAngle(anchor, pt, false);
                            _handleGrabbedObject.MoveHandleTo(snapped, _handleGrabbed);
                        }
                        else
                        {
                            _handleGrabbedObject.MoveHandleTo(pt, _handleGrabbed);
                        }
                        drawingCanvas.Cursor = _handleGrabbedObject.GetHandleCursor(_handleGrabbed);
                    }
                    break;

                case SelectionMode.GroupSelection:
                    // Resize the rubber-band rectangle (handle 5 = bottom-right corner).
                    if (drawingCanvas.Count > 0)
                        drawingCanvas[drawingCanvas.Count - 1].MoveHandleTo(pt, 5);
                    break;
            }
        }

        public override void OnPointerReleased(DrawingCanvas drawingCanvas, PointerReleasedEventArgs e)
        {
            if (!drawingCanvas.IsMouseCaptured)
            {
                drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
                _selectMode = SelectionMode.None;
                return;
            }

            if (_handleGrabbedObject != null)
            {
                _handleGrabbedObject.Normalize();
                _handleGrabbedObject = null;
            }

            if (_selectMode == SelectionMode.GroupSelection && drawingCanvas.Count > 0)
            {
                var rubber = drawingCanvas[drawingCanvas.Count - 1] as GraphicSelectionRectangle;
                if (rubber != null)
                {
                    rubber.Normalize();
                    var rect = rubber.Bounds;
                    drawingCanvas.GraphicsList.Remove(rubber);

                    foreach (var g in drawingCanvas.GraphicsList)
                    {
                        if (rect.Contains(g.Bounds))
                            g.IsSelected = true;
                    }
                }
            }

            drawingCanvas.ReleaseMouseCapture();

            var pt = drawingCanvas.ToContentPoint(e.GetPosition(drawingCanvas));
            SetHitTest(drawingCanvas, pt);

            _selectMode = SelectionMode.None;
            if (_wasEdit)
                drawingCanvas.AddCommandToHistory(false);
        }

        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
        }

        private Rect GetTransformedRect(Rect source, int handleNumber, Point point)
        {
            if (source.Width <= 0 || source.Height <= 0)
                return default;

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
                    return default;
            }
        }

        private Rect TranslateDestAroundHandle(Rect source, Rect dest, int handleNumber)
        {
            switch (handleNumber)
            {
                case 5:
                    return new Rect(source.X, source.Y, dest.Width, dest.Height);
                case 7:
                    return new Rect(source.Right - dest.Width, source.Y, dest.Width, dest.Height);
                case 1:
                    return new Rect(source.Right - dest.Width, source.Bottom - dest.Height, dest.Width, dest.Height);
                case 3:
                    return new Rect(source.X, source.Bottom - dest.Height, dest.Width, dest.Height);
                default:
                    return default;
            }
        }

        private Rect ScaleRectToAspect(Rect dest, double sourceAspect)
        {
            if (dest.Height <= 0)
                return dest;

            double destAspect = dest.Width / dest.Height;
            double w, h;

            if (sourceAspect > destAspect)
            {
                // wider than high — keep the height, scale the width
                h = dest.Height;
                w = dest.Height * sourceAspect;
            }
            else
            {
                // taller than wide — keep the width, scale the height
                w = dest.Width;
                h = dest.Width / sourceAspect;
            }

            return new Rect(dest.X, dest.Y, w, h);
        }
    }
}
