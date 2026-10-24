using System.ComponentModel;
using Avalonia.Input;

namespace Clowd.Config
{
    /// <summary>
    /// Persisted hotkey gestures — pure data. OS registration, live status and the
    /// pause-while-editing behavior are owned entirely by the UI layer (HotkeyManager in
    /// Clowd.Ui); this class never references any hotkey backend.
    /// <para>Each action holds an ordered <see cref="GestureList"/>, possibly empty; see that type
    /// for why it is not a plain collection. The properties never return null — assigning null
    /// clears the action. The Hotkeys page groups and orders the rows itself (HotkeyPageLayout in
    /// Clowd.Ui); it takes each row's name from [DisplayName] and its visibility from [VisibleWhen].</para>
    /// </summary>
    public class SettingsHotkey : SimpleNotifyObject
    {
        // the rows for a feature that can be switched off (uploads, recording) go with it: the
        // gesture is kept, but a shortcut for something the app no longer offers is noise here —
        // and HotkeyManager stops registering it, so it does not swallow the key either.
        [DisplayName("Upload from File")]
        [VisibleWhen(nameof(SettingsUpload.Mode), UploadsMode.On, Section = nameof(SettingsRoot.Uploads))]
        public GestureList FileUploadShortcut
        {
            get => _fileUploadShortcut;
            set => Set(ref _fileUploadShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Upload Clipboard")]
        [VisibleWhen(nameof(SettingsUpload.Mode), UploadsMode.On, Section = nameof(SettingsRoot.Uploads))]
        public GestureList ClipboardUploadShortcut
        {
            get => _clipboardUploadShortcut;
            set => Set(ref _clipboardUploadShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Capture Region")]
        public GestureList CaptureRegionShortcut
        {
            get => _captureRegionShortcut;
            set => Set(ref _captureRegionShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Capture Active Screen")]
        public GestureList CaptureFullscreenShortcut
        {
            get => _captureFullscreenShortcut;
            set => Set(ref _captureFullscreenShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Capture Active Window")]
        public GestureList CaptureActiveShortcut
        {
            get => _captureActiveShortcut;
            set => Set(ref _captureActiveShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Start / Stop Recording")]
        [VisibleWhen(nameof(SettingsRecording.Mode), RecordingMode.Studio, RecordingMode.Instant, Section = nameof(SettingsRoot.Recording))]
        public GestureList StartStopRecordingShortcut
        {
            get => _startStopRecordingShortcut;
            set => Set(ref _startStopRecordingShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Share Region")]
        [VisibleWhen(nameof(SettingsShareRegion.Mode), ShareRegionMode.On, Section = nameof(SettingsRoot.ShareRegion))]
        public GestureList ShareRegionShortcut
        {
            get => _shareRegionShortcut;
            set => Set(ref _shareRegionShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Draw on Screen")]
        [VisibleWhen(nameof(SettingsDrawOnScreen.Mode), DrawOnScreenMode.On, Section = nameof(SettingsRoot.DrawOnScreen))]
        public GestureList DrawOnScreenShortcut
        {
            get => _drawOnScreenShortcut;
            set => Set(ref _drawOnScreenShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("Draw on Instant Screenshot")]
        [VisibleWhen(nameof(SettingsDrawOnScreen.Mode), DrawOnScreenMode.On, Section = nameof(SettingsRoot.DrawOnScreen))]
        public GestureList DrawOnScreenshotShortcut
        {
            get => _drawOnScreenshotShortcut;
            set => Set(ref _drawOnScreenshotShortcut, value ?? GestureList.Empty);
        }

        // the tool windows, in the tray menu's order. Opening one is never urgent the way a
        // screenshot is, so none of them ships with a gesture (see the no-default reasoning below).
        [DisplayName("Color Wheel")]
        public GestureList ColorWheelShortcut
        {
            get => _colorWheelShortcut;
            set => Set(ref _colorWheelShortcut, value ?? GestureList.Empty);
        }

        [DisplayName("New Image Editor")]
        public GestureList NewImageEditorShortcut
        {
            get => _newImageEditorShortcut;
            set => Set(ref _newImageEditorShortcut, value ?? GestureList.Empty);
        }

        // Recording Off takes the video editor out of the app with it, so this row goes too.
        [DisplayName("New Video Editor")]
        [VisibleWhen(nameof(SettingsRecording.Mode), RecordingMode.Studio, RecordingMode.Instant, Section = nameof(SettingsRoot.Recording))]
        public GestureList NewVideoEditorShortcut
        {
            get => _newVideoEditorShortcut;
            set => Set(ref _newVideoEditorShortcut, value ?? GestureList.Empty);
        }

        private GestureList _fileUploadShortcut = GestureList.Empty;
        private GestureList _clipboardUploadShortcut = GestureList.Empty;
        private GestureList _captureRegionShortcut = GestureList.Of(new SimpleKeyGesture(Key.Snapshot));
        private GestureList _captureFullscreenShortcut = GestureList.Of(new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Control));
        private GestureList _captureActiveShortcut = GestureList.Of(new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Alt));
        private GestureList _startStopRecordingShortcut = GestureList.Of(new SimpleKeyGesture(Key.Snapshot, KeyModifiers.Shift));

        // No default, on purpose: PrintScreen with no modifier, Ctrl, Alt and Shift are all already
        // spoken for by the four above, and the remaining combinations (Ctrl+Shift, Ctrl+Alt, …) are
        // worth less than the chance of stamping on something the user's other tools have claimed.
        // Sharing a region is also a deliberate, unhurried act — you are setting up a meeting — so
        // it does not need a reflex key the way taking a screenshot does. The action is on the tray
        // menu, and the Hotkeys settings page lets anyone who wants one bind it.
        private GestureList _shareRegionShortcut = GestureList.Empty;

        // No default either, for the same reason: the PrintScreen family is taken, and anything
        // else risks colliding with the user's other tools. The tray menu has the action.
        private GestureList _drawOnScreenShortcut = GestureList.Empty;

        // No default, for the same reason. Unlike the rest it has no tray item either: a screenshot
        // taken from the menu would have to wait out the menu's own fade.
        private GestureList _drawOnScreenshotShortcut = GestureList.Empty;

        private GestureList _colorWheelShortcut = GestureList.Empty;
        private GestureList _newImageEditorShortcut = GestureList.Empty;
        private GestureList _newVideoEditorShortcut = GestureList.Empty;
    }
}
