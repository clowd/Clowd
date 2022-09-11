using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using RT.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// A serializable manager for global hotkeys
    /// </summary>
    public sealed class GlobalTrigger : SimpleNotifyObject, IClassifyObjectProcessor, IDisposable
    {
        public static bool IsPaused { get; set; }
        private static List<GlobalTrigger> Instances = new();

        public string KeyGestureText => IsRegistered ? KeyGesture?.ToString() : null;

        public SimpleKeyGesture KeyGesture
        {
            get => _keyGesture;
            set
            {
                ThrowIfDisposed();
                if (Set(ref _keyGesture, value, nameof(KeyGesture), nameof(KeyGestureText)))
                    RefreshHotkey();
            }
        }

        public bool IsRegistered
        {
            get => _isRegistered;
            private set => Set(ref _isRegistered, value, nameof(IsRegistered), nameof(KeyGestureText));
        }

        public string Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        public event EventHandler TriggerExecuted
        {
            add
            {
                ThrowIfDisposed();
                _triggerExecuted += value;
                if (!IsRegistered) RefreshHotkey();
            }
            remove { _triggerExecuted -= value; }
        }

        private SimpleKeyGesture _keyGesture; // only persisted value
        [ClassifyIgnore] private EventHandler _triggerExecuted;
        [ClassifyIgnore] private bool _isRegistered;
        [ClassifyIgnore] private string _error;
        [ClassifyIgnore] private bool _disposed;
        [ClassifyIgnore] private HotKey _hotKey;

        public GlobalTrigger(Key key, ModifierKeys modifier)
            : this(new SimpleKeyGesture(key, modifier))
        { }

        public GlobalTrigger(Key key)
            : this(new SimpleKeyGesture(key, ModifierKeys.None))
        { }

        public GlobalTrigger()
            : this(null)
        { }

        public GlobalTrigger(SimpleKeyGesture gesture)
        {
            _keyGesture = gesture;
            Instances.Add(this);
            RefreshHotkey();
        }

        private void RefreshHotkey()
        {
            _hotKey?.Dispose();
            
            ThrowIfDisposed();

            if (_triggerExecuted == null)
            {
                // do not register if there are no triggers
                return;
            }

            if (_keyGesture == null)
            {
                IsRegistered = false;
                Error = "Gesture is empty.";
                return;
            }

            if (Instances.Except(new []{ this }).Any(i => i._keyGesture?.Equals(_keyGesture) == true))
            {
                _keyGesture = null;
                IsRegistered = false;
                Error = "Gesture is already in-use by another hotkey.";
                OnPropertyChanged(nameof(KeyGesture));
                return;
            }

            try
            {
                _hotKey = new HotKey(_keyGesture.Key, _keyGesture.Modifiers, OnExecuted, false);
            }
            catch (InvalidOperationException)
            {
                // the hotkey is already registered within this process.
                IsRegistered = false;
                Error = "Gesture is already in-use by another hotkey.";
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
            if (!IsPaused)
            {
                _triggerExecuted?.Invoke(this, new EventArgs());
            }
        }

        public void BeforeSerialize()
        { }

        public void AfterDeserialize()
        {
            RefreshHotkey();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("GlobalTrigger");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hotKey?.Dispose();
            Instances.Remove(this);
            IsRegistered = false;
            _triggerExecuted = null;
            Error = "Disposed";
        }

        public override string ToString()
        {
            if (_keyGesture == null)
                return "Trigger:{null/false}";

            return $"Trigger:{{{_keyGesture?.Key}:{IsRegistered}}}";
        }
    }
}
