using System.ComponentModel;
using System.Windows.Input;
using RT.Serialization;

namespace Clowd.Config
{
    public class SettingsHotkey : CategoryBase
    {
        [DisplayName("General - File Upload"), ClassifyIgnoreIfDefault]
        public GlobalTrigger FileUploadShortcut
        {
            get => _fileUploadShortcut;
            set => Set(ref _fileUploadShortcut, value);
        }

        [DisplayName("Capture - Region"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureRegionShortcut
        {
            get => _captureRegionShortcut;
            set => Set(ref _captureRegionShortcut, value);
        }

        [DisplayName("Capture - Fullscreen"), ClassifyIgnoreIfDefault]
        public GlobalTrigger CaptureFullscreenShortcut
        {
            get => _captureFullscreenShortcut;
            set => Set(ref _captureFullscreenShortcut, value);
        }

        [DisplayName("Capture - Active Window"), ClassifyIgnoreIfDefault]
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

        private GlobalTrigger _fileUploadShortcut = new();
        private GlobalTrigger _captureRegionShortcut = new(Key.PrintScreen);
        private GlobalTrigger _captureFullscreenShortcut = new(Key.PrintScreen, ModifierKeys.Control);
        private GlobalTrigger _captureActiveShortcut = new(Key.PrintScreen, ModifierKeys.Alt);
        private GlobalTrigger _drawOnScreenShortcut = new(Key.PrintScreen, ModifierKeys.Control | ModifierKeys.Shift);

        public SettingsHotkey()
        {
            Subscribe(FileUploadShortcut, CaptureRegionShortcut, CaptureFullscreenShortcut, CaptureActiveShortcut, DrawOnScreenShortcut);
        }

        protected override void DisposeInternal()
        {
            FileUploadShortcut?.Dispose();
            CaptureRegionShortcut?.Dispose();
            CaptureFullscreenShortcut?.Dispose();
            CaptureActiveShortcut?.Dispose();
            DrawOnScreenShortcut?.Dispose();
        }
    }
}
