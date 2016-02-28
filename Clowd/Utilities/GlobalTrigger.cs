using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using RT.Util.Serialization;

namespace Clowd.Utilities
{
    /// <summary>
    /// A serializable manager for global hotkeys
    /// </summary>
    public class GlobalTrigger : IClassifyObjectProcessor, IDisposable
    {
        [ClassifyIgnore]
        public KeyGesture Gesture
        {
            get
            {
                return _gesture;
            }
            set
            {
                ThrowIfDisposed();
                if (GestureEqualsCurrent(value))
                    return;
                _gesture = value;
                RefreshHotkey();
            }
        }

        [ClassifyIgnore]
        public Action Action { get; private set; }

        [ClassifyIgnore]
        public bool IsRegistered { get; private set; }

        [ClassifyIgnore]
        public string Error { get; private set; }

        // the only value that actually gets serialized.
        private StorableKeyGesture _storable;

        [ClassifyIgnore]
        private bool _disposed = false;

        [ClassifyIgnore]
        private HotKey _hotKey = null;

        [ClassifyIgnore]
        private KeyGesture _gesture;

        public GlobalTrigger(Key key, ModifierKeys modifier, Action action)
            : this(new KeyGesture(key, modifier), action)
        {
        }
        public GlobalTrigger(Key key, Action action)
            : this(new KeyGesture(key, ModifierKeys.None), action)
        {
        }
        public GlobalTrigger(Action action)
            : this(null, action)
        {
        }
        public GlobalTrigger(KeyGesture gesture, Action action)
        {
            _gesture = gesture;
            Action = action;
            Initialize();
        }
        private GlobalTrigger()
        {
            // only invoked by classify
        }

        private void Initialize()
        {
            if (_gesture == null)
            {
                IsRegistered = false;
                Error = "Gesture is empty.";
                return;
            }

            try
            {
                _hotKey = new HotKey(Gesture.Key, Gesture.Modifiers, key => Action(), false);
            }
            catch (InvalidOperationException)
            {
                // the hotkey is already registered within this process.
                IsRegistered = false;
                Error = "Selected gesture already set for a different action.";
                return;
            }

            var success = _hotKey.Register();
            if (success)
            {
                IsRegistered = true;
                Error = "";
            }
            else
            {
                IsRegistered = false;
                Error = "Selected gesture is in use by a different process.";
            }
        }
        private void RefreshHotkey()
        {
            _hotKey?.Dispose();
            Initialize();
        }

        private bool GestureEqualsCurrent(KeyGesture other)
        {
            if (other == null)
                return false;
            return other.Key == _gesture.Key && other.Modifiers == _gesture.Modifiers;
        }

        public void BeforeSerialize()
        {
            if (_gesture != null)
                _storable = new StorableKeyGesture() { Key = _gesture.Key, Modifiers = _gesture.Modifiers };
        }
        public void AfterDeserialize()
        {
            if (_storable != null)
                _gesture = new KeyGesture(_storable.Key, _storable.Modifiers);
            RefreshHotkey();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("KeyBinding");
        }
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hotKey?.Dispose();
            IsRegistered = false;
        }

        public override string ToString()
        {
            if (_gesture == null)
                return "Trigger:{null/false}";

            return $"Trigger:{{{_gesture.GetDisplayStringForCulture(CultureInfo.CurrentUICulture)}:{IsRegistered}}}";
        }
    }
    public class StorableKeyGesture
    {
        public Key Key { get; set; }
        public ModifierKeys Modifiers { get; set; }
    }
}
