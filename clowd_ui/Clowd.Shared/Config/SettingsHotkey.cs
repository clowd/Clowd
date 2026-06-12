using System.ComponentModel;
using Avalonia.Input;

namespace Clowd.Config
{
    /// <summary>
    /// Persisted hotkey gestures — pure data. OS registration, live status and the
    /// pause-while-editing behaviour are owned entirely by the UI layer (HotkeyManager in
    /// Clowd.Ui); this class never references any hotkey backend.
    /// </summary>
    public class SettingsHotkey : SimpleNotifyObject
    {
        [DisplayName("Upload from File")]
        public SimpleKeyGesture FileUploadShortcut
        {
            get => _fileUploadShortcut;
            set => Set(ref _fileUploadShortcut, Normalize(value));
        }

        [DisplayName("Upload Clipboard")]
        public SimpleKeyGesture ClipboardUploadShortcut
        {
            get => _clipboardUploadShortcut;
            set => Set(ref _clipboardUploadShortcut, Normalize(value));
        }

        [DisplayName("Capture Region")]
        public SimpleKeyGesture CaptureRegionShortcut
        {
            get => _captureRegionShortcut;
            set => Set(ref _captureRegionShortcut, Normalize(value));
        }

        [DisplayName("Capture Active Screen")]
        public SimpleKeyGesture CaptureFullscreenShortcut
        {
            get => _captureFullscreenShortcut;
            set => Set(ref _captureFullscreenShortcut, Normalize(value));
        }

        [DisplayName("Capture Active Window")]
        public SimpleKeyGesture CaptureActiveShortcut
        {
            get => _captureActiveShortcut;
            set => Set(ref _captureActiveShortcut, Normalize(value));
        }

        [DisplayName("Draw on Screen")]
        public SimpleKeyGesture DrawOnScreenShortcut
        {
            get => _drawOnScreenShortcut;
            set => Set(ref _drawOnScreenShortcut, Normalize(value));
        }

        [DisplayName("Start / Stop Recording")]
        public SimpleKeyGesture StartStopRecordingShortcut
        {
            get => _startStopRecordingShortcut;
            set => Set(ref _startStopRecordingShortcut, Normalize(value));
        }

        /// <summary>
        /// Canonicalizes "not set": a Key.None gesture and null are the same state, stored as null.
        /// (A cleared gesture is persisted as "None" because the configuration binder never assigns
        /// null converted values — see SettingsService — and must round-trip back to null here.)
        /// </summary>
        private static SimpleKeyGesture Normalize(SimpleKeyGesture value) =>
            value != null && value.Key == Key.None ? null : value;

        private SimpleKeyGesture _fileUploadShortcut;
        private SimpleKeyGesture _clipboardUploadShortcut;
        private SimpleKeyGesture _captureRegionShortcut = new(Key.Snapshot);
        private SimpleKeyGesture _captureFullscreenShortcut = new(Key.Snapshot, KeyModifiers.Control);
        private SimpleKeyGesture _captureActiveShortcut = new(Key.Snapshot, KeyModifiers.Alt);
        private SimpleKeyGesture _drawOnScreenShortcut = new(Key.Snapshot, KeyModifiers.Control | KeyModifiers.Shift);
        private SimpleKeyGesture _startStopRecordingShortcut = new(Key.Snapshot, KeyModifiers.Shift);
    }
}
