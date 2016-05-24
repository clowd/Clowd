using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    /// <summary>
    /// Pointer tool
    /// </summary>
    class ToolPointer : Tool
    {
        private enum SelectionMode
        {
            None,
            Move,               // object(s) are moved
            HandleDrag,         // object is edited via dragging a handle (e.g. resize, rotate)
            GroupSelection
        }

        private SelectionMode _selectMode = SelectionMode.None;

        // Object which is currently resized:
        private GraphicsBase _handleGrabbedObject;
        private int _handleGrabbed;

        // Keep state about last and current point (used to edit objects via dragging, e.g. move and resize)
        private Point _lastPoint = new Point(0, 0);

        private CommandChangeState _commandChangeState;
        bool _wasEdit;


        public ToolPointer()
        {
        }

        private GraphicsBase MakeHitTest(DrawingCanvas drawingCanvas, Point point, out int handleNumber)
        {
            var controls = drawingCanvas.GraphicsList.Cast<GraphicsVisual>().Select(gv => new { gv.Graphic, gv.Graphic.IsSelected, HitTest = gv.Graphic.MakeHitTest(point) }).Reverse().ToArray();

            // Test if we start dragging a handle (e.g. resize, rotate, etc.; only if control is selected and cursor is on the handle)
            var grabHandle = controls.FirstOrDefault(g => g.IsSelected && g.HitTest > 0);
            if (grabHandle != null)
            {
                handleNumber = grabHandle.HitTest;
                return grabHandle.Graphic;
            }

            // Test if we start dragging an object 
            var grabObject = controls.FirstOrDefault(g => g.HitTest == 0);
            if (grabObject != null)
            {
                handleNumber = 0;
                return grabObject.Graphic;
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
                drawingCanvas.UnselectAll();
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
                }
                else
                {
                    _selectMode = SelectionMode.Move;
                    drawingCanvas.Cursor = Cursors.SizeAll;
                }

                graphic.IsSelected = true;
                _commandChangeState = new CommandChangeState(drawingCanvas);
            }
            else
            {
                // Click on background — start a selection rectangle for group selection.
                var rect = HelperFunctions.CreateRectSafe(_lastPoint.X, _lastPoint.Y, _lastPoint.X + 1, _lastPoint.Y + 1);
                var gsr = new GraphicsSelectionRectangle(drawingCanvas, rect);
                drawingCanvas.GraphicsList.Add(gsr.CreateVisual());

                _selectMode = SelectionMode.GroupSelection;
                _commandChangeState = null;
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
            {
                return;
            }

            _wasEdit = true;

            // Find difference between previous and current position
            double dx = point.X - _lastPoint.X;
            double dy = point.Y - _lastPoint.Y;

            _lastPoint = point;

            switch (_selectMode)
            {
                case SelectionMode.Move:
                    foreach (GraphicsVisual o in drawingCanvas.Selection)
                        o.Graphic.Move(dx, dy);
                    break;

                case SelectionMode.HandleDrag:
                    if (_handleGrabbedObject != null)
                    {
                        _handleGrabbedObject.MoveHandleTo(point, _handleGrabbed);
                        drawingCanvas.Cursor = _handleGrabbedObject.GetHandleCursor(_handleGrabbed);
                    }
                    break;

                case SelectionMode.GroupSelection:
                    // Resize selection rectangle
                    drawingCanvas[drawingCanvas.Count - 1].Graphic.MoveHandleTo(point, 5);
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
                GraphicsSelectionRectangle r = (GraphicsSelectionRectangle)(drawingCanvas[drawingCanvas.Count - 1].Graphic);
                r.Normalize();
                Rect rect = r.Bounds;

                drawingCanvas.GraphicsList.Remove(drawingCanvas[drawingCanvas.Count - 1]);

                foreach (GraphicsVisual g in drawingCanvas.GraphicsList)
                {
                    if (g.Graphic.ContainedIn(rect))
                    {
                        g.IsSelected = true;
                    }
                }
            }

            drawingCanvas.ReleaseMouseCapture();

            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;

            _selectMode = SelectionMode.None;

            AddChangeToHistory(drawingCanvas);
        }

        /// <summary>
        /// Set cursor
        /// </summary>
        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
        }


        /// <summary>
        /// Add change to history.
        /// Called after finishing moving/resizing.
        /// </summary>
        public void AddChangeToHistory(DrawingCanvas drawingCanvas)
        {
            if (_commandChangeState != null && _wasEdit)
            {
                // Keep state after moving/resizing and add command to history
                _commandChangeState.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(_commandChangeState);
                _commandChangeState = null;
            }
        }

    }
}
