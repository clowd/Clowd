using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using RT.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// A serializable manager for global hotkeys
    /// </summary>
    public class GlobalTrigger : IClassifyObjectProcessor, IDisposable, INotifyPropertyChanged
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
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
                _gesture = value;
                RefreshHotkey();
            }
        }

        public event EventHandler TriggerExecuted;

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

        public event PropertyChangedEventHandler PropertyChanged;

        public GlobalTrigger(Key key, ModifierKeys modifier)
            : this(new KeyGesture(key, modifier))
        {
        }

        public GlobalTrigger(Key key)
            : this(new KeyGesture(key, ModifierKeys.None))
        {
        }

        public GlobalTrigger(KeyGesture gesture)
        {
            _gesture = gesture;
            Initialize();
        }

        public GlobalTrigger()
        {
            Initialize();
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
                _hotKey = new HotKey(Gesture.Key, Gesture.Modifiers, OnExecuted, false);
            }
            catch (InvalidOperationException)
            {
                // the hotkey is already registered within this process.
                IsRegistered = false;
                Error = "Selected gesture already set for a different action.";
                return;
            }

            try
            {
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
            catch (Exception e)
            {
                IsRegistered = false;
                Error = "Internal Error: " + e.Message;
            }
        }

        private void OnExecuted(HotKey obj)
        {
            TriggerExecuted?.Invoke(this, new EventArgs());
        }

        private void RefreshHotkey()
        {
            _hotKey?.Dispose();
            Initialize();
        }

        private bool GestureEqualsCurrent(KeyGesture other)
        {
            if (other == null && _gesture == null)
                return true;
            if (other == null || _gesture == null)
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

        public StorableKeyGesture()
        {

        }

        public StorableKeyGesture(Key key)
        {
            Key = key;
        }

        public StorableKeyGesture(Key key, ModifierKeys modifiers)
        {
            Key = key;
            Modifiers = modifiers;
        }
    }
}
