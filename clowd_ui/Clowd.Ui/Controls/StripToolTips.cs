using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Tooltips for a strip of buttons: the floating tray (FloatingTrayWindow) and the editors' "tipBridge"
    /// bars (AppResources.axaml).
    /// <para>
    /// A strip with <see cref="IsEnabledProperty"/> set shows its buttons' tips itself, in ONE popup that
    /// stays open while the pointer moves from button to button: the popup is re-aimed at the next button
    /// and its content swapped. Avalonia's ToolTipService gives every owner its own popup, so moving between
    /// two buttons destroys one top-level window and creates another, and for a frame or two neither is on
    /// screen — a flicker no amount of gap-bridging fixes. The service's own tips are therefore cancelled
    /// (ToolTipOpening) for every control in the strip's visual tree — not switched off with the inherited
    /// <c>ToolTip.ServiceEnabled</c>, which would also reach the menus a strip button opens, through their
    /// logical parent — and this reads the same attached properties the service would have:
    /// <c>ToolTip.Tip</c>, the placement, offsets and callback, <c>ShowDelay</c>, <c>BetweenShowDelay</c>,
    /// <c>ShowOnDisabled</c> and <c>ServiceEnabled</c> (a grip turns its tip off for a drag). A tip that is a <see cref="ToolTip"/> instance (the
    /// editors' rich cards) is hosted as-is, with its own theme; anything else is wrapped in a shared ToolTip
    /// wearing the tray's chip (TrayToolTipTheme).
    /// </para>
    /// <para>
    /// Over the strip's gaps (the strip itself, or anything on it without a tip) the open tip stays where it
    /// is while the pointer is within a short distance of its button, and it closes past that, when the
    /// pointer leaves the strip, or when it presses a button. Nothing opens while a button
    /// is held or the pointer is captured, so a drag never raises a tip under the pointer.
    /// </para>
    /// </summary>
    public static class StripToolTips
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(StripToolTips));

        private static readonly AttachedProperty<Host> HostProperty =
            AvaloniaProperty.RegisterAttached<Control, Host>("Host", typeof(StripToolTips));

        /// <summary>
        /// The callback the "tipBridge" style hands every button on a bar: the strip is the nearest ancestor
        /// with <see cref="IsEnabledProperty"/> set, and its axis is its longer side, since an editor bar is a
        /// full-length row or column.
        /// </summary>
        public static readonly CustomPopupPlacementCallback Placement = PlaceOnStrip;

        static StripToolTips()
        {
            IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
            {
                control.GetValue(HostProperty)?.Dispose();
                control.ClearValue(HostProperty);
                if (e.GetNewValue<bool>())
                    control.SetValue(HostProperty, new Host(control));
            });

            ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, e) =>
            {
                if (IsOnStrip(control))
                    e.Cancel = true;
            });
        }

        public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

        private static bool IsOnStrip(Visual visual)
        {
            for (var v = visual; v != null; v = v.GetVisualParent())
            {
                if (v is Control c && GetIsEnabled(c))
                    return true;
            }
            return false;
        }

        private static void PlaceOnStrip(CustomPopupPlacement placement)
        {
            var strip = (placement.Target as Visual)?.FindAncestorOfType<Control>();
            while (strip != null && !GetIsEnabled(strip))
                strip = strip.FindAncestorOfType<Control>();

            var horizontal = strip == null || strip.Bounds.Width >= strip.Bounds.Height;
            Place(placement, strip, horizontal);
        }

        /// <summary>
        /// Below a horizontal strip or right of a vertical one, centred on the control, its anchor the
        /// control's span along the strip by the strip's full depth across it. Flipped to the other side
        /// when that one has no room, slid along the strip to stay on screen.
        /// </summary>
        /// <remarks>
        /// Anchored to the control alone, a tip on a multi-lane strip opened over the next lane, and one
        /// the positioner flipped (no room below a strip at the bottom of the screen) opened over the lane
        /// above it; stretched across the strip, a flip moves the tip to the strip's other side, still
        /// clear of every button. The gap between strip and tip is the tip theme's margin
        /// (TrayTokens.TipMargin), not an offset here: an offset is applied in the same direction after a
        /// flip, which would push a flipped tip into the strip.
        /// </remarks>
        public static void Place(CustomPopupPlacement placement, Visual strip, bool horizontal)
        {
            if (placement.Target is not Visual target)
                return;

            // the positioner hands over the control's own rect already in the top level's
            // coordinates, which is the space the anchor is written back in
            var own = placement.AnchorRectangle;

            // the strip body, translated to the control and then moved with it into that space;
            // without a strip (or before layout) the control alone is the anchor
            var across = strip?.TranslatePoint(default, target) is { } origin
                ? new Rect(origin + own.Position, strip.Bounds.Size)
                : own;

            placement.AnchorRectangle = horizontal
                ? new Rect(own.X, across.Y, own.Width, across.Height)
                : new Rect(across.X, own.Y, across.Width, own.Height);
            placement.Anchor = horizontal ? PopupAnchor.Bottom : PopupAnchor.Right;
            placement.Gravity = horizontal ? PopupGravity.Bottom : PopupGravity.Right;
            placement.ConstraintAdjustment = horizontal
                ? PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX
                : PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.SlideY;
            placement.Offset = default;
        }

        /// <summary>One strip's tip: the popup, the owner it is aimed at, and the hover timer.</summary>
        private sealed class Host : IDisposable
        {
            /// <summary>
            /// How far off its owner (logical px) a tip survives over a gap: enough to cross from one
            /// button to the next (a tray divider is 4 + 1 + 4), short enough that a pointer resting on an
            /// empty stretch of the strip is not still showing the last button's tip. A gap wider than
            /// this (an editor bar's group break) closes the tip on the way across.
            /// </summary>
            private const double MaxBridge = 10;

            private readonly Control _strip;
            private readonly DispatcherTimer _timer = new();
            private Popup _popup;

            // the popup's one child, never replaced: Popup hands its Child to the popup window only
            // when it opens, so swapping Child on an open popup changed nothing on screen. The tips
            // are swapped inside this instead, which is an ordinary live visual tree.
            private Decorator _frame;
            private ToolTip _chip;

            // the button the tip belongs to (open, or waiting on the timer)
            private Control _owner;

            // the button that was pressed: its tip stays shut until the pointer reaches another one,
            // as ToolTipService does
            private Control _pressed;
            private long _lastCloseTicks;

            public Host(Control strip)
            {
                _strip = strip;

                _timer.Tick += (_, _) =>
                {
                    _timer.Stop();
                    if (_owner != null)
                        Show(_owner);
                };

                strip.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
                strip.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
                strip.AddHandler(InputElement.PointerExitedEvent, OnPointerExited, RoutingStrategies.Direct, handledEventsToo: true);
                strip.DetachedFromVisualTree += OnDetached;
            }

            public void Dispose()
            {
                Close();
                _strip.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
                _strip.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                _strip.RemoveHandler(InputElement.PointerExitedEvent, OnPointerExited);
                _strip.DetachedFromVisualTree -= OnDetached;
            }

            private void OnDetached(object sender, VisualTreeAttachmentEventArgs e) => Close();

            private void OnPointerExited(object sender, PointerEventArgs e)
            {
                _pressed = null;
                Close();
            }

            private void OnPointerPressed(object sender, PointerPressedEventArgs e)
            {
                _pressed = _owner;
                Close();
            }

            private void OnPointerMoved(object sender, PointerEventArgs e)
            {
                if (e.Pointer.Captured != null || e.GetCurrentPoint(_strip).Properties.IsLeftButtonPressed
                    || e.GetCurrentPoint(_strip).Properties.IsRightButtonPressed)
                    return;

                var point = e.GetPosition(_strip);
                var owner = FindOwner(point);

                // a gap, or something on the strip without a tip: whatever is up (or pending) stays
                // while the pointer is still near its owner, i.e. on its way to the next button
                if (owner == null)
                {
                    if (_owner != null && DistanceFromOwner(point) > MaxBridge)
                        Close();
                    return;
                }

                if (owner != _pressed)
                    _pressed = null;
                if (owner == _pressed)
                    return;

                if (owner == _owner)
                {
                    // the owner rewrote its tip while it was up (a state-dependent tile)
                    if (_popup?.IsOpen == true)
                        Fill(owner);
                    return;
                }

                _owner = owner;
                _timer.Stop();

                var betweenTicks = TimeSpan.FromMilliseconds(Math.Max(0, ToolTip.GetBetweenShowDelay(owner))).Ticks;
                if (_popup?.IsOpen == true || DateTime.UtcNow.Ticks - _lastCloseTicks <= betweenTicks)
                {
                    Show(owner);
                }
                else
                {
                    _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolTip.GetShowDelay(owner)));
                    _timer.Start();
                }
            }

            /// <summary>How far from the current owner's edge the pointer is, in strip coordinates.</summary>
            private double DistanceFromOwner(Point point)
            {
                if (_owner.TranslatePoint(default, _strip) is not { } origin)
                    return double.PositiveInfinity;

                var r = new Rect(origin, _owner.Bounds.Size);
                var dx = Math.Max(0, Math.Max(r.Left - point.X, point.X - r.Right));
                var dy = Math.Max(0, Math.Max(r.Top - point.Y, point.Y - r.Bottom));
                return Math.Sqrt(dx * dx + dy * dy);
            }

            /// <summary>
            /// The control on the strip whose tip the pointer is over. Hit-tested here rather than taken
            /// from the event's source, because a pointer event goes to the first ENABLED element, and a
            /// disabled tile's tip (ShowOnDisabled) is often the only explanation of why it is disabled.
            /// </summary>
            private Control FindOwner(Point point)
            {
                var hit = _strip.InputHitTest(point, enabledElementsOnly: false) as Visual;
                for (var v = hit; v != null && v != _strip; v = v.GetVisualParent())
                {
                    if (v is not Control c)
                        continue;
                    if (!ToolTip.GetServiceEnabled(c))
                        return null;
                    if (ToolTip.GetTip(c) != null && (c.IsEffectivelyEnabled || ToolTip.GetShowOnDisabled(c)))
                        return c;
                }
                return null;
            }

            private void Show(Control owner)
            {
                if (!owner.IsAttachedToVisualTree() || ToolTip.GetTip(owner) == null)
                {
                    Close();
                    return;
                }

                if (_popup == null)
                {
                    _popup = new Popup
                    {
                        IsHitTestVisible = false,
                        IsLightDismissEnabled = false,
                        TakesFocusFromNativeControl = false,
                        WindowManagerAddShadowHint = false,
                        Child = _frame = new Decorator(),
                    };
                    // the strip as logical parent, so the tip resolves the strip window's styles and
                    // theme variant (Avalonia parents a tip's popup to its owner for the same reason);
                    // it never changes, since leaving the logical tree closes a popup
                    ((ISetLogicalParent)_popup).SetParent(_strip);
                }

                Fill(owner);

                // aim before the target: setting each of these on an open popup re-runs the positioner,
                // and the target last means the final run sees all of them
                _popup.Placement = ToolTip.GetPlacement(owner);
                _popup.CustomPopupPlacementCallback = ToolTip.GetCustomPopupPlacementCallback(owner);
                _popup.HorizontalOffset = ToolTip.GetHorizontalOffset(owner);
                _popup.VerticalOffset = ToolTip.GetVerticalOffset(owner);
                _popup.PlacementTarget = owner;
                _popup.IsOpen = true;
            }

            /// <summary>Puts the owner's tip in the popup: its own ToolTip if it has one, else the chip.</summary>
            private void Fill(Control owner)
            {
                var tip = ToolTip.GetTip(owner);
                if (tip is ToolTip own)
                {
                    if (_frame.Child != own)
                        _frame.Child = own;
                    return;
                }

                if (_chip == null)
                {
                    _chip = new ToolTip();
                    if (_strip.TryFindResource("TrayToolTipTheme", out var theme) && theme is ControlTheme chipTheme)
                        _chip.Theme = chipTheme;
                }

                if (_chip.Content != tip)
                    _chip.Content = tip;
                if (_frame.Child != _chip)
                    _frame.Child = _chip;
            }

            private void Close()
            {
                _timer.Stop();
                _owner = null;
                if (_popup?.IsOpen == true)
                {
                    _popup.IsOpen = false;
                    _lastCloseTicks = DateTime.UtcNow.Ticks;
                }

                // a rich card's demo decodes only while attached, so let it go with the popup
                if (_frame != null)
                    _frame.Child = null;
            }
        }
    }
}
