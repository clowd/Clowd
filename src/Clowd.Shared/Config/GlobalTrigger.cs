using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using RT.Serialization;

namespace Clowd.Config
{
    public class GlobalKeyGesture : IEquatable<GlobalKeyGesture>
    {
        public Key Key { get; }
        public ModifierKeys Modifiers { get; }

        public GlobalKeyGesture()
        { }

        public GlobalKeyGesture(Key key)
        {
            Key = key;
        }

        public GlobalKeyGesture(Key key, ModifierKeys modifiers)
        {
            Key = key;
            Modifiers = modifiers;
        }

        public override int GetHashCode()
        {
            return unchecked(Key.GetHashCode() + Modifiers.GetHashCode());
        }

        public override bool Equals(object obj)
        {
            if (obj is GlobalKeyGesture kg) return Equals(kg);
            return false;
        }

        public bool Equals(GlobalKeyGesture other)
        {
            if (other == null) return false;
            return other.Key == Key && other.Modifiers == Modifiers;
        }

        public override string ToString()
        {
            if (Key == Key.None)
                return String.Empty;

            string strBinding = "";
            string strKey = Key.ToString();
            if (strKey != String.Empty)
            {
                if (Modifiers != ModifierKeys.None)
                {
                    strBinding += Modifiers.ToString();
                    if (strBinding != String.Empty)
                    {
                        strBinding += '+';
                    }
                }

                strBinding += strKey;
            }

            return String.Join("+", strBinding.Split('+', ',').Select(c => c.Trim()))
                .Replace("Snapshot", "PrtScr")
                .Replace("Control", "Ctrl");
        }
    }

    /// <summary>
    /// A serializable manager for global hotkeys
    /// </summary>
    public sealed class GlobalTrigger : SimpleNotifyObject, IClassifyObjectProcessor, IDisposable
    {
        public static bool IsPaused { get; set; }
        private static List<GlobalTrigger> Instances = new();

        public string KeyGestureText => IsRegistered ? KeyGesture?.ToString() : null;

        public GlobalKeyGesture KeyGesture
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

        private GlobalKeyGesture _keyGesture; // only persisted value
        [ClassifyIgnore] private EventHandler _triggerExecuted;
        [ClassifyIgnore] private bool _isRegistered;
        [ClassifyIgnore] private string _error;
        [ClassifyIgnore] private bool _disposed;
        [ClassifyIgnore] private HotKey _hotKey;

        public GlobalTrigger(Key key, ModifierKeys modifier)
            : this(new GlobalKeyGesture(key, modifier))
        { }

        public GlobalTrigger(Key key)
            : this(new GlobalKeyGesture(key, ModifierKeys.None))
        { }

        public GlobalTrigger()
            : this(null)
        { }

        public GlobalTrigger(GlobalKeyGesture gesture)
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
