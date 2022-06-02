using System.ComponentModel;
using System.Windows.Input;
using RT.Serialization;

namespace Clowd.Config
{
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
        private GlobalTrigger _captureRegionShortcut = new(Key.PrintScreen);
        private GlobalTrigger _captureFullscreenShortcut = new(Key.PrintScreen, ModifierKeys.Control);
        private GlobalTrigger _captureActiveShortcut = new(Key.PrintScreen, ModifierKeys.Alt);
        private GlobalTrigger _drawOnScreenShortcut = new(Key.PrintScreen, ModifierKeys.Control | ModifierKeys.Shift);
        private GlobalTrigger _startStopRecordingShortcut = new(Key.PrintScreen, ModifierKeys.Shift);

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
