using System;
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
        bool _wasMove;


        public ToolPointer()
        {
        }

        /// <summary>
        /// Handle mouse down.
        /// Start moving, resizing or group selection.
        /// </summary>
        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            _commandChangeState = null;
            _wasMove = false;
            _selectMode = SelectionMode.None;

            Point point = e.GetPosition(drawingCanvas);
            GraphicsBase movedObject = null;

            // Test if we start dragging an object or a handle (e.g. resize, rotate, etc.; only if control is selected and cursor is on the handle)
            for (int i = drawingCanvas.GraphicsList.Count - 1; i >= 0; i--)
            {
                var o = drawingCanvas[i].Graphic;
                int handleNumber;

                // Test for handles
                if (o.IsSelected && (handleNumber = o.MakeHitTest(point)) > 0)
                {
                    _selectMode = SelectionMode.HandleDrag;
                    _handleGrabbedObject = o;
                    _handleGrabbed = handleNumber;

                    // Since we want to edit only one object, unselect all other objects
                    drawingCanvas.UnselectAll();
                    o.IsSelected = true;

                    _commandChangeState = new CommandChangeState(drawingCanvas);

                    // Since handles take precedence over everything, we do not need to check the rest of the objects
                    break;
                }

                // Test for dragging an object, but only if we haven’t already found an object to drag
                else if (o.MakeHitTest(point) == 0 && _selectMode != SelectionMode.Move)
                {
                    movedObject = o;
                    _selectMode = SelectionMode.Move;

                    // Unselect all if Ctrl is not pressed and clicked object is not selected yet
                    if (Keyboard.Modifiers != ModifierKeys.Control && !movedObject.IsSelected)
                    {
                        drawingCanvas.UnselectAll();
                    }

                    // Select clicked object
                    movedObject.IsSelected = true;

                    // Set move cursor
                    drawingCanvas.Cursor = Cursors.SizeAll;

                    _commandChangeState = new CommandChangeState(drawingCanvas);
                }
            }

            // Click on background
            if (_selectMode == SelectionMode.None)
            {
                // Unselect all if Ctrl is not pressed
                if (Keyboard.Modifiers != ModifierKeys.Control)
                {
                    drawingCanvas.UnselectAll();
                }

                // Group selection. Create selection rectangle.
                var rect = HelperFunctions.CreateRectSafe(point.X, point.Y, point.X + 1, point.Y + 1);
                GraphicsSelectionRectangle r = new GraphicsSelectionRectangle(drawingCanvas, rect);

                drawingCanvas.GraphicsList.Add(r.CreateVisual());
                _selectMode = SelectionMode.GroupSelection;
            }

            _lastPoint = point;

            // Capture mouse until MouseUp event is received
            drawingCanvas.CaptureMouse();
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
                Cursor cursor = null;

                for (int i = drawingCanvas.Count - 1; i >= 0; i--)
                {
                    int n = drawingCanvas[i].Graphic.MakeHitTest(point);

                    if (n == 0)
                    {
                        cursor = Cursors.Hand;
                        break;
                    }
                    else if (n > 0)
                    {
                        cursor = drawingCanvas[i].Graphic.GetHandleCursor(n);
                        break;
                    }
                }

                if (cursor == null)
                    cursor = HelperFunctions.DefaultCursor;

                drawingCanvas.Cursor = cursor;

                return;

            }

            if (!drawingCanvas.IsMouseCaptured)
            {
                return;
            }

            _wasMove = true;

            // Find difference between previous and current position
            double dx = point.X - _lastPoint.X;
            double dy = point.Y - _lastPoint.Y;

            _lastPoint = point;

            // Resize or rotate
            if (_selectMode == SelectionMode.HandleDrag)
            {
                if (_handleGrabbedObject != null)
                {
                    _handleGrabbedObject.MoveHandleTo(point, _handleGrabbed);
                }
            }

            // Move
            if (_selectMode == SelectionMode.Move)
            {
                foreach (GraphicsVisual o in drawingCanvas.Selection)
                {
                    o.Graphic.Move(dx, dy);
                }
            }

            // Group selection
            if (_selectMode == SelectionMode.GroupSelection)
            {
                // Resize selection rectangle
                drawingCanvas[drawingCanvas.Count - 1].Graphic.MoveHandleTo(point, 5);
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
                // after resizing
                _handleGrabbedObject.Normalize();

                // Special case for text
                //if (resizedObject is GraphicsText)
                //{
                //    ((GraphicsText)resizedObject).
                //}

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
            if (_commandChangeState != null && _wasMove)
            {
                // Keep state after moving/resizing and add command to history
                _commandChangeState.NewState(drawingCanvas);
                drawingCanvas.AddCommandToHistory(_commandChangeState);
                _commandChangeState = null;
            }
        }

    }
}
