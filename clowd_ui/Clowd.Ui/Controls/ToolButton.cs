using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    public class ToolButton : ToggleButton
    {
        public static readonly StyledProperty<Geometry> IconPathProperty =
            AvaloniaProperty.Register<ToolButton, Geometry>(nameof(IconPath));

        public Geometry IconPath
        {
            get => GetValue(IconPathProperty);
            set => SetValue(IconPathProperty, value);
        }

        public static readonly StyledProperty<bool> CanToggleProperty =
            AvaloniaProperty.Register<ToolButton, bool>(nameof(CanToggle), false);

        public bool CanToggle
        {
            get => GetValue(CanToggleProperty);
            set => SetValue(CanToggleProperty, value);
        }

        static ToolButton()
        {
            ControlThemes.EnsureRegistered();
        }

        protected override void Toggle()
        {
            if (CanToggle)
                base.Toggle();
        }
    }
}
