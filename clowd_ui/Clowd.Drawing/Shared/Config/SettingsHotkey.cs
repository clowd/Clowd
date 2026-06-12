using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Input;
using RT.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// A serializable manager for global hotkeys. The gesture is the only persisted value; actual OS
    /// registration is delegated to the pluggable <see cref="Host"/> (installed by the UI layer at
    /// startup) so this library never references a platform hook itself. While no host is installed,
    /// triggers behave like inert stubs: never registered, <see cref="TriggerExecuted"/> never fires.
    /// </summary>
    public sealed class GlobalTrigger : SimpleNotifyObject, IDisposable
    {
        public static bool IsPaused { get; set; }

        private const string ERROR_NO_HOST = "Global hotkeys are not supported in this build.";

        private static readonly List<GlobalTrigger> Instances = new();
        private static IGlobalTriggerHost _host;

        /// <summary>
        /// The OS hotkey backend. Installing (or clearing) the host re-evaluates the registration of
        /// every live trigger. Must be accessed from the UI thread.
        /// </summary>
        public static IGlobalTriggerHost Host
        {
            get => _host;
            set
            {
                if (ReferenceEquals(_host, value))
                    return;

                _host = value;
                foreach (var inst in Instances.ToArray())
                    inst.RefreshHotkey();
            }
        }

        public string KeyGestureText => KeyGesture?.ToString();

        public SimpleKeyGesture KeyGesture
        {
            get => _keyGesture;
            set
            {
                if (Set(ref _keyGesture, value, nameof(KeyGesture), nameof(KeyGestureText)))
                    RefreshHotkey();
            }
        }

        public bool IsRegistered
        {
            get => _isRegistered;
            private set => Set(ref _isRegistered, value);
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
                _triggerExecuted += value;
                if (!IsRegistered)
                    RefreshHotkey();
            }
            remove => _triggerExecuted -= value;
        }

        private SimpleKeyGesture _keyGesture; // only persisted value
        [ClassifyIgnore] private EventHandler _triggerExecuted;
        [ClassifyIgnore] private bool _isRegistered;
        [ClassifyIgnore] private string _error = ERROR_NO_HOST;
        [ClassifyIgnore] private IGlobalTriggerRegistration _registration;
        [ClassifyIgnore] private bool _disposed;

        public GlobalTrigger(Key key, KeyModifiers modifier)
            : this(new SimpleKeyGesture(key, modifier))
        { }

        public GlobalTrigger(Key key)
            : this(new SimpleKeyGesture(key, KeyModifiers.None))
        { }

        public GlobalTrigger()
            : this((SimpleKeyGesture)null)
        { }

        public GlobalTrigger(SimpleKeyGesture gesture)
        {
            _keyGesture = gesture;
            Instances.Add(this);
        }

        private void RefreshHotkey()
        {
            if (_registration != null)
            {
                _registration.StatusChanged -= OnRegistrationStatusChanged;
                _registration.Dispose();
                _registration = null;
            }

            if (_disposed)
                return;

            if (_host == null)
            {
                IsRegistered = false;
                Error = ERROR_NO_HOST;
                return;
            }

            if (_triggerExecuted == null)
            {
                // do not register if nothing is listening (matches the WPF behaviour)
                IsRegistered = false;
                Error = "";
                return;
            }

            if (_keyGesture == null || _keyGesture.Key == Key.None)
            {
                IsRegistered = false;
                Error = "Gesture is empty.";
                return;
            }

            _registration = _host.RegisterTrigger(_keyGesture, OnHostTriggerExecuted);
            _registration.StatusChanged += OnRegistrationStatusChanged;
            OnRegistrationStatusChanged(_registration, EventArgs.Empty);
        }

        private void OnRegistrationStatusChanged(object sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _registration))
                return;

            IsRegistered = _registration.IsRegistered;
            Error = _registration.Error ?? "";
        }

        private void OnHostTriggerExecuted()
        {
            if (!IsPaused && !_disposed)
                _triggerExecuted?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Instances.Remove(this);
            _triggerExecuted = null;

            if (_registration != null)
            {
                _registration.StatusChanged -= OnRegistrationStatusChanged;
                _registration.Dispose();
                _registration = null;
            }
        }

        public override string ToString()
        {
            if (_keyGesture == null)
                return "Trigger:{null/false}";

            return $"Trigger:{{{_keyGesture?.Key}:{IsRegistered}}}";
        }
    }

    public class SettingsHotkey : CategoryBase
    {
        [DisplayName("Upload from File"), ClassifyIgnoreIfDefault]
        public GlobalTrigger FileUploadShortcut
        {
            get => _fileUploadShortcut;
            set => Set(ref _fileUploadShortcut, value);
        }

        [DisplayName("Upload Clipboard"), ClassifyIgnoreIfDefault]
        public GlobalTrigger ClipboardUploadShortcut
        {
            get => _clipboardUploadShortcut;
            set => Set(ref _clipboardUploadShortcut, value);
        }

        [DisplayName("Capture Region"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureRegionShortcut
        {
            get => _captureRegionShortcut;
            set => Set(ref _captureRegionShortcut, value);
        }

        [DisplayName("Capture Active Screen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureFullscreenShortcut
        {
            get => _captureFullscreenShortcut;
            set => Set(ref _captureFullscreenShortcut, value);
        }

        [DisplayName("Capture Active Window"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureActiveShortcut
        {
            get => _captureActiveShortcut;
            set => Set(ref _captureActiveShortcut, value);
        }

        [DisplayName("Draw on Screen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger DrawOnScreenShortcut
        {
            get => _drawOnScreenShortcut;
            set => Set(ref _drawOnScreenShortcut, value);
        }

        [DisplayName("Start / Stop Recording"), ClassifyIgnoreIfDefault]
        public GlobalTrigger StartStopRecordingShortcut
        {
            get => _startStopRecordingShortcut;
            set => Set(ref _startStopRecordingShortcut, value);
        }

        private GlobalTrigger _fileUploadShortcut = new();
        private GlobalTrigger _clipboardUploadShortcut = new();
        private GlobalTrigger _captureRegionShortcut = new(Key.Snapshot);
        private GlobalTrigger _captureFullscreenShortcut = new(Key.Snapshot, KeyModifiers.Control);
        private GlobalTrigger _captureActiveShortcut = new(Key.Snapshot, KeyModifiers.Alt);
        private GlobalTrigger _drawOnScreenShortcut = new(Key.Snapshot, KeyModifiers.Control | KeyModifiers.Shift);
        private GlobalTrigger _startStopRecordingShortcut = new(Key.Snapshot, KeyModifiers.Shift);

        public SettingsHotkey()
        {
            Subscribe(
                FileUploadShortcut, CaptureRegionShortcut, CaptureFullscreenShortcut, CaptureActiveShortcut, DrawOnScreenShortcut,
                StartStopRecordingShortcut, ClipboardUploadShortcut);
        }

        protected override void DisposeInternal()
        {
            FileUploadShortcut?.Dispose();
            CaptureRegionShortcut?.Dispose();
            CaptureFullscreenShortcut?.Dispose();
            CaptureActiveShortcut?.Dispose();
            DrawOnScreenShortcut?.Dispose();
            StartStopRecordingShortcut?.Dispose();
            ClipboardUploadShortcut?.Dispose();
        }
    }
}
