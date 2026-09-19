using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The tray's move affordance: a dot-grid handle the user drags and a rotate button, two equal
    /// cells under (horizontal) or beside (vertical) each other with the same hover veil. Neither has a
    /// fill of its own at rest — both sit straight on the tray, so the grip reads as part of the chassis
    /// rather than as another button.
    /// <para>
    /// The grip reports gestures and asks for a rotation; it never moves anything itself. Its owner
    /// owns the window and decides what a delta means, which is what keeps this control free of any
    /// knowledge of what the tray is for.
    /// </para>
    /// <para>
    /// Pointer handling lives in the four <c>OnPointer*</c> overrides rather than in template-part
    /// event wiring: a <see cref="TemplatedControl"/> has no class handler that marks a press handled,
    /// so presses on the dots and on the padding both reach us with no tunnel routing needed, while the
    /// rotate <see cref="Button"/> marks its own press handled and is therefore invisible to us — a
    /// rotate click can never also start a drag.
    /// </para>
    /// </summary>
    public class TrayGrip : TemplatedControl, ITrayOrientable
    {
        /// <summary>
        /// The axis of the strip this grip sits in, pushed down by the tray. Horizontal means the grip
        /// is a column (handle cell above the rotate cell); Vertical means it is a row.
        /// </summary>
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayGrip, Orientation>(nameof(Orientation), Orientation.Horizontal);

        /// <summary>
        /// True from <see cref="DragStarted"/> until the gesture ends (release or capture loss).
        /// Read-only: the theme uses it to hold the dots at their hover brightness for the whole gesture,
        /// including the stretches where the pointer has been dragged off the grip.
        /// </summary>
        public static readonly DirectProperty<TrayGrip, bool> IsDraggingProperty =
            AvaloniaProperty.RegisterDirect<TrayGrip, bool>(nameof(IsDragging), o => o.IsDragging);

        private TrayDragGesture _gesture;
        private bool _isDragging;
        private Button _rotate;

        static TrayGrip()
        {
            ControlThemes.EnsureRegistered();
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public bool IsDragging
        {
            get => _isDragging;
            private set => SetAndRaise(IsDraggingProperty, ref _isDragging, value);
        }

        /// <summary>
        /// True from a left press on the dots until the release or a capture loss — including the part of
        /// a press that never crosses the drag threshold. A plain CLR property: nothing binds to it, the
        /// owner simply reads it to hold its automatic placement while the grip is held, so that a posted
        /// re-placement cannot move the strip out from under a press that may still become a drag.
        /// </summary>
        public bool IsPressed { get; private set; }

        /// <summary>Raised once per gesture, the moment the movement threshold is crossed — never on the press.</summary>
        public event EventHandler DragStarted;

        /// <summary>
        /// The total screen-pixel offset from the point the gesture was pressed at, raised on every move
        /// while dragging. It is a total and not an increment, so a listener adds it to the position it
        /// recorded at <see cref="DragStarted"/> and never accumulates rounding error.
        /// </summary>
        public event EventHandler<PixelPoint> DragDelta;

        /// <summary>
        /// Raised when a press ends — by release or by capture loss — WITHOUT the movement threshold ever
        /// having been crossed, so no <see cref="DragStarted"/> was raised and nothing was moved. It is the
        /// moment an owner that held something back while the pointer was down can safely run it. A gesture
        /// that did become a drag does not raise it: its listener already knows how that one ended.
        /// </summary>
        public event EventHandler PressEnded;

        /// <summary>The rotate button was clicked. The owner flips the axis; the grip does not.</summary>
        public event EventHandler RotateRequested;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_rotate != null)
                _rotate.Click -= OnRotateClick;

            _rotate = e.NameScope.Find<Button>("PART_Rotate");

            if (_rotate != null)
                _rotate.Click += OnRotateClick;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(this);
            IsPressed = true;

            // the tip is advice about how to start an interaction, so it has no business appearing
            // during one — the hover timer would otherwise pop it up under the pointer mid-drag
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetServiceEnabled(this, false);

            // built per press so a monitor change between two gestures cannot leave a stale threshold
            _gesture = new TrayDragGesture(DragThresholdPx());
            _gesture.Press(this.PointToScreen(e.GetPosition(this)));

            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_gesture == null || !_gesture.IsPressed)
                return;

            var wasDragging = _gesture.IsDragging;
            if (_gesture.Move(this.PointToScreen(e.GetPosition(this)), out var delta))
            {
                if (!wasDragging)
                {
                    IsDragging = true;
                    DragStarted?.Invoke(this, EventArgs.Empty);
                }

                DragDelta?.Invoke(this, delta);
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_gesture == null || !_gesture.IsPressed)
                return;

            // Release() before releasing capture: Capture(null) raises PointerCaptureLost synchronously,
            // and the handler for it must find a gesture that is no longer pressed so it returns instead
            // of ending this gesture a second time.
            var wasDragging = _gesture.Release();
            e.Pointer.Capture(null);

            EndGesture(wasDragging);

            // a press and release under the threshold does nothing at all: the axis flips only from the
            // rotate button, never from a click on the dots
            e.Handled = true;
        }

        /// <summary>
        /// Losing capture without a release (the window hidden mid-drag, another window taking the
        /// pointer) never runs <see cref="OnPointerReleased"/>: without this the strip stays glued to
        /// the pointer for the rest of the run — <see cref="OnPointerMoved"/> only tests whether the
        /// gesture is pressed, not whether the button is still held — and the tooltip stays switched off.
        /// </summary>
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            if (_gesture == null || !_gesture.IsPressed)
                return;

            // Cancel() resets the gesture unconditionally and reports nothing, so the drag state has to
            // come from our own property (the gesture has already forgotten it by the time it returns).
            var wasDragging = IsDragging;

            _gesture.Cancel();
            EndGesture(wasDragging);
        }

        /// <param name="wasDragging">
        /// Whether the gesture that is ending had crossed the movement threshold — reported by
        /// <see cref="TrayDragGesture.Release"/> on a release, taken from <see cref="IsDragging"/> on a
        /// capture loss. It decides only whether <see cref="PressEnded"/> is raised.
        /// </param>
        private void EndGesture(bool wasDragging)
        {
            IsDragging = false;
            IsPressed = false;
            ToolTip.SetServiceEnabled(this, true);

            if (!wasDragging)
                PressEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 5 logical px, converted to the screen pixels the gesture is measured in. macOS reports screen
        /// coordinates in points, so there the two are the same unit and the scale is 1.
        /// </summary>
        private double DragThresholdPx()
        {
            var scaling = OperatingSystem.IsMacOS() ? 1.0 : (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
            return TrayTokens.DragThreshold * scaling;
        }

        private void OnRotateClick(object sender, RoutedEventArgs e)
        {
            RotateRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The dot grid itself, drawn rather than assembled: a 4×3 grid of 2 px dots on a 4.5 px pitch that
    /// becomes 3×4 when the strip stands up. Drawing it is a dozen lines against a dozen elements plus a
    /// panel, and it lets the grid report its exact 15.5 px extent instead of arriving at it by luck.
    /// </summary>
    internal sealed class TrayGripDots : Control
    {
        private const double Pitch = 4.5;
        private const double DotRadius = 1;
        private const int LongCount = 4;
        private const int ShortCount = 3;

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayGripDots, Orientation>(nameof(Orientation), Orientation.Horizontal);

        static TrayGripDots()
        {
            AffectsMeasure<TrayGripDots>(OrientationProperty);
            AffectsRender<TrayGripDots>(OrientationProperty);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private int Columns => Orientation == Orientation.Horizontal ? LongCount : ShortCount;

        private int Rows => Orientation == Orientation.Horizontal ? ShortCount : LongCount;

        /// <summary>4 dots → 15.5, 3 dots → 11: the pitch spans the gaps, the diameter caps both ends.</summary>
        private static double Extent(int count) => (count - 1) * Pitch + DotRadius * 2;

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(Extent(Columns), Extent(Rows));
        }

        public override void Render(DrawingContext context)
        {
            var columns = Columns;
            var rows = Rows;

            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    var centre = new Point(DotRadius + x * Pitch, DotRadius + y * Pitch);
                    context.DrawEllipse(TrayTokens.FgBrush, null, centre, DotRadius, DotRadius);
                }
            }
        }
    }
}
