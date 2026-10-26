using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Prisc.UI.Controls.Tray
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
    /// them at their own 40 px height.
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

        /// <summary>
        /// Whether the strip is engaged — its hotkeys are live. Shown by the engaged frame: the grip edge
        /// becomes a solid accent spine, an accent ring runs round the strip, and an accent glow replaces
        /// the drop shadow, all fading in together (the theme's <c>[IsEngaged=True]</c> styles). The glow
        /// needs room on every side, so only a window built with <see cref="FloatingTrayOptions.EngagedFrame"/>
        /// sets it.
        /// </summary>
        public static readonly StyledProperty<bool> IsEngagedProperty =
            AvaloniaProperty.Register<FloatingTray, bool>(nameof(IsEngaged));

        /// <summary>The glow burst: up to full strength in a third of its run, then back down.</summary>
        private static readonly Animation BurstAnimation = new()
        {
            Duration = TimeSpan.FromMilliseconds(700),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(0.35), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d) } },
            },
        };

        private Border _burst;
        private CancellationTokenSource _burstRun;

        static FloatingTray()
        {
            ControlThemes.EnsureRegistered();
            OrientationProperty.Changed.AddClassHandler<FloatingTray>((tray, _) => tray.PushOrientation());
            IsEngagedProperty.Changed.AddClassHandler<FloatingTray>((tray, _) => tray.PushEngaged());
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

        public bool IsEngaged
        {
            get => GetValue(IsEngagedProperty);
            set => SetValue(IsEngagedProperty, value);
        }

        /// <summary>
        /// Swells an accent glow round the strip and lets it fade, to draw the eye to it; a second burst
        /// restarts the first. Only a tray in a window that reserves the glow's room may call it
        /// (<see cref="FloatingTrayOptions.EngagedFrame"/>). Skipped with Windows' animation effects off.
        /// </summary>
        public void PlayGlowBurst()
        {
            if (_burst == null || !AnimationsEnabled())
                return;

            _burstRun?.Cancel();
            _burstRun = new CancellationTokenSource();
            _ = BurstAnimation.RunAsync(_burst, _burstRun.Token);
        }

        /// <summary>Windows' "Animation effects" setting. Every other platform animates.</summary>
        internal static bool AnimationsEnabled()
        {
            if (!OperatingSystem.IsWindows())
                return true;
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var enabled, 0) || enabled;
        }

        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] out bool pvParam, uint fWinIni);

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _burst = e.NameScope.Find<Border>("PART_Burst");
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
            if (container is TrayGrip grip)
                grip.IsEngaged = IsEngaged;
        }

        /// <summary>The grip is the one item that restyles itself for the engaged frame: its dots sit on the
        /// spine at full strength.</summary>
        private void PushEngaged()
        {
            var engaged = IsEngaged;
            foreach (var item in Items)
            {
                if (item is TrayGrip grip)
                    grip.IsEngaged = engaged;
            }
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
