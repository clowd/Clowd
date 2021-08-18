using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;
using RT.Util;

namespace Clowd.Config
{

    public class SettingsHotkey : SettingsCategoryBase
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

        private GlobalTrigger _fileUploadShortcut = new GlobalTrigger();
        private GlobalTrigger _captureRegionShortcut = new GlobalTrigger(Key.PrintScreen);
        private GlobalTrigger _captureFullscreenShortcut = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Control);
        private GlobalTrigger _captureActiveShortcut = new GlobalTrigger(Key.PrintScreen, ModifierKeys.Alt);

        public SettingsHotkey()
        {
            Subscribe(FileUploadShortcut, CaptureRegionShortcut, CaptureFullscreenShortcut, CaptureActiveShortcut);
        }

        protected override void DisposeInternal()
        {
            FileUploadShortcut?.Dispose();
            CaptureRegionShortcut?.Dispose();
            CaptureFullscreenShortcut?.Dispose();
            CaptureActiveShortcut?.Dispose();
        }
    }
}
