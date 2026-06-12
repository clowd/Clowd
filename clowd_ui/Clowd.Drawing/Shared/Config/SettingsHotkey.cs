using System;
using System.ComponentModel;
using Avalonia.Input;
using RT.Serialization;

namespace Clowd.Config
{
    /// <summary>
    /// STUB: no OS hotkey registration is performed in this migration. The gesture is kept
    /// (and persisted) so the settings UI can render it alongside the "not supported" status.
    /// </summary>
    public sealed class GlobalTrigger : SimpleNotifyObject, IDisposable
    {
        public static bool IsPaused { get; set; }

        public string KeyGestureText => KeyGesture?.ToString();

        public SimpleKeyGesture KeyGesture
        {
            get => _keyGesture;
            set => Set(ref _keyGesture, value, nameof(KeyGesture), nameof(KeyGestureText));
        }

        public bool IsRegistered => false;

        public string Error => "Global hotkeys are not supported in this build.";

        public event EventHandler TriggerExecuted
        {
            add => _triggerExecuted += value; // never fires in this build
            remove => _triggerExecuted -= value;
        }

        private SimpleKeyGesture _keyGesture; // only persisted value
        [ClassifyIgnore] private EventHandler _triggerExecuted;

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
        }

        public void Dispose()
        {
            _triggerExecuted = null;
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
