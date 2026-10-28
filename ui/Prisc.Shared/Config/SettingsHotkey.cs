using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace Prisc.Config
{
    /// <summary>
    /// Persisted hotkey gestures — pure data. OS registration, live status and the
    /// pause-while-editing behavior are owned entirely by the UI layer (HotkeyManager in
    /// Prisc.Ui); this class never references any hotkey backend.
    /// <para>Each action holds an ordered <see cref="GestureList"/>, possibly empty; see that type
    /// for why it is not a plain collection. The properties never return null — assigning null
    /// clears the action. The Hotkeys page groups and orders the rows itself (HotkeyPageLayout in
    /// Prisc.Ui); it takes each row's name from [DisplayName] and its visibility from [VisibleWhen].</para>
    /// </summary>
    public class SettingsHotkey : SimpleNotifyObject
    {
        /// <summary>This OS's defaults — the constructor the settings binder uses, so a missing
        /// file or value falls back to them.</summary>
        public SettingsHotkey() : this(HotkeyGesture.CurrentPlatform) { }

        /// <summary>The defaults as they are on <paramref name="platform"/>, so each OS's set can
        /// be tested on any one. Only the screenshot actions have defaults: the PrintScreen family
        /// on Windows (and anywhere else). A Mac has no PrintScreen key code, so there region is
        /// Cmd+Shift+2 — first, as the one the capture page's caption names — and F13, which sits
        /// where PrintScreen does on a PC keyboard plugged into a Mac; the active screen is
        /// Cmd+Shift+1. The active window and recording have none there: macOS takes Cmd+Shift+3/4/5
        /// itself, and anything further risks colliding with the user's other tools.</summary>
        public SettingsHotkey(OSPlatform platform)
        {
            if (platform == OSPlatform.OSX)
            {
                const HotkeyModifiers CmdShift = HotkeyModifiers.Meta | HotkeyModifiers.Shift;
                _captureRegionShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.D2, CmdShift), new HotkeyGesture(HotkeyKey.F13));
                _captureFullscreenShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.D1, CmdShift));
            }
            else
            {
                _captureRegionShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.PrintScreen));
                _captureFullscreenShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.PrintScreen, HotkeyModifiers.Control));
                _captureActiveShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.PrintScreen, HotkeyModifiers.Alt));
                _startStopRecordingShortcut = GestureList.Of(new HotkeyGesture(HotkeyKey.PrintScreen, HotkeyModifiers.Shift));
            }
        }

        /// <summary>Which defaults the file was written against, so a change to them can be
        /// carried into a file that still holds the old ones (see
        /// <see cref="MigrateDefaults"/>). Always this build's: a file is written as it now is. A
        /// file that has no value predates per-OS defaults.</summary>
        [Browsable(false)]
        public int DefaultsVersion => CurrentDefaultsVersion;

        /// <summary>1: the screenshot defaults depend on the OS (before, every OS got the
        /// PrintScreen family).</summary>
        internal const int CurrentDefaultsVersion = 1;

        /// <summary>
        /// Brings forward a file written against <paramref name="writtenWith"/>'s defaults (0: none
        /// recorded). Only a Mac has anything to do: there every build before per-OS defaults wrote
        /// out the PrintScreen family, which a Mac cannot register — so each screenshot action
        /// still holding exactly its old default gets this OS's default instead. A gesture the user
        /// chose is left alone, as is everything on any other OS. A gesture belongs to one action
        /// (HotkeyManager enforces that only on an edit), so a new default another action already
        /// holds stays with it: the user bound it there while the old defaults were dead.
        /// </summary>
        public void MigrateDefaults(int writtenWith, OSPlatform platform)
        {
            if (writtenWith >= 1 || platform != OSPlatform.OSX)
                return;

            var old = new SettingsHotkey(OSPlatform.Windows);
            var now = new SettingsHotkey(platform);
            var actions = TypeDescriptor.GetProperties(this).Cast<PropertyDescriptor>()
                .Where(p => p.PropertyType == typeof(GestureList) && !p.IsReadOnly)
                .ToList();
            var moving = actions.Where(p => Equals(p.GetValue(this), p.GetValue(old))).ToList();
            var taken = new HashSet<HotkeyGesture>(actions.Except(moving).SelectMany(p => (GestureList)p.GetValue(this)));
            foreach (var action in moving)
            {
                var gestures = ((GestureList)action.GetValue(now)).Except(taken);
                action.SetValue(this, gestures);
                taken.UnionWith(gestures);
            }
        }

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
        // the screenshot actions' defaults depend on the OS, so the constructor sets them
        private GestureList _captureRegionShortcut = GestureList.Empty;
        private GestureList _captureFullscreenShortcut = GestureList.Empty;
        private GestureList _captureActiveShortcut = GestureList.Empty;
        private GestureList _startStopRecordingShortcut = GestureList.Empty;

        // No default, on purpose: the reflex keys are spoken for — on Windows PrintScreen with no
        // modifier, Ctrl, Alt and Shift by the four above, on a Mac Cmd+Shift+1/2 by the two above
        // and Cmd+Shift+3/4/5 by macOS — and the remaining combinations (Ctrl+Shift+PrtScr,
        // Cmd+Shift+6, …) are worth less than the chance of stamping on something the user's other
        // tools have claimed.
        // Sharing a region is also a deliberate, unhurried act — you are setting up a meeting — so
        // it does not need a reflex key the way taking a screenshot does. The action is on the tray
        // menu, and the Hotkeys settings page lets anyone who wants one bind it.
        private GestureList _shareRegionShortcut = GestureList.Empty;

        // No default either, for the same reason: the screenshot keys are taken, and anything
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
