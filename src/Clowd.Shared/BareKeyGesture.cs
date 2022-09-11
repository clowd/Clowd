using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Clowd
{
    /// <summary>
    /// This gesture doesn't handle keys originating in a text control. This allows key bindings without modifier keys
    /// that don't break normal typing. A standard KeyGesture doesn't have such logic; this allows the parent of a
    /// text box to handle such bare keypresses before the textbox gets to see it as normal text input, thus breaking
    /// normal typing.
    /// </summary>
    public class BareKeyGesture : InputGesture
    {
        public Key Key { get; set; }

        public override bool Matches(object targetElement, InputEventArgs inputEventArgs)
        {
            var keyEventArgs = inputEventArgs as KeyEventArgs;
            if (keyEventArgs == null)
                return false;

            if (inputEventArgs.OriginalSource is TextBoxBase)
                return false;

            return (int)Key == (int)keyEventArgs.Key && Keyboard.Modifiers == ModifierKeys.None;
        }
    }

    /// <summary>
    /// This only exists because the InputBinding constructor is protected, but since we have to have it anyway
    /// we also use this opportunity to simplify adding a BareKeyGesture to it.
    /// </summary>
    public class BareKeyBinding : InputBinding
    {
        private BareKeyGesture _gesture = new();

        public BareKeyBinding()
        {
            Gesture = _gesture;
        }

        public BareKeyBinding(Key key)
        {
            _gesture = new BareKeyGesture() { Key = key };
            Gesture = _gesture;
        }

        public Key Key
        {
            get => _gesture.Key;
            set { _gesture.Key = value; }
        }
    }
}
