using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;

namespace Clowd.UI.Helpers
{
    /// <summary>
    /// Dims a window while a modal dialog it owns is open, the way a sheet dims the document
    /// behind it. Installed once for the whole app: every <see cref="Window.ShowDialog{TResult}"/>
    /// in Clowd is covered without touching a single call site, because the hook is a class
    /// handler on <see cref="Visual.IsVisibleProperty"/> rather than anything the caller does.
    ///
    /// The veil is a hit-test-invisible black <see cref="Border"/> in the owner's
    /// <see cref="OverlayLayer"/>, so it covers the whole client area without disturbing the
    /// window's own layout, and it costs nothing when no dialog is open. Note that this is the
    /// CLIENT area: on Windows the system titlebar keeps its normal colour, while a window that
    /// extends into its titlebar (macOS) dims to the top edge.
    ///
    /// Native pickers (file/folder) are OS windows rather than Avalonia ones, so they do not dim;
    /// there is no window of ours to notice them being shown.
    /// </summary>
    internal static class ModalDim
    {
        /// <summary>How black the veil goes. Dark enough to read as "this is not what you're
        /// talking to", light enough that the content behind stays legible.</summary>
        private const double DimOpacity = 0.45;

        /// <summary>Long enough to read as a fade rather than a flash, short enough that a dialog
        /// which opens immediately does not appear to lag behind it.</summary>
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

        /// <summary>Set on a window that must never be dimmed (an overlay that is already its own
        /// scrim, say). Read off the OWNER, not the dialog.</summary>
        public static readonly AttachedProperty<bool> IsExemptProperty =
            AvaloniaProperty.RegisterAttached<Window, bool>("IsExempt", typeof(ModalDim));

        public static void SetIsExempt(Window window, bool value) => window.SetValue(IsExemptProperty, value);

        public static bool GetIsExempt(Window window) => window.GetValue(IsExemptProperty);

        private sealed class DimState
        {
            public int Count;
            public Border Veil;
            public IDisposable BoundsSubscription;
            public EventHandler OwnerClosed;
        }

        /// <summary>Owners currently veiled, with how many dialogs are stacked on each — a dialog
        /// that opens a dialog of its own must not un-dim the window when only the inner one
        /// closes.</summary>
        private static readonly Dictionary<Window, DimState> _states = new();

        /// <summary>Which owner each open dialog dimmed. Remembered rather than re-read on close,
        /// because a closing window has already let go of its owner by the time we hear about it.</summary>
        private static readonly Dictionary<Window, Window> _dimmedBy = new();

        private static bool _installed;

        /// <summary>Call once at startup. Idempotent.</summary>
        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((window, e) =>
            {
                if (e.GetNewValue<bool>())
                {
                    // Owner and IsDialog are handed to the window by ShowDialog around the moment
                    // it becomes visible, and nothing promises they are in place first. Posting
                    // asks the question once that setup has certainly finished.
                    Dispatcher.UIThread.Post(() => OnWindowShown(window));
                }
                else
                {
                    Release(window);
                }
            });
        }

        private static void OnWindowShown(Window dialog)
        {
            if (_dimmedBy.ContainsKey(dialog))
                return;

            if (!dialog.IsVisible || !dialog.IsDialog)
                return;

            if (dialog.Owner is not Window owner || GetIsExempt(owner))
                return;

            _dimmedBy[dialog] = owner;
            dialog.Closed += OnDialogClosed;
            Push(owner);
        }

        private static void OnDialogClosed(object sender, EventArgs e)
        {
            if (sender is Window dialog)
                Release(dialog);
        }

        /// <summary>Un-dims whatever <paramref name="dialog"/> dimmed, if anything. Safe to call
        /// more than once, and for windows that never dimmed at all — both hide and close arrive.</summary>
        private static void Release(Window dialog)
        {
            if (!_dimmedBy.TryGetValue(dialog, out var owner))
                return;

            _dimmedBy.Remove(dialog);
            dialog.Closed -= OnDialogClosed;
            Pop(owner);
        }

        private static void Push(Window owner)
        {
            if (!_states.TryGetValue(owner, out var state))
            {
                state = new DimState();
                _states[owner] = state;

                // An owner that is closed out from under its dialogs (app shutdown) still has to
                // let go of the veil, or the window lives on in the table for good.
                state.OwnerClosed = (_, _) => Forget(owner);
                owner.Closed += state.OwnerClosed;
            }

            if (++state.Count == 1)
                Show(owner, state);
        }

        private static void Pop(Window owner)
        {
            if (!_states.TryGetValue(owner, out var state))
                return;

            if (--state.Count > 0)
                return;

            state.Count = 0;
            Hide(owner, state);
        }

        private static void Show(Window owner, DimState state)
        {
            // Already veiled — a dialog closed and another opened before the fade finished, so the
            // veil we are about to fade back in is the one still on screen.
            if (state.Veil != null)
            {
                state.Veil.Opacity = DimOpacity;
                return;
            }

            var layer = OverlayLayer.GetOverlayLayer(owner);
            if (layer == null)
                return; // owner not laid out yet; nothing to veil, and the count still balances

            var veil = new Border
            {
                Background = Brushes.Black,
                Opacity = 0,
                IsHitTestVisible = false,
                Width = layer.Bounds.Width,
                Height = layer.Bounds.Height,
                Transitions = new Transitions
                {
                    // One transition serves both directions. Eased out rather than linear: the
                    // veil arrives quickly and settles, which reads as the dialog casting it
                    // rather than as a rectangle being animated.
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = FadeDuration,
                        Easing = new CubicEaseOut(),
                    },
                },
            };

            // The overlay layer is a canvas: a child of it is whatever size it says it is, so the
            // veil tracks the window rather than assuming the size it had when it was made.
            state.BoundsSubscription = layer.GetObservable(Visual.BoundsProperty).Subscribe(
                new AnonymousObserver<Rect>(bounds =>
                {
                    veil.Width = bounds.Width;
                    veil.Height = bounds.Height;
                }));

            state.Veil = veil;
            layer.Children.Add(veil);

            // The transition only runs on a change made after the veil is in the tree; setting the
            // target opacity in the same breath as Opacity = 0 would simply start it black.
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(state.Veil, veil) && state.Count > 0)
                    veil.Opacity = DimOpacity;
            }, DispatcherPriority.Render);
        }

        private static void Hide(Window owner, DimState state)
        {
            var veil = state.Veil;
            if (veil == null)
            {
                Forget(owner);
                return;
            }

            veil.Opacity = 0;

            // Kept in the tree until the fade has played out, and only dropped if no new dialog
            // claimed it in the meantime.
            DispatcherTimer.RunOnce(() =>
            {
                if (!ReferenceEquals(state.Veil, veil) || state.Count > 0)
                    return;

                Forget(owner);
            }, FadeDuration);
        }

        /// <summary>Drops the veil and every subscription held for <paramref name="owner"/>.</summary>
        private static void Forget(Window owner)
        {
            if (!_states.TryGetValue(owner, out var state))
                return;

            _states.Remove(owner);

            state.BoundsSubscription?.Dispose();

            if (state.Veil != null)
                OverlayLayer.GetOverlayLayer(owner)?.Children.Remove(state.Veil);

            if (state.OwnerClosed != null)
                owner.Closed -= state.OwnerClosed;
        }
    }
}
