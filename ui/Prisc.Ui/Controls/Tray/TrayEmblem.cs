using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The app's mark at the head of every strip: the same logo geometry and brand brush the main
    /// window's header shows, scaled from its 16-unit canvas to 32 px, drawn straight on the tray with no fill, no hover and no click — it says
    /// whose strip this is and nothing else. The chassis adds one right after the grip (or first, on a
    /// strip without one), so no owner has to remember it.
    /// <para>
    /// The geometry and the brush are the application's own resources, looked up once on first render
    /// rather than duplicated here: the header is the single source for both, and a mark that drifted
    /// from it would be the wrong logo. Missing resources draw nothing rather than throw — the strips
    /// are floating over a capture, and a branding lookup must never take one down.
    /// </para>
    /// </summary>
    public sealed class TrayEmblem : Control, ITrayOrientable
    {
        private const string GeometryKey = "PathLogoSmall16", BrushKey = "ClowdAccentBrush";
        private const double CanvasSize = 16;
        private const double MarkSize = 32;

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayEmblem, Orientation>(nameof(Orientation), Orientation.Horizontal);

        private Geometry _geometry;
        private IBrush _brush;
        private bool _resolved;

        static TrayEmblem()
        {
            AffectsMeasure<TrayEmblem>(OrientationProperty);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        /// <summary>
        /// <see cref="TrayTokens.EmblemLength"/> along the strip axis; across it the panel stretches the
        /// control to the row height / column width, so the mark is centred there by
        /// <see cref="Render"/> rather than by layout.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(TrayTokens.EmblemLength, TrayTokens.EmblemLength);
        }

        public override void Render(DrawingContext context)
        {
            if (!_resolved)
            {
                _resolved = true;
                var app = Application.Current;
                if (app != null)
                {
                    if (app.TryFindResource(GeometryKey, out var geometry))
                        _geometry = geometry as Geometry;
                    if (app.TryFindResource(BrushKey, out var brush))
                        _brush = brush as IBrush;
                }
            }

            if (_geometry == null || _brush == null)
                return;

            // a viewbox by hand: the 16-unit canvas scaled uniformly to MarkSize and centred in the control
            var scale = MarkSize / CanvasSize;
            var bounds = Bounds.Size;
            var offset = new Vector((bounds.Width - MarkSize) / 2, (bounds.Height - MarkSize) / 2);
            var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset);
            using (context.PushTransform(transform))
                context.DrawGeometry(_brush, null, _geometry);
        }
    }
}
