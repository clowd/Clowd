using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Prisc.UI.Controls
{
    /// <summary>
    /// A Button whose string Content declares an accelerator with a leading underscore (WPF
    /// menu convention, e.g. "Copy HE_X" or "_Cancel"). The marked letter renders with an
    /// always-visible underline, and pressing that key anywhere in the owning window clicks
    /// the button. The bare key is suppressed while a TextBox has focus (the letter is being
    /// typed); Alt+key works regardless. Set Content the same way from XAML or code-behind.
    /// Content that is not a string (an icon beside the label, say) can't declare its key, so it
    /// is named with <see cref="Accelerator"/> instead, and the content underlines its own letter.
    /// </summary>
    public class AcceleratedButton : Button
    {
        public static readonly StyledProperty<Key> AcceleratorProperty =
            AvaloniaProperty.Register<AcceleratedButton, Key>(nameof(Accelerator));

        /// <summary>The key that clicks the button when Content is not a string; ignored when a
        /// string Content declares one with an underscore.</summary>
        public Key Accelerator
        {
            get => GetValue(AcceleratorProperty);
            set => SetValue(AcceleratorProperty, value);
        }

        private Key? _key;
        private TopLevel _topLevel;

        // keep the plain Button style key so theme styles ("Button.copy", SolidButton) apply
        protected override Type StyleKeyOverride => typeof(Button);

        private static readonly FuncDataTemplate<string> LabelTemplate = new((text, _) => BuildLabel(text));

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ContentProperty)
            {
                // the underlining template is for string Content only: given a control it doesn't
                // match, and the presenter falls back to printing the control's type name
                if (Content is string)
                    ContentTemplate = LabelTemplate;
                else if (ContentTemplate == LabelTemplate)
                    ClearValue(ContentTemplateProperty);
            }

            if (change.Property == ContentProperty || change.Property == AcceleratorProperty)
                _key = ParseKey(Content as string) ?? (Accelerator != Key.None ? Accelerator : null);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;
        }

        private void OnTopLevelKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || _key == null || e.Key != _key)
                return;

            if (!IsEffectivelyEnabled || !IsEffectivelyVisible)
                return;

            var typing = (_topLevel?.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<TextBox>(true) != null;
            if (e.KeyModifiers != KeyModifiers.Alt && (e.KeyModifiers != KeyModifiers.None || typing))
                return;

            e.Handled = true;
            OnClick();
        }

        private static Key? ParseKey(string text)
        {
            var idx = text?.IndexOf('_') ?? -1;
            if (idx < 0 || idx >= text.Length - 1)
                return null;

            var c = char.ToUpperInvariant(text[idx + 1]);
            if (c >= 'A' && c <= 'Z')
                return Key.A + (c - 'A');
            if (c >= '0' && c <= '9')
                return Key.D0 + (c - '0');
            return null;
        }

        private static TextBlock BuildLabel(string text)
        {
            var tb = new TextBlock();
            var idx = text?.IndexOf('_') ?? -1;
            if (idx < 0 || idx >= text.Length - 1)
            {
                tb.Text = text;
                return tb;
            }

            if (idx > 0)
                tb.Inlines.Add(new Run(text.Substring(0, idx)));
            tb.Inlines.Add(new Run(text[idx + 1].ToString()) { TextDecorations = TextDecorations.Underline });
            if (idx + 2 < text.Length)
                tb.Inlines.Add(new Run(text.Substring(idx + 2)));
            return tb;
        }
    }
}
