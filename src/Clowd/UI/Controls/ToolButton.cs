using System.Windows;
using System.Windows.Controls.Primitives;

namespace Clowd.UI.Controls
{
    class ToolButton : ToggleButton
    {
        public UIElement IconPath
        {
            get { return (UIElement)GetValue(IconPathProperty); }
            set { SetValue(IconPathProperty, value); }
        }

        public static readonly UIElement IconPathDefaultValue = null;

        public static readonly DependencyProperty IconPathProperty =
            DependencyProperty.Register(nameof(IconPath), typeof(UIElement), typeof(ToolButton),
                new PropertyMetadata(IconPathDefaultValue, (s, e) => (s as ToolButton)?.OnIconPathChanged(s, e)));

        public event DependencyPropertyChangedEventHandler IconPathChanged;

        protected virtual void OnIconPathChanged(object sender, DependencyPropertyChangedEventArgs e)
            => this.IconPathChanged?.Invoke(sender, e);

        public bool CanToggle
        {
            get { return (bool)GetValue(CanToggleProperty); }
            set { SetValue(CanToggleProperty, value); }
        }

        public static readonly bool CanToggleDefaultValue = false;

        public static readonly DependencyProperty CanToggleProperty =
            DependencyProperty.Register(nameof(CanToggle), typeof(bool), typeof(ToolButton),
                new PropertyMetadata(CanToggleDefaultValue, (s, e) => (s as ToolButton)?.OnCanToggleChanged(s, e)));

        public event DependencyPropertyChangedEventHandler CanToggleChanged;

        protected virtual void OnCanToggleChanged(object sender, DependencyPropertyChangedEventArgs e)
            => this.CanToggleChanged?.Invoke(sender, e);

        static ToolButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolButton), new FrameworkPropertyMetadata(typeof(ToolButton)));
        }

        public ToolButton()
        {
        }

        protected override void OnToggle()
        {
            if (CanToggle)
                base.OnToggle();
        }
    }
}
