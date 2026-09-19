using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The graphite panel every floating strip is built on: a rounded, ringed, shadowed row (or column)
    /// of controls with a fixed 4 px gap between them. It is an <see cref="ItemsControl"/> whose items
    /// are plain controls — each item is its own container, so <c>Items.Add(new TrayButton())</c> is
    /// the whole API — and its axis is one property that the owning window is the single writer of.
    /// <para>
    /// Rotation never rebuilds anything. The panel flips its own <c>StackPanel</c> through a template
    /// binding, and the value is pushed into every item that implements <see cref="ITrayOrientable"/>
    /// so each of those restyles itself through its own theme selectors. Items that do not implement
    /// it need no help: a vertical column stretches them to the widest child, a horizontal row centres
    /// them at their own 32 px height.
    /// </para>
    /// </summary>
    public class FloatingTray : ItemsControl
    {
        /// <summary>The strip's axis. Written by the owning window only (placement or a rotate click).</summary>
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<FloatingTray, Orientation>(nameof(Orientation), Orientation.Horizontal);

        /// <summary>
        /// The drop shadow under the panel. Defaults to the compact shadow because the window must
        /// reserve transparent room around the tray for it, and every transparent pixel of the window
        /// eats a click meant for whatever is beneath the strip (see <see cref="TrayTokens.ShadowCompact"/>).
        /// </summary>
        public static readonly StyledProperty<BoxShadows> ShadowProperty =
            AvaloniaProperty.Register<FloatingTray, BoxShadows>(nameof(Shadow), TrayTokens.ShadowCompact);

        static FloatingTray()
        {
            ControlThemes.EnsureRegistered();
            OrientationProperty.Changed.AddClassHandler<FloatingTray>((tray, _) => tray.PushOrientation());
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public BoxShadows Shadow
        {
            get => GetValue(ShadowProperty);
            set => SetValue(ShadowProperty, value);
        }

        /// <summary>
        /// Items added after a rotation must not come up on the wrong axis, so every container the
        /// panel prepares — which, with control items, is the item itself — receives the current
        /// orientation the moment it joins the strip.
        /// </summary>
        protected override void PrepareContainerForItemOverride(Control container, object item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);

            if (container is ITrayOrientable orientable)
                orientable.Orientation = Orientation;
        }

        /// <summary>One explicit loop over at most a handful of items: no inherited attached
        /// property, no per-item subscriptions, nothing to unsubscribe.</summary>
        private void PushOrientation()
        {
            var orientation = Orientation;
            foreach (var item in Items)
            {
                if (item is ITrayOrientable orientable)
                    orientable.Orientation = orientation;
            }
        }
    }
}
